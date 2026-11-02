using Multiverse.Services;

internal static class HostPipelineRecoveryTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static HostPipelineRecoverySample S(long now, long encoded = 10,
        double outputAge = 0, double callbackAge = 0, double publishAge = 0,
        uint inFlight = 0, uint slots = 4, uint queued = 0,
        int session = 1, long stream = 1, long runtime = 1,
        bool connected = true, bool running = true, bool stopping = false) => new()
    {
        NowMs = now, SessionGeneration = session, StreamGeneration = stream,
        RuntimeGeneration = runtime, Connected = connected, Running = running,
        EncoderRunning = true, CaptureRunning = true, EncoderWorkerAlive = true,
        StopInProgress = stopping, EncodedFrames = encoded,
        EncoderOutputAgeMs = outputAge, CaptureCallbackAgeMs = callbackAge,
        CapturePublishAgeMs = publishAge, InFlightFrames = inFlight,
        InputSlots = slots, QueuedPackets = queued
    };

    internal static void Run()
    {
        var p = new HostPipelineRecoveryPolicy();
        Check(p.Observe(S(0)) == "", "initial observation");
        Check(p.Observe(S(6000, outputAge: 6000, callbackAge: 6000, publishAge: 6000)) == "",
            "idle/static source cannot trigger restart");
        Check(p.Observe(S(7000, outputAge: 7000, publishAge: 7000, queued: 3)) == "",
            "coded queue pressure cannot trigger restart");
        Check(p.Observe(S(8000, outputAge: 8000, publishAge: 8000)) == "", "confirmation begins");
        Check(p.Observe(S(9000, outputAge: 9000, publishAge: 9000)) == "HOST_CAPTURE_STALL",
            "sustained capture stall triggers once");
        Check(p.AttemptActive, "attempt remains owned");
        Check(p.Observe(S(10000, outputAge: 10000, publishAge: 10000)) == "", "no concurrent restart");
        p.Complete(10000);
        Check(p.Observe(S(11000, outputAge: 11000, publishAge: 11000)) == "", "cooldown");
        p.Observe(S(12000, encoded: 11));
        p.Observe(S(14000, encoded: 12));
        Check(p.Observe(S(71000, outputAge: 6000, publishAge: 6000)) == "", "new episode confirmation");
        Check(p.Observe(S(72000, outputAge: 7000, publishAge: 7000)) == "HOST_CAPTURE_STALL",
            "rearm after healthy progress and cooldown");
        p.Complete(72000);
        p.Reset();
        p.Observe(S(0));
        p.Observe(S(5000, outputAge: 5000, callbackAge: 5000, inFlight: 4));
        Check(p.Observe(S(6000, outputAge: 6000, callbackAge: 6000, inFlight: 4)) == "",
            "full slots require oldest-frame evidence");
        p.Reset();
        var stuck = new HostPipelineRecoverySample
        {
            NowMs = 0, SessionGeneration = 1, StreamGeneration = 1, RuntimeGeneration = 1,
            Connected = true, Running = true, EncoderRunning = true, CaptureRunning = true,
            EncoderWorkerAlive = true, EncoderOutputAgeMs = 6000, CaptureCallbackAgeMs = 6000,
            InFlightFrames = 4, InputSlots = 4, OldestInFlightMs = 6000, EncodedFrames = 10
        };
        p.Observe(stuck);
        Check(p.Observe(new HostPipelineRecoverySample
        {
            NowMs = 1000, SessionGeneration = 1, StreamGeneration = 1, RuntimeGeneration = 1,
            Connected = true, Running = true, EncoderRunning = true, CaptureRunning = true,
            EncoderWorkerAlive = true, EncoderOutputAgeMs = 7000, CaptureCallbackAgeMs = 7000,
            InFlightFrames = 4, InputSlots = 4, OldestInFlightMs = 7000, EncodedFrames = 10
        }) == "HOST_NVENC_SLOTS_STALL", "full stale NVENC slots are actionable");
        p.Complete(1000);
        p.Reset();
        p.Observe(S(0));
        p.Observe(S(5000, outputAge: 5000, publishAge: 5000));
        Check(p.Observe(S(6000, outputAge: 6000, publishAge: 6000, runtime: 2)) == "",
            "runtime change invalidates old observation");
        Check(p.Observe(S(7000, outputAge: 7000, publishAge: 7000, stopping: true)) == "",
            "manual stop cancels suspicion");
        p.Reset();
        p.Observe(S(0));
        p.Observe(S(5000, outputAge: 5000, publishAge: 5000));
        Check(p.Observe(S(6000, outputAge: 6000, publishAge: 6000, session: 2)) == "",
            "new session must not inherit old stall");
        Console.WriteLine("host recovery policy: PASS");
    }
}
