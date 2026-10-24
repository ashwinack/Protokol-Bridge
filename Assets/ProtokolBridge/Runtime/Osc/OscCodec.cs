using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ProtokolBridge
{
    /// <summary>
    /// Minimal, dependency-free OSC 1.0 encoder/decoder.
    /// Supports the type tags i, f, s, b, h, d, T, F, N, I and #bundle containers,
    /// which covers TouchDesigner, Resolume, Max/MSP, QLab and typical lighting desks.
    /// </summary>
    public static class OscCodec
    {
        public static byte[] Encode(string address, IList<object> args)
        {
            if (string.IsNullOrEmpty(address)) address = "/";
            if (address[0] != '/') address = "/" + address;

            var body = new List<byte>(64);
            WritePaddedString(body, address);

            var tags = new StringBuilder(",");
            var payload = new List<byte>(64);

            if (args != null)
            {
                foreach (var arg in args)
                {
                    if (arg == null) { tags.Append('N'); continue; }

                    if (arg is bool)
                    {
                        tags.Append((bool)arg ? 'T' : 'F');
                    }
                    else if (arg is int || arg is short || arg is byte || arg is sbyte || arg is ushort)
                    {
                        tags.Append('i');
                        WriteInt32(payload, Convert.ToInt32(arg, CultureInfo.InvariantCulture));
                    }
                    else if (arg is long || arg is uint)
                    {
                        tags.Append('h');
                        WriteInt64(payload, Convert.ToInt64(arg, CultureInfo.InvariantCulture));
                    }
                    else if (arg is float)
                    {
                        tags.Append('f');
                        WriteFloat32(payload, (float)arg);
                    }
                    else if (arg is double || arg is decimal)
                    {
                        tags.Append('d');
                        WriteFloat64(payload, Convert.ToDouble(arg, CultureInfo.InvariantCulture));
                    }
                    else if (arg is byte[])
                    {
                        tags.Append('b');
                        var blob = (byte[])arg;
                        WriteInt32(payload, blob.Length);
                        payload.AddRange(blob);
                        Pad(payload);
                    }
                    else
                    {
                        tags.Append('s');
                        WritePaddedString(payload, Convert.ToString(arg, CultureInfo.InvariantCulture));
                    }
                }
            }

            WritePaddedString(body, tags.ToString());
            body.AddRange(payload);
            return body.ToArray();
        }

        /// <summary>
        /// Decodes a datagram into one or more messages. A bundle yields all of its
        /// contained messages in order; a plain message yields exactly one.
        /// </summary>
        public static bool TryDecode(byte[] data, List<OscParsed> results)
        {
            if (data == null || data.Length < 4 || results == null) return false;
            var offset = 0;
            return TryDecodeElement(data, ref offset, data.Length, results);
        }

        private static bool TryDecodeElement(byte[] data, ref int offset, int end, List<OscParsed> results)
        {
            if (offset >= end) return false;

            if (data[offset] == '#')
            {
                string marker;
                if (!TryReadPaddedString(data, ref offset, end, out marker)) return false;
                if (marker != "#bundle") return false;

                // Skip the 8-byte OSC time tag; immediate dispatch is the only mode we need.
                if (offset + 8 > end) return false;
                offset += 8;

                var any = false;
                while (offset + 4 <= end)
                {
                    var size = ReadInt32(data, ref offset);
                    if (size < 0 || offset + size > end) return false;

                    var inner = offset;
                    if (TryDecodeElement(data, ref inner, offset + size, results)) any = true;
                    offset += size;
                }
                return any;
            }

            string address;
            if (!TryReadPaddedString(data, ref offset, end, out address)) return false;
            if (address.Length == 0 || address[0] != '/') return false;

            var parsed = new OscParsed { Address = address };

            string tagString;
            if (!TryReadPaddedString(data, ref offset, end, out tagString) || tagString.Length == 0 || tagString[0] != ',')
            {
                // Address-only message: legal, and some senders do exactly this.
                results.Add(parsed);
                return true;
            }

            for (var i = 1; i < tagString.Length; i++)
            {
                switch (tagString[i])
                {
                    case 'i':
                        if (offset + 4 > end) return false;
                        parsed.Args.Add(ReadInt32(data, ref offset));
                        break;
                    case 'f':
                        if (offset + 4 > end) return false;
                        parsed.Args.Add(ReadFloat32(data, ref offset));
                        break;
                    case 'h':
                        if (offset + 8 > end) return false;
                        parsed.Args.Add(ReadInt64(data, ref offset));
                        break;
                    case 'd':
                        if (offset + 8 > end) return false;
                        parsed.Args.Add(ReadFloat64(data, ref offset));
                        break;
                    case 's':
                    case 'S':
                    {
                        string s;
                        if (!TryReadPaddedString(data, ref offset, end, out s)) return false;
                        parsed.Args.Add(s);
                        break;
                    }
                    case 'b':
                    {
                        if (offset + 4 > end) return false;
                        var len = ReadInt32(data, ref offset);
                        if (len < 0 || offset + len > end) return false;
                        var blob = new byte[len];
                        Buffer.BlockCopy(data, offset, blob, 0, len);
                        offset += len;
                        offset = Align(offset);
                        parsed.Args.Add(blob);
                        break;
                    }
                    case 'T': parsed.Args.Add(true); break;
                    case 'F': parsed.Args.Add(false); break;
                    case 'N': parsed.Args.Add(null); break;
                    case 'I': parsed.Args.Add(float.PositiveInfinity); break;
                    default:
                        // Unknown tag: we cannot know its width, so stop rather than misread.
                        results.Add(parsed);
                        return true;
                }
            }

            results.Add(parsed);
            return true;
        }

        private static int Align(int offset)
        {
            var remainder = offset % 4;
            return remainder == 0 ? offset : offset + (4 - remainder);
        }

        private static void Pad(List<byte> buffer)
        {
            while (buffer.Count % 4 != 0) buffer.Add(0);
        }

        private static void WritePaddedString(List<byte> buffer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            buffer.AddRange(bytes);
            buffer.Add(0);
            Pad(buffer);
        }

        private static bool TryReadPaddedString(byte[] data, ref int offset, int end, out string value)
        {
            value = null;
            var start = offset;
            while (offset < end && data[offset] != 0) offset++;
            if (offset >= end) return false;

            value = Encoding.UTF8.GetString(data, start, offset - start);
            offset++; // the terminating null
            offset = Align(offset);
            return true;
        }

        private static void WriteInt32(List<byte> buffer, int value)
        {
            buffer.Add((byte)(value >> 24));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        private static void WriteInt64(List<byte> buffer, long value)
        {
            for (var shift = 56; shift >= 0; shift -= 8) buffer.Add((byte)(value >> shift));
        }

        private static void WriteFloat32(List<byte> buffer, float value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            buffer.AddRange(bytes);
        }

        private static void WriteFloat64(List<byte> buffer, double value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            buffer.AddRange(bytes);
        }

        private static int ReadInt32(byte[] data, ref int offset)
        {
            var value = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            offset += 4;
            return value;
        }

        private static long ReadInt64(byte[] data, ref int offset)
        {
            long value = 0;
            for (var i = 0; i < 8; i++) value = (value << 8) | data[offset + i];
            offset += 8;
            return value;
        }

        private static float ReadFloat32(byte[] data, ref int offset)
        {
            var bytes = new byte[4];
            Buffer.BlockCopy(data, offset, bytes, 0, 4);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            offset += 4;
            return BitConverter.ToSingle(bytes, 0);
        }

        private static double ReadFloat64(byte[] data, ref int offset)
        {
            var bytes = new byte[8];
            Buffer.BlockCopy(data, offset, bytes, 0, 8);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            offset += 8;
            return BitConverter.ToDouble(bytes, 0);
        }
    }

    public sealed class OscParsed
    {
        public string Address;
        public readonly List<object> Args = new List<object>();
    }
}
