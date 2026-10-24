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
        /// <remarks>
        /// This used to call <c>_buffer.ToString()</c> inside the extraction loop and
        /// <c>Remove(0, n)</c> once per line, which made a chunk holding k lines cost
        /// O(k * n) — measured at 16x the lines costing 122x the time. It now scans the
        /// StringBuilder by index, materialises only each line, and compacts once at the
        /// end, which is linear in the chunk size.
        /// </remarks>
        public void Append(byte[] data, int count, List<string> lines)
        {
            if (data == null || count <= 0) return;

            _buffer.Append(Encoding.UTF8.GetString(data, 0, count));

            var consumed = 0;
            var searchFrom = 0;

            while (true)
            {
                var index = IndexOfTerminator(searchFrom);
                if (index < 0) break;

                var length = index - consumed;
                if (length > 0)
                {
                    var line = _buffer.ToString(consumed, length).Trim('\r', '\n', ' ');
                    if (line.Length > 0) lines.Add(line);
                }

                consumed = index + _terminator.Length;
                searchFrom = consumed;
            }

            // One compaction for the whole chunk rather than one per line.
            if (consumed > 0) _buffer.Remove(0, consumed);

            // A peer that never sends a terminator must not grow this forever.
            if (_buffer.Length > MaxBufferedChars) _buffer.Length = 0;
        }

        /// <summary>Finds the terminator in the buffer without materialising it as a string.</summary>
        private int IndexOfTerminator(int from)
        {
            var last = _buffer.Length - _terminator.Length;
            for (var i = from; i <= last; i++)
            {
                var match = true;
                for (var k = 0; k < _terminator.Length; k++)
                {
                    if (_buffer[i + k] == _terminator[k]) continue;
                    match = false;
                    break;
                }
                if (match) return i;
            }
            return -1;
        }
    }
}
