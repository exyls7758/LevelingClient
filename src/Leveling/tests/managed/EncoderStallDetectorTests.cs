using Multiverse.Services;
internal static class EncoderStallDetectorTests
{
    internal static void Run()
    {
        static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        static EncoderStallEvidence Sample(long now, long arrived, long published = 10,
            bool worker = false, uint slots = 4, uint pending = 4, int status = unchecked((int)0x800705B4)) =>
            new(now, 1, 1, true, arrived, published, 0, 6000, true, worker, slots, 4, pending, 6000, status, false);
        var d = new EncoderStallDetector();
        Check(!d.Observe(Sample(0, 10)), "must establish baseline");
        Check(!d.Observe(Sample(500, 11)), "sustained window");
        Check(d.Observe(Sample(1500, 20)), "dead output with capture advancement must trigger");
        Check(!d.Observe(Sample(1750, 21, 11)), "publication progress cancels suspicion");
        d = new(); d.Observe(Sample(0, 10) with { EncoderRunning = false });
        d.Observe(Sample(500, 11) with { EncoderRunning = false });
        Check(d.Observe(Sample(1500, 20) with { EncoderRunning = false }), "native fatal status clears Running and must still recover");
        d = new(); d.Observe(Sample(0, 10));
        Check(!d.Observe(Sample(6000, 10)), "static source cannot trigger");
        d = new(); d.Observe(Sample(0, 10, worker: true, slots: 0, pending: 0, status: 0));
        Check(!d.Observe(Sample(6000, 30, worker: true, slots: 0, pending: 0, status: 0)), "RTT/transport alone cannot trigger");
        // Native output timeout is already terminal. Do not wait another five
        // seconds with a dead worker before the ownership-safe recovery starts.
        d = new();
        var fatal = Sample(0, 10) with { EncoderRunning = false, PublishAgeMs = 1200, OldestAgeMs = 1400 };
        d.Observe(fatal);
        Check(!d.Observe(fatal with { Now = 250, Arrived = 11 }), "fatal still requires sustained evidence");
        Check(d.Observe(fatal with { Now = 1250, Arrived = 20, PublishAgeMs = 2450, OldestAgeMs = 2650 }), "terminal native timeout should recover before five-second threshold");
        d = new();
        var busy = fatal with { EncoderRunning = true, OutputWorkerAlive = true, Status = 0 };
        d.Observe(busy);
        d.Observe(busy with { Now = 250, Arrived = 11 });
        Check(!d.Observe(busy with { Now = 1250, Arrived = 20 }), "GPU load with live workers must not use terminal fast path");
        d = new(); d.Observe(fatal); d.Observe(fatal with { Now = 250, Arrived = 11 });
        Check(!d.Observe(fatal with { Now = 1250, Arrived = 20, Published = 11 }), "publication progress cancels fatal suspicion");
        d = new(); d.Observe(fatal); d.Observe(fatal with { Now = 250, Arrived = 11 });
        Check(!d.Observe(fatal with { Now = 1250, Arrived = 20, Stopping = true }), "intentional stop never recovers");
        Console.WriteLine("encoder stall evidence: PASS");
    }
}
