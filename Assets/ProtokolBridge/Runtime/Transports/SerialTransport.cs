using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace ProtokolBridge
{
    /// <summary>
    /// COM-port transport for Arduino/ESP32/RS-485 style devices.
    /// Requires the project's API Compatibility Level to be .NET Framework,
    /// because System.IO.Ports is absent from the .NET Standard profile.
    /// </summary>
    public sealed class SerialTransport : TransportBase
    {
        private readonly EndpointConfig _config;
        private readonly string _terminator;
        private readonly StringBuilder _lineBuffer = new StringBuilder(256);
        private readonly byte[] _readBuffer = new byte[4096];
        private SerialPort _port;

        public SerialTransport(EndpointConfig config) : base(config.id, config.name)
        {
            _config = config;
            _terminator = config.ResolvedTerminator();
            if (string.IsNullOrEmpty(_terminator)) _terminator = "\n";
        }

        public override TransportKind Kind { get { return TransportKind.Serial; } }

        /// <summary>COM ports currently present on this machine, for the port dropdown.</summary>
        public static string[] AvailablePorts()
        {
            try { return SerialPort.GetPortNames(); }
            catch { return Array.Empty<string>(); }
        }

        protected override void OpenConnection()
        {
            _port = new SerialPort(_config.portName, _config.baudRate)
            {
                DataBits = _config.dataBits,
                Parity = ParseEnum(_config.parity, Parity.None),
                StopBits = ParseEnum(_config.stopBits, StopBits.One),
                Handshake = Handshake.None,
                ReadTimeout = 50,
                WriteTimeout = 500,
                DtrEnable = true,
                RtsEnable = true
            };

            _port.Open();
            _lineBuffer.Length = 0;
        }

        protected override void PumpOnce()
        {
            if (_port == null || !_port.IsOpen)
            {
                Thread.Sleep(20);
                return;
            }

            int available;
            try { available = _port.BytesToRead; }
            catch (InvalidOperationException) { return; }

            if (available <= 0)
            {
                Thread.Sleep(5);
                return;
            }

            var count = _port.Read(_readBuffer, 0, Math.Min(available, _readBuffer.Length));
            if (count <= 0) return;

            if (!_config.useLineFraming)
            {
                var chunk = new byte[count];
                Buffer.BlockCopy(_readBuffer, 0, chunk, 0, count);
                Publish(BridgeMessage.FromBytes(Id, chunk, _config.portName));
                return;
            }

            _lineBuffer.Append(Encoding.UTF8.GetString(_readBuffer, 0, count));
            ExtractLines();
        }

        private void ExtractLines()
        {
            while (true)
            {
                var text = _lineBuffer.ToString();
                var index = text.IndexOf(_terminator, StringComparison.Ordinal);
                if (index < 0)
                {
                    // Guard against a device that never sends a terminator.
                    if (_lineBuffer.Length > 65536) _lineBuffer.Length = 0;
                    return;
                }

                var line = text.Substring(0, index).Trim('\r', '\n', ' ');
                _lineBuffer.Remove(0, index + _terminator.Length);

                if (line.Length > 0) Publish(BridgeMessage.FromText(Id, line, _config.portName));
            }
        }

        protected override void WriteMessage(BridgeMessage message)
        {
            if (_port == null || !_port.IsOpen) return;

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

            _port.Write(payload, 0, payload.Length);
        }

        protected override void CloseConnection()
        {
            if (_port == null) return;
            try { if (_port.IsOpen) _port.Close(); }
            finally
            {
                _port.Dispose();
                _port = null;
            }
        }

        protected override void AbortPendingReads()
        {
            // Closing here would race the worker's own Close; the 50 ms read timeout
            // is short enough that the worker notices cancellation on its own.
        }

        private static T ParseEnum<T>(string value, T fallback) where T : struct
        {
            T parsed;
            return Enum.TryParse(value, true, out parsed) ? parsed : fallback;
        }
    }
}
