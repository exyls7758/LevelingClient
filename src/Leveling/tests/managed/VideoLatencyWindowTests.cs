using Multiverse.Services;

internal static class VideoLatencyWindowTests
{
    internal static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
        }
        var window = new VideoLatencyWindow();
        window.ObserveTimestamp(1000, 900, 1000);
        window.ObserveTimestamp(1000, 2000, 0);
        window.ObserveMilliseconds(double.NaN);
        Check(window.Snapshot().Samples == 0, "invalid clocks are ignored");
        for (int i = 1; i <= 100; i++) window.ObserveMilliseconds(i);
        var s = window.Snapshot();
        Check(s.Samples == 100 && s.AverageMs == 50.5 && s.P95Ms == 95 && s.P99Ms == 99,
            "nearest-rank percentiles");
        for (int i = 0; i < 128; i++) window.ObserveMilliseconds(2);
        s = window.Snapshot();
        Check(s.Samples == 128 && s.P95Ms == 2 && s.MaxMs == 2, "bounded window evicts old samples");
        window.Reset();
        window.ObserveTimestamp(100, 120, 1000);
        s = window.Snapshot();
        Check(s.Samples == 1 && s.AverageMs == 20, "same-process timestamp conversion");
        window.Reset();
        Check(window.Snapshot().Samples == 0, "new decoder starts a fresh window");
        Console.WriteLine("local latency window: PASS");
    }
}
