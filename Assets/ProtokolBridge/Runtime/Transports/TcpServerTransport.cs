using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ProtokolBridge
{
    /// <summary>
    /// Listening TCP endpoint. Accepts many simultaneous clients; an outbound message
    /// is broadcast to every one of them. Clients are polled from the single worker
    /// thread rather than given a thread each, which keeps shutdown deterministic.
    /// </summary>
    public sealed class TcpServerTransport : TransportBase
    {
        private sealed class Peer
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public StreamFramer Framer;
            public string Remote;
        }

        private readonly EndpointConfig _config;
        private readonly string _terminator;
        private readonly List<Peer> _peers = new List<Peer>();
        private readonly List<string> _lines = new List<string>(8);
        private readonly byte[] _readBuffer = new byte[8192];
        private TcpListener _listener;

        public TcpServerTransport(EndpointConfig config) : base(config.id, config.name)
        {
            _config = config;
            _terminator = config.ResolvedTerminator();
        }

        public override TransportKind Kind { get { return TransportKind.TcpServer; } }

        public int ConnectedCount
        {
            get { lock (_peers) return _peers.Count; }
        }

        protected override void OpenConnection()
        {
            _listener = new TcpListener(IPAddress.Any, _config.listenPort);
            _listener.Start();
        }

        protected override void PumpOnce()
        {
            if (_listener == null)
            {
                Thread.Sleep(20);
                return;
            }

            AcceptPending();
            var didWork = ReadFromPeers();
            if (!didWork) Thread.Sleep(5);
        }

        private void AcceptPending()
        {
            while (_listener.Pending())
            {
                var client = _listener.AcceptTcpClient();
                client.NoDelay = true;

                var peer = new Peer
                {
                    Client = client,
                    Stream = client.GetStream(),
                    Framer = new StreamFramer(_terminator),
                    Remote = client.Client.RemoteEndPoint != null ? client.Client.RemoteEndPoint.ToString() : "?"
                };

                lock (_peers) _peers.Add(peer);
                SetError(string.Empty);
            }
        }

        private bool ReadFromPeers()
        {
            List<Peer> snapshot;
            lock (_peers) snapshot = new List<Peer>(_peers);

            var didWork = false;

            foreach (var peer in snapshot)
            {
                try
                {
                    if (!peer.Client.Connected)
                    {
                        RemovePeer(peer);
                        continue;
                    }

                    if (!peer.Stream.DataAvailable)
                    {
                        if (peer.Client.Client.Poll(0, SelectMode.SelectRead) && peer.Client.Available == 0)
                        {
                            RemovePeer(peer);
                        }
                        continue;
                    }

                    var count = peer.Stream.Read(_readBuffer, 0, _readBuffer.Length);
                    if (count <= 0)
                    {
                        RemovePeer(peer);
                        continue;
                    }

                    didWork = true;

                    if (!_config.useLineFraming)
                    {
                        var chunk = new byte[count];
                        Buffer.BlockCopy(_readBuffer, 0, chunk, 0, count);
                        Publish(BridgeMessage.FromBytes(Id, chunk, peer.Remote));
                        continue;
                    }

                    _lines.Clear();
                    peer.Framer.Append(_readBuffer, count, _lines);
                    foreach (var line in _lines) Publish(BridgeMessage.FromText(Id, line, peer.Remote));
                }
                catch (Exception ex)
                {
                    SetError(ex.Message);
                    RemovePeer(peer);
                }
            }

            return didWork;
        }

        private void RemovePeer(Peer peer)
        {
            lock (_peers) _peers.Remove(peer);
            try { peer.Stream.Close(); } catch { /* already gone */ }
            try { peer.Client.Close(); } catch { /* already gone */ }
        }

        protected override void WriteMessage(BridgeMessage message)
        {
            byte[] payload;
            if (message.Raw != null && message.Raw.Length > 0 && string.IsNullOrEmpty(message.Text))
            {
                payload = message.Raw;
            }
            else
            {
                var text = message.Text ?? string.Empty;
                if (_config.useLineFraming && _config.appendTerminatorOnSend) text += _terminator;
                payload = Encoding.UTF8.GetBytes(text);
            }

            if (payload == null || payload.Length == 0) return;

            List<Peer> snapshot;
            lock (_peers) snapshot = new List<Peer>(_peers);

            foreach (var peer in snapshot)
            {
                try
                {
                    peer.Stream.Write(payload, 0, payload.Length);
                    peer.Stream.Flush();
                }
                catch
                {
                    RemovePeer(peer);
                }
            }
        }

        protected override void CloseConnection()
        {
            List<Peer> snapshot;
            lock (_peers)
            {
                snapshot = new List<Peer>(_peers);
                _peers.Clear();
            }

            foreach (var peer in snapshot)
            {
                try { peer.Stream.Close(); } catch { /* already gone */ }
                try { peer.Client.Close(); } catch { /* already gone */ }
            }

            if (_listener == null) return;
            try { _listener.Stop(); }
            finally { _listener = null; }
        }
    }
}
