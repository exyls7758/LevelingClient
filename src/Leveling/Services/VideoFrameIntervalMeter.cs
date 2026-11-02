using System;
using System.Diagnostics;
using System.Threading;

namespace Multiverse.Services
{
    internal sealed class VideoFrameIntervalMeter
    {
        private long _lastTick;
        private long _lastGapUs;
        private long _maxGapUs;
        private long _gapsOver100Ms;
        internal void Mark()
        {
            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Exchange(ref _lastTick, now);
            if (previous <= 0 || now <= previous) return;
            long gap = (long)((now - previous) * (1_000_000.0 / Stopwatch.Frequency));
            Interlocked.Exchange(ref _lastGapUs, gap);
            long current;
            while (gap > (current = Interlocked.Read(ref _maxGapUs)) &&
                Interlocked.CompareExchange(ref _maxGapUs, gap, current) != current) { }
            if (gap >= 100000) Interlocked.Increment(ref _gapsOver100Ms);
        }
        internal void Reset()
        {
            Interlocked.Exchange(ref _lastTick, 0);
            Interlocked.Exchange(ref _lastGapUs, 0);
            Interlocked.Exchange(ref _maxGapUs, 0);
            Interlocked.Exchange(ref _gapsOver100Ms, 0);
        }
        internal VideoFrameIntervalSnapshot GetSnapshot()
        {
            long tick = Interlocked.Read(ref _lastTick);
            return new VideoFrameIntervalSnapshot
            {
                LastAgeMs = tick == 0 ? -1 : Math.Max(0, Stopwatch.GetElapsedTime(tick).TotalMilliseconds),
                LastGapMs = Interlocked.Read(ref _lastGapUs) / 1000.0,
                MaxGapMs = Interlocked.Read(ref _maxGapUs) / 1000.0,
                GapsOver100Ms = Interlocked.Read(ref _gapsOver100Ms)
            };
        }
    }
    internal sealed class VideoFrameIntervalSnapshot
    {
        public double LastAgeMs { get; init; }
        public double LastGapMs { get; init; }
        public double MaxGapMs { get; init; }
        public long GapsOver100Ms { get; init; }
    }
}
