using System;
using Multiverse.Services;

internal static class TransportRecoveryTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL: " + message);
    }

    private static ClientTransportRecoverySample S(long now, long packets = 0,
        long frames = 0, long presented = 0, int session = 1, long stream = 1,
        bool connected = true, bool decoder = true, bool presenter = true) => new()
    {
        NowMs = now, SessionGeneration = session, StreamGeneration = stream,
        Connected = connected, ReceiverRunning = true, DecoderRunning = decoder,
        PresenterAttached = presenter, Packets = packets, Frames = frames,
        Presented = presented
    };

    internal static void Run()
    {
        var policy = new ClientTransportRecoveryPolicy();
        Check(policy.Observe(S(0)).Action == ClientTransportRecoveryAction.None, "startup grace");
        Check(policy.Observe(S(3500, 100, 0)).Action == ClientTransportRecoveryAction.ReopenReceiver,
            "packets without complete frames must recover");
        policy.Complete(ClientTransportRecoveryAction.ReopenReceiver, true, 3500, 1, 1);
        Check(policy.Observe(S(4000, 200, 0)).Action == ClientTransportRecoveryAction.None,
            "no immediate second rebound");
        Check(policy.Observe(S(6000, 300, 0)).Action == ClientTransportRecoveryAction.RotateSender,
            "local rebound without progress escalates to host");
        policy.Complete(ClientTransportRecoveryAction.RotateSender, true, 6000, 1, 1);
        Check(policy.Observe(S(8000, 400, 0)).Action == ClientTransportRecoveryAction.None,
            "remote rotation needs a confirmation window");
        Check(policy.Observe(S(14000, 500, 0)).Action == ClientTransportRecoveryAction.RotateSender,
            "bounded second remote attempt");
        policy.Complete(ClientTransportRecoveryAction.RotateSender, true, 14000, 1, 1);
        Check(policy.Observe(S(22000, 600, 0)).Action == ClientTransportRecoveryAction.Exhausted,
            "no infinite destructive retry loop");
        Check(policy.Observe(S(30000, 700, 0)).Action == ClientTransportRecoveryAction.None,
            "exhaustion is latched");
        var recovered = policy.Observe(S(30250, 710, 1, 1));
        Check(recovered.Action == ClientTransportRecoveryAction.Recovered,
            "only presented progress confirms recovery");
        Check(policy.Observe(S(30500, 720, 2, 2)).Action == ClientTransportRecoveryAction.None,
            "healthy progress rearms without a restart");

        policy.Reset();
        policy.Observe(S(0, 1000, 100, 100));
        Check(policy.Observe(S(3500, 1100, 100, 100)).Action == ClientTransportRecoveryAction.ReopenReceiver,
            "initial stall before receiver replacement");
        policy.Complete(ClientTransportRecoveryAction.ReopenReceiver, true, 3500, 1, 1);
        Check(policy.Observe(S(4000, 0, 0, 100)).Action == ClientTransportRecoveryAction.None,
            "receiver counter reset preserves the attempt budget");
        Check(policy.Observe(S(6000, 0, 0, 100)).Action == ClientTransportRecoveryAction.RotateSender,
            "a rebound must not erase the need for host escalation");

        policy.Reset();
        policy.Observe(S(0, 0, 0, 0));
        policy.Observe(S(100, 10, 10, 10));
        Check(policy.Observe(S(4000, 300, 200, 10)).Action == ClientTransportRecoveryAction.None,
            "decoder/presenter stall must not rotate network sockets");
        policy.Reset();
        policy.Observe(S(0));
        Check(policy.Observe(S(3500)).Action == ClientTransportRecoveryAction.ReopenReceiver,
            "UDP silence with no frames");
        policy.Complete(ClientTransportRecoveryAction.ReopenReceiver, false, 3500, 1, 1);
        Check(policy.Observe(S(8000)).Action == ClientTransportRecoveryAction.Exhausted,
            "failed cleanup blocks replacement");
        policy.Reset();
        policy.Observe(S(0));
        policy.Observe(S(3500));
        policy.Complete(ClientTransportRecoveryAction.ReopenReceiver, true, 3500, 1, 1);
        Check(policy.Observe(S(6000, session: 2)).Action == ClientTransportRecoveryAction.None,
            "new session cancels old work");
        policy.Complete(ClientTransportRecoveryAction.RotateSender, true, 6000, 1, 1);
        Check(policy.Observe(S(9500, session: 2)).Action == ClientTransportRecoveryAction.ReopenReceiver,
            "old completion cannot affect new session");
        policy.Reset();
        policy.Observe(S(0));
        policy.Observe(S(3500));
        policy.Complete(ClientTransportRecoveryAction.ReopenReceiver, true, 3500, 1, 1);
        Check(policy.Observe(S(4000, 10, 1, 1)).Action == ClientTransportRecoveryAction.Recovered,
            "spontaneous progress cancels pending escalation");
        policy.Observe(S(4500, 11, 2, 2));
        Check(policy.Observe(S(8000, 100, 2, 2)).Action == ClientTransportRecoveryAction.ReopenReceiver,
            "a new independent stall is actionable");

        policy.Reset();
        policy.Observe(S(0));
        var reference = new ClientTransportRecoverySample
        {
            NowMs = 3500, SessionGeneration = 1, StreamGeneration = 1,
            Connected = true, ReceiverRunning = true, DecoderRunning = true,
            PresenterAttached = true, WaitingForReference = true,
            Packets = 500, Frames = 100, Presented = 0
        };
        Check(policy.Observe(reference).Action == ClientTransportRecoveryAction.ReopenReceiver,
            "continuous P-frames without a valid reference must not hide a stall");


        policy.Reset();
        policy.Observe(S(0));
        policy.Observe(S(3500));
        policy.Complete(ClientTransportRecoveryAction.ReopenReceiver, true, 3500, 1, 1);
        Check(policy.Observe(S(6000)).Action == ClientTransportRecoveryAction.RotateSender,
            "remote recovery request is issued after local rebound");
        policy.Complete(ClientTransportRecoveryAction.RotateSender, false, 6000, 1, 1);
        Check(policy.Observe(S(7000)).Action == ClientTransportRecoveryAction.None,
            "rejected host recovery keeps a short backoff");
        Check(policy.Observe(S(7500)).Action == ClientTransportRecoveryAction.RotateSender,
            "explicit host rejection retries without waiting the full accepted window");

        var host = new HostSenderRecoveryPolicy();
        Check(host.TryRegisterRequest(1, 1, 1, out _), "first request registered");
        Check(host.TryConsumeAttempt(10000, out _), "first attempt accepted");
        Check(!host.TryRegisterRequest(1, 1, 1, out _), "replay rejected");
        Check(host.TryRegisterRequest(1, 1, 2, out _), "second request registered without spending budget");
        Check(!host.TryConsumeAttempt(12000, out _), "cooldown enforced");
        Check(host.TryRegisterRequest(1, 1, 3, out _), "third request registered");
        Check(host.TryConsumeAttempt(18000, out _), "second attempt accepted");
        Check(host.TryRegisterRequest(1, 1, 4, out _), "fourth request registered");
        Check(!host.TryConsumeAttempt(26000, out _), "rolling budget enforced");
        Check(host.TryRegisterRequest(1, 1, 5, out _), "fifth request registered");
        Check(host.TryConsumeAttempt(80000, out _), "budget expires");
        Check(host.TryRegisterRequest(2, 1, 1, out _), "new session has independent request sequence");
        Check(host.TryConsumeAttempt(81000, out _), "new session has independent budget");
        Check(!host.TryRegisterRequest(2, 1, 0, out _), "invalid request id rejected");
        Console.WriteLine("Transport recovery regression: PASS");
    }
}
