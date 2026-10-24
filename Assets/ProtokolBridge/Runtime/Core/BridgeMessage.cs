using System;
using System.Collections.Generic;
using System.Text;

namespace ProtokolBridge
{
    public enum MessageDirection
    {
        Inbound,
        Outbound
    }

    /// <summary>
    /// The single envelope every transport speaks. A serial line, a UDP datagram,
    /// a TCP frame and an OSC message all arrive here in the same shape so the
    /// routing engine never has to care where data came from.
    /// </summary>
    public sealed class BridgeMessage
    {
        /// <summary>Id of the endpoint that produced (inbound) or will carry (outbound) this message.</summary>
        public string EndpointId;

        public MessageDirection Direction;

        public DateTime TimestampUtc;

        /// <summary>OSC address pattern (e.g. "/sensor/temp"). Empty for raw transports.</summary>
        public string Address;

        /// <summary>Raw bytes as they went over the wire. Always populated.</summary>
        public byte[] Raw;

        /// <summary>Human-readable view: the serial line, or the decoded text of a datagram.</summary>
        public string Text;

        /// <summary>Typed arguments. OSC fills these from its type tags; raw transports leave it empty.</summary>
        public readonly List<object> Args = new List<object>();

        /// <summary>"192.168.1.40:9000" for sockets, "COM4" for serial.</summary>
        public string Remote;

        public static BridgeMessage FromText(string endpointId, string text, string remote = null)
        {
            return new BridgeMessage
            {
                EndpointId = endpointId,
                Direction = MessageDirection.Inbound,
                TimestampUtc = DateTime.UtcNow,
                Address = string.Empty,
                Text = text ?? string.Empty,
                Raw = Encoding.UTF8.GetBytes(text ?? string.Empty),
                Remote = remote
            };
        }

        public static BridgeMessage FromBytes(string endpointId, byte[] raw, string remote = null)
        {
            return new BridgeMessage
            {
                EndpointId = endpointId,
                Direction = MessageDirection.Inbound,
                TimestampUtc = DateTime.UtcNow,
                Address = string.Empty,
                Raw = raw ?? Array.Empty<byte>(),
                Text = SafeText(raw),
                Remote = remote
            };
        }

        /// <summary>Argument at <paramref name="index"/> as a string, or empty when out of range.</summary>
        public string ArgAsString(int index)
        {
            if (index < 0 || index >= Args.Count) return string.Empty;
            var a = Args[index];
            return a == null ? string.Empty : Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture);
        }

        public bool TryArgAsFloat(int index, out float value)
        {
            value = 0f;
            if (index < 0 || index >= Args.Count) return false;
            try
            {
                value = Convert.ToSingle(Args[index], System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Decodes bytes as UTF-8, replacing control characters so the log stays readable.</summary>
        public static string SafeText(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return string.Empty;
            var sb = new StringBuilder(raw.Length);
            var decoded = Encoding.UTF8.GetString(raw);
            foreach (var c in decoded)
            {
                if (c == '\r' || c == '\n' || c == '\t') sb.Append(' ');
                else if (char.IsControl(c)) sb.Append('.');
                else sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        public string ToHex(int maxBytes = 32)
        {
            if (Raw == null || Raw.Length == 0) return string.Empty;
            var count = Math.Min(Raw.Length, maxBytes);
            var sb = new StringBuilder(count * 3);
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Raw[i].ToString("X2"));
            }
            if (Raw.Length > count) sb.Append(" ...");
            return sb.ToString();
        }

        public string Describe()
        {
            if (!string.IsNullOrEmpty(Address))
            {
                var sb = new StringBuilder();
                sb.Append(Address);
                for (var i = 0; i < Args.Count; i++)
                {
                    sb.Append(' ');
                    sb.Append(ArgAsString(i));
                }
                return sb.ToString();
            }
            return string.IsNullOrEmpty(Text) ? ToHex() : Text;
        }
    }
}
