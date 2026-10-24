using System;
using System.Collections.Generic;
using System.Globalization;

namespace ProtokolBridge
{
    public struct TrafficEntry
    {
        public DateTime TimeUtc;
        public string EndpointName;
        public MessageDirection Direction;
        public string Summary;
        public string Remote;
        /// <summary>Which rule produced this, for outbound entries. Empty otherwise.</summary>
        public string RuleName;

        public string TimeText
        {
            get { return TimeUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture); }
        }

        public string DirectionArrow
        {
            get { return Direction == MessageDirection.Inbound ? "IN " : "OUT"; }
        }
    }

    /// <summary>
    /// Fixed-capacity ring buffer of recent traffic. A bridge under load can see
    /// thousands of messages a second, so the log must never grow without bound.
    /// </summary>
    public sealed class TrafficLog
    {
        private TrafficEntry[] _entries;
        private int _start;
        private int _count;

        public TrafficLog(int capacity)
        {
            Resize(capacity);
        }

        public int Count { get { return _count; } }
        public int Capacity { get { return _entries.Length; } }
        public long TotalSeen { get; private set; }

        public void Resize(int capacity)
        {
            if (capacity < 16) capacity = 16;
            if (capacity > 20000) capacity = 20000;

            var replacement = new TrafficEntry[capacity];
            var copied = Math.Min(_count, capacity);

            for (var i = 0; i < copied; i++) replacement[i] = this[_count - copied + i];

            _entries = replacement;
            _start = 0;
            _count = copied;
        }

        public TrafficEntry this[int index]
        {
            get
            {
                if (index < 0 || index >= _count) throw new ArgumentOutOfRangeException("index");
                return _entries[(_start + index) % _entries.Length];
            }
        }

        public void Add(TrafficEntry entry)
        {
            TotalSeen++;

            if (_count < _entries.Length)
            {
                _entries[(_start + _count) % _entries.Length] = entry;
                _count++;
                return;
            }

            _entries[_start] = entry;
            _start = (_start + 1) % _entries.Length;
        }

        public void Clear()
        {
            _start = 0;
            _count = 0;
        }

        /// <summary>Copies the most recent <paramref name="max"/> entries, oldest first.</summary>
        public void CopyRecent(List<TrafficEntry> target, int max)
        {
            target.Clear();
            var take = Math.Min(max, _count);
            for (var i = _count - take; i < _count; i++) target.Add(this[i]);
        }
    }
}
