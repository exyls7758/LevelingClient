using System;

namespace Multiverse.Services
{
    internal sealed class VideoLatencySnapshot
    {
        public int Samples { get; init; }
        public double AverageMs { get; init; }
        public double P95Ms { get; init; }
        public double P99Ms { get; init; }
        public double MaxMs { get; init; }
    }

    // A small bounded window for timestamps from the SAME process only.
    // No remote clock conversion, media content or input data is retained.
    internal sealed class VideoLatencyWindow
    {
        private const int Capacity = 128;
        private readonly object _sync = new();
        private readonly double[] _values = new double[Capacity];
        private int _count;
        private int _next;

        internal void Reset()
        {
            lock (_sync) { _count = 0; _next = 0; }
        }

        internal void ObserveTimestamp(long started, long now, long frequency)
        {
            if (started <= 0 || now < started || frequency <= 0) return;
            double ms = (now - started) * (1000.0 / frequency);
            ObserveMilliseconds(ms);
        }

        internal void ObserveMilliseconds(double ms)
        {
            if (!double.IsFinite(ms) || ms < 0 || ms > 60000) return;
            lock (_sync)
            {
                _values[_next] = ms;
                _next = (_next + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }

        internal VideoLatencySnapshot Snapshot()
        {
            double[] values;
            lock (_sync)
            {
                if (_count == 0) return new VideoLatencySnapshot();
                values = new double[_count];
                Array.Copy(_values, values, _count);
            }
            Array.Sort(values);
            double sum = 0;
            foreach (double value in values) sum += value;
            static double Percentile(double[] data, double percent)
            {
                int index = Math.Clamp((int)Math.Ceiling(data.Length * percent) - 1, 0, data.Length - 1);
                return data[index];
            }
            return new VideoLatencySnapshot
            {
                Samples = values.Length,
                AverageMs = sum / values.Length,
                P95Ms = Percentile(values, 0.95),
                P99Ms = Percentile(values, 0.99),
                MaxMs = values[^1]
            };
        }
    }
}
