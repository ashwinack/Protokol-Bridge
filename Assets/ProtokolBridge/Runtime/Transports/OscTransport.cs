using System.Collections.Generic;
using System.Net;

namespace ProtokolBridge
{
    /// <summary>
    /// OSC over UDP. Inherits the socket handling from <see cref="UdpTransport"/> and
    /// only swaps the payload codec, so a bundle arriving in one datagram fans out
    /// into several <see cref="BridgeMessage"/> instances.
    /// </summary>
    public sealed class OscTransport : UdpTransport
    {
        private readonly List<OscParsed> _scratch = new List<OscParsed>(8);

        public OscTransport(EndpointConfig config) : base(config) { }

        public override TransportKind Kind { get { return TransportKind.Osc; } }

        protected override void OnDatagram(byte[] data, IPEndPoint from)
        {
            _scratch.Clear();

            if (!OscCodec.TryDecode(data, _scratch) || _scratch.Count == 0)
            {
                // Not valid OSC: surface the bytes rather than silently dropping them,
                // so a misconfigured sender is visible in the traffic log.
                SetError("Received a datagram that is not valid OSC (" + data.Length + " bytes)");
                Publish(BridgeMessage.FromBytes(Id, data, from.ToString()));
                return;
            }

            SetError(string.Empty);

            foreach (var parsed in _scratch)
            {
                var message = new BridgeMessage
                {
                    Address = parsed.Address,
                    Raw = data,
                    Remote = from.ToString(),
                    Text = string.Empty
                };
                message.Args.AddRange(parsed.Args);
                message.Text = message.Describe();
                Publish(message);
            }
        }

        protected override byte[] BuildPayload(BridgeMessage message)
        {
            // A rule produced an address + args: encode properly.
            if (!string.IsNullOrEmpty(message.Address)) return OscCodec.Encode(message.Address, message.Args);

            // Already-encoded bytes pass straight through.
            if (message.Raw != null && message.Raw.Length > 0) return message.Raw;

            return null;
        }
    }
}
