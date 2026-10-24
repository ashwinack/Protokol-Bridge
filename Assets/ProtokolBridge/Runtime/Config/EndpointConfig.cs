using System;

namespace ProtokolBridge
{
    /// <summary>
    /// Everything the UI lets an operator set for one connection. Kept as a flat
    /// serializable class so Unity's JsonUtility can round-trip it without custom
    /// converters; fields that do not apply to a given <see cref="kind"/> are ignored.
    /// </summary>
    [Serializable]
    public class EndpointConfig
    {
        public string id = Guid.NewGuid().ToString("N");
        public string name = "New Endpoint";
        public TransportKind kind = TransportKind.Osc;
        public bool autoStart = true;

        // --- Serial -------------------------------------------------------
        public string portName = "COM3";
        public int baudRate = 115200;
        public int dataBits = 8;
        /// <summary>None, Odd, Even, Mark, Space.</summary>
        public string parity = "None";
        /// <summary>One, Two, OnePointFive.</summary>
        public string stopBits = "One";

        // --- Framing (serial + TCP) ---------------------------------------
        /// <summary>Split the incoming stream into messages on <see cref="lineTerminator"/>.</summary>
        public bool useLineFraming = true;
        /// <summary>Escape sequences \n, \r and \t are expanded.</summary>
        public string lineTerminator = "\\n";
        /// <summary>Appended to every outgoing message when line framing is on.</summary>
        public bool appendTerminatorOnSend = true;

        // --- Sockets ------------------------------------------------------
        public string remoteHost = "127.0.0.1";
        public int remotePort = 9000;
        public int listenPort = 8000;
        public bool enableBroadcast;

        /// <summary>Turns the literal "\n" typed in the UI into a real newline.</summary>
        public string ResolvedTerminator()
        {
            var t = lineTerminator ?? string.Empty;
            return t.Replace("\\r\\n", "\r\n").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        }

        public EndpointConfig Clone()
        {
            return (EndpointConfig)MemberwiseClone();
        }

        public string Summary()
        {
            switch (kind)
            {
                case TransportKind.Serial:
                    return string.Format("{0} @ {1} {2}{3}{4}", portName, baudRate, dataBits, ParityLetter(), StopBitsDigit());
                case TransportKind.Udp:
                case TransportKind.Osc:
                    return string.Format("in :{0}  ->  out {1}:{2}", listenPort, remoteHost, remotePort);
                case TransportKind.TcpClient:
                    return string.Format("connect to {0}:{1}", remoteHost, remotePort);
                case TransportKind.TcpServer:
                    return string.Format("listen on :{0}", listenPort);
                default:
                    return string.Empty;
            }
        }

        private string ParityLetter()
        {
            if (string.IsNullOrEmpty(parity)) return "N";
            return parity.Substring(0, 1).ToUpperInvariant();
        }

        private string StopBitsDigit()
        {
            switch (stopBits)
            {
                case "Two": return "2";
                case "OnePointFive": return "1.5";
                default: return "1";
            }
        }
    }
}
