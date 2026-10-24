using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ProtokolBridge
{
    /// <summary>
    /// Outgoing TCP connection with automatic reconnect. A server that is not up yet
    /// must not permanently fault the endpoint, so connection failures are retried
    /// on a backoff instead of being thrown.
    /// </summary>
    public sealed class TcpClientTransport : TransportBase
    {
        private const double RetrySeconds = 2.0;

        private readonly EndpointConfig _config;
        private readonly StreamFramer _framer;
        private readonly List<string> _lines = new List<string>(8);
        private readonly byte[] _readBuffer = new byte[8192];
        private readonly string _terminator;

        private TcpClient _client;
        private NetworkStream _stream;
        private DateTime _nextAttemptUtc = DateTime.MinValue;

        public TcpClientTransport(EndpointConfig config) : base(config.id, config.name)
        {
            _config = config;
            _terminator = config.ResolvedTerminator();
            _framer = new StreamFramer(_terminator);
        }

        public override TransportKind Kind { get { return TransportKind.TcpClient; } }

        public bool IsConnected
        {
            get { return _client != null && _client.Connected; }
        }

        protected override void OpenConnection()
        {
            TryConnect();
        }

        private void TryConnect()
        {
            DropConnection();

            try
            {
                _client = new TcpClient { NoDelay = true };
                _client.Connect(_config.remoteHost, _config.remotePort);
                _stream = _client.GetStream();
                _framer.Reset();
                SetError(string.Empty);
            }
            catch (Exception ex)
            {
                DropConnection();
                SetError("Not connected: " + ex.Message);
                _nextAttemptUtc = DateTime.UtcNow.AddSeconds(RetrySeconds);
            }
        }

        protected override void PumpOnce()
        {
            if (!IsConnected)
            {
                if (DateTime.UtcNow >= _nextAttemptUtc) TryConnect();
                else Thread.Sleep(50);
                return;
            }

            if (!_stream.DataAvailable)
            {
                // Detect a half-open socket: readable with zero bytes means the peer closed.
                if (_client.Client.Poll(2000, SelectMode.SelectRead) && _client.Available == 0)
                {
                    SetError("Peer closed the connection");
                    DropConnection();
                    _nextAttemptUtc = DateTime.UtcNow.AddSeconds(RetrySeconds);
                }
                return;
            }

            var count = _stream.Read(_readBuffer, 0, _readBuffer.Length);
            if (count <= 0)
            {
                DropConnection();
                _nextAttemptUtc = DateTime.UtcNow.AddSeconds(RetrySeconds);
                return;
            }

            var remote = _config.remoteHost + ":" + _config.remotePort;

            if (!_config.useLineFraming)
            {
                var chunk = new byte[count];
                Buffer.BlockCopy(_readBuffer, 0, chunk, 0, count);
                Publish(BridgeMessage.FromBytes(Id, chunk, remote));
                return;
            }

            _lines.Clear();
            _framer.Append(_readBuffer, count, _lines);
            foreach (var line in _lines) Publish(BridgeMessage.FromText(Id, line, remote));
        }

        protected override void WriteMessage(BridgeMessage message)
        {
            if (!IsConnected) return;

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

            _stream.Write(payload, 0, payload.Length);
            _stream.Flush();
        }

        private void DropConnection()
        {
            if (_stream != null)
            {
                try { _stream.Close(); } catch { /* already gone */ }
                _stream = null;
            }
            if (_client != null)
            {
                try { _client.Close(); } catch { /* already gone */ }
                _client = null;
            }
        }

        protected override void CloseConnection()
        {
            DropConnection();
        }
    }
}
