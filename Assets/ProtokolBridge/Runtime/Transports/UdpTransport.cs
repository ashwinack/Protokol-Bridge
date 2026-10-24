using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ProtokolBridge
{
    /// <summary>
    /// Raw UDP datagrams. One socket both listens on <c>listenPort</c> and sends to
    /// <c>remoteHost:remotePort</c>, so replies come back from the port peers expect.
    /// Set <c>listenPort</c> to 0 for a send-only endpoint.
    /// </summary>
    public class UdpTransport : TransportBase
    {
        protected readonly EndpointConfig Config;
        private UdpClient _client;
        private IPEndPoint _target;

        public UdpTransport(EndpointConfig config) : base(config.id, config.name)
        {
            Config = config;
        }

        public override TransportKind Kind { get { return TransportKind.Udp; } }

        protected override void OpenConnection()
        {
            _client = Config.listenPort > 0
                ? new UdpClient(new IPEndPoint(IPAddress.Any, Config.listenPort))
                : new UdpClient();

            _client.EnableBroadcast = Config.enableBroadcast;

            // Without this, a peer that has gone away makes the next Receive throw
            // ConnectionReset on Windows and kills the endpoint.
            try { _client.Client.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null); }
            catch { /* not supported on this platform; harmless */ }

            _target = ResolveTarget(Config.remoteHost, Config.remotePort);
        }

        protected override void PumpOnce()
        {
            if (_client == null)
            {
                Thread.Sleep(20);
                return;
            }

            var handled = false;
            while (_client.Available > 0)
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try { data = _client.Receive(ref from); }
                catch (SocketException) { break; }

                handled = true;
                OnDatagram(data, from);
            }

            if (!handled) Thread.Sleep(2);
        }

        /// <summary>Overridden by <see cref="OscTransport"/> to parse the payload.</summary>
        protected virtual void OnDatagram(byte[] data, IPEndPoint from)
        {
            Publish(BridgeMessage.FromBytes(Id, data, from.ToString()));
        }

        protected override void WriteMessage(BridgeMessage message)
        {
            if (_client == null || _target == null) return;
            var payload = BuildPayload(message);
            if (payload == null || payload.Length == 0) return;
            _client.Send(payload, payload.Length, _target);
        }

        protected virtual byte[] BuildPayload(BridgeMessage message)
        {
            if (!string.IsNullOrEmpty(message.Text)) return Encoding.UTF8.GetBytes(message.Text);
            return message.Raw;
        }

        protected override void CloseConnection()
        {
            if (_client == null) return;
            try { _client.Close(); }
            finally
            {
                _client = null;
            }
        }

        protected override void AbortPendingReads()
        {
            var client = _client;
            if (client != null)
            {
                try { client.Client.Shutdown(SocketShutdown.Both); } catch { /* already closed */ }
            }
        }

        protected static IPEndPoint ResolveTarget(string host, int port)
        {
            if (string.IsNullOrEmpty(host)) return null;

            IPAddress address;
            if (!IPAddress.TryParse(host, out address))
            {
                var entries = Dns.GetHostAddresses(host);
                foreach (var candidate in entries)
                {
                    if (candidate.AddressFamily == AddressFamily.InterNetwork)
                    {
                        address = candidate;
                        break;
                    }
                }
                if (address == null) throw new SocketException((int)SocketError.HostNotFound);
            }

            return new IPEndPoint(address, port);
        }
    }
}
