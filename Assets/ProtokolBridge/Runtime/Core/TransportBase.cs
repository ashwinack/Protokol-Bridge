using System;
using System.Collections.Concurrent;
using System.Threading;

namespace ProtokolBridge
{
    /// <summary>
    /// Shared plumbing for every transport: a worker thread, a cancellation flag,
    /// an outbound queue and state/error bookkeeping. Subclasses implement
    /// <see cref="OpenConnection"/>, <see cref="PumpOnce"/>, <see cref="WriteMessage"/>
    /// and <see cref="CloseConnection"/> and never worry about threading again.
    /// </summary>
    public abstract class TransportBase : ITransport
    {
        private Thread _worker;
        private volatile bool _cancel;
        private volatile TransportState _state = TransportState.Stopped;
        private string _lastError = string.Empty;
        private readonly object _errorLock = new object();

        protected readonly ConcurrentQueue<BridgeMessage> OutboundQueue = new ConcurrentQueue<BridgeMessage>();

        protected TransportBase(string id, string displayName)
        {
            Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id;
            DisplayName = displayName;
        }

        public string Id { get; private set; }
        public string DisplayName { get; protected set; }
        public abstract TransportKind Kind { get; }
        public TransportState State { get { return _state; } }

        public string LastError
        {
            get { lock (_errorLock) return _lastError; }
        }

        public event Action<BridgeMessage> MessageReceived;

        /// <summary>True once <see cref="Stop"/> has been requested; long loops must poll this.</summary>
        protected bool IsCancelled { get { return _cancel; } }

        public void Start()
        {
            if (_worker != null && _worker.IsAlive) return;

            _cancel = false;
            SetError(string.Empty);
            _state = TransportState.Connecting;

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "ProtokolBridge:" + DisplayName
            };
            _worker.Start();
        }

        public void Stop()
        {
            _cancel = true;

            // Wake the worker if it is parked in a blocking read.
            try { AbortPendingReads(); }
            catch (Exception ex) { SetError(ex.Message); }

            var worker = _worker;
            _worker = null;
            if (worker != null && worker.IsAlive)
            {
                if (!worker.Join(1500)) worker.Interrupt();
            }

            _state = TransportState.Stopped;
        }

        public bool Send(BridgeMessage message)
        {
            if (message == null) return false;
            if (_state != TransportState.Running) return false;

            message.Direction = MessageDirection.Outbound;
            message.EndpointId = Id;
            if (message.TimestampUtc == default(DateTime)) message.TimestampUtc = DateTime.UtcNow;

            OutboundQueue.Enqueue(message);
            return true;
        }

        private void WorkerLoop()
        {
            try
            {
                OpenConnection();
                _state = TransportState.Running;
            }
            catch (Exception ex)
            {
                Fault(ex);
                return;
            }

            while (!_cancel)
            {
                try
                {
                    DrainOutbound();
                    PumpOnce();
                }
                catch (ThreadInterruptedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_cancel) break;
                    Fault(ex);
                    return;
                }
            }

            try { CloseConnection(); }
            catch (Exception ex) { SetError(ex.Message); }

            _state = TransportState.Stopped;
        }

        private void DrainOutbound()
        {
            BridgeMessage pending;
            while (OutboundQueue.TryDequeue(out pending))
            {
                if (_cancel) return;
                WriteMessage(pending);
            }
        }

        /// <summary>Publishes an inbound message. Call from the worker thread only.</summary>
        protected void Publish(BridgeMessage message)
        {
            if (message == null) return;
            message.EndpointId = Id;
            message.Direction = MessageDirection.Inbound;
            if (message.TimestampUtc == default(DateTime)) message.TimestampUtc = DateTime.UtcNow;

            var handler = MessageReceived;
            if (handler != null) handler(message);
        }

        protected void SetError(string message)
        {
            lock (_errorLock) _lastError = message ?? string.Empty;
        }

        private void Fault(Exception ex)
        {
            SetError(ex.Message);
            _state = TransportState.Faulted;
            try { CloseConnection(); } catch { /* already failing; nothing useful to add */ }
        }

        /// <summary>Open sockets/ports here. Throwing puts the endpoint into <see cref="TransportState.Faulted"/>.</summary>
        protected abstract void OpenConnection();

        /// <summary>One slice of receive work. Must return promptly (block for at most ~100 ms).</summary>
        protected abstract void PumpOnce();

        protected abstract void WriteMessage(BridgeMessage message);

        protected abstract void CloseConnection();

        /// <summary>Optional hook to unblock a blocking read during <see cref="Stop"/>.</summary>
        protected virtual void AbortPendingReads() { }

        public void Dispose()
        {
            Stop();
        }
    }
}
