using System;
using System.Collections.Generic;
using System.Text;

namespace ProtokolBridge
{
    /// <summary>
    /// Turns a byte stream into discrete messages by splitting on a terminator.
    /// TCP has no message boundaries of its own, so anything text-oriented needs this.
    /// </summary>
    internal sealed class StreamFramer
    {
        private const int MaxBufferedChars = 1 << 16;

        private readonly StringBuilder _buffer = new StringBuilder(512);
        private readonly string _terminator;

        public StreamFramer(string terminator)
        {
            _terminator = string.IsNullOrEmpty(terminator) ? "\n" : terminator;
        }

        public void Reset()
        {
            _buffer.Length = 0;
        }

        /// <summary>Appends a chunk and adds every complete message to <paramref name="lines"/>.</summary>
        public void Append(byte[] data, int count, List<string> lines)
        {
            if (data == null || count <= 0) return;

            _buffer.Append(Encoding.UTF8.GetString(data, 0, count));

            while (true)
            {
                var text = _buffer.ToString();
                var index = text.IndexOf(_terminator, StringComparison.Ordinal);
                if (index < 0)
                {
                    // A peer that never sends a terminator must not grow this forever.
                    if (_buffer.Length > MaxBufferedChars) _buffer.Length = 0;
                    return;
                }

                var line = text.Substring(0, index).Trim('\r', '\n', ' ');
                _buffer.Remove(0, index + _terminator.Length);

                if (line.Length > 0) lines.Add(line);
            }
        }
    }
}
