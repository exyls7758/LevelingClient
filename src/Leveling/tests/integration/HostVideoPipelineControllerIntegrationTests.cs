using System.Reflection;

namespace Multiverse.Services;

internal static class HostVideoPipelineControllerIntegrationTests
{
    internal static IEnumerable<(string Name, Func<Task> Test)> Cases()
    {
        yield return ("rapid_reconnect_same_endpoint_rotates_sender_without_restarting_capture", SameEndpointReconnectAsync);
        yield return ("video_ready_timeout_clears_remote_stream_and_allows_retry", BootstrapFailureRetryAsync);
        yield return ("twenty_start_stop_cycles_release_every_encoder_and_sender", TwentyStartStopCyclesAsync);
        yield return ("nvenc_output_fatal_running_false_recovers_without_encoder_overlap", () => OutputFatalRecoversAsync());
        yield return ("terminal_timeout_recovers_before_five_seconds_and_rejects_sender_only_restart", () => OutputFatalRecoversAsync(true));
        yield return ("transport_recovery_confirms_client_present_without_restarting_encoder", TransportRecoveryConfirmsChainAsync);
        yield return ("old_generation_recovery_is_rejected_without_touching_current_resources", OldGenerationRejectedAsync);
        yield return ("encoder_stop_failure_faults_and_never_opens_a_second_encoder", StopFailureNeverOverlapsAsync);
        yield return ("encoder_start_failure_rolls_back_capture_and_returns_to_stopped", StartFailureRollsBackAsync);
        yield return ("qhd_profile_reaches_native_encoder", QhdProfileAsync);
        yield return ("hung_capture_disposal_releases_gate_but_retains_ownership", HungCaptureDisposeAsync);
    }

    private static async Task SameEndpointReconnectAsync()
    {
        FakeEnvironment.Reset();
        using var controller = NewController();
        Check(await controller.StartAsync(), "initial start failed");
        RemoteSessionService.Instance.AdvanceSession();
        await Task.Delay(100);
        Check(RemoteHostVideoTransport.CreatedCount == 2, "old sender reused after reconnect to identical endpoint");
        Check(NativeCoreService.OpenCaptureCalls == 1, "healthy capture restarted for reconnect");
        Check(RemoteHostVideoTransport.ActiveSenders == 1 && RemoteHostVideoTransport.MaxActiveSenders == 1,
            "senders overlap across sessions");
        await controller.StopAsync();
    }

    private static async Task BootstrapFailureRetryAsync()
    {
        FakeEnvironment.Reset();
        using var controller = NewController();
        RemoteSessionService.Instance.VideoReady = false;
        Check(!await controller.StartAsync(), "unacknowledged stream started");
        Check(!controller.HasOwnedResources, "bootstrap rollback retained resources");
        Check(RemoteSessionService.Instance.StreamStoppedPublishes == 1, "client was left waiting for a dead stream");
        Check(GetState(controller) == HostPipelineState.STOPPED, "clean rollback incorrectly requires manual stop");
        RemoteSessionService.Instance.VideoReady = true;
        Check(await controller.StartAsync(), "retry after clean rollback failed");
        await controller.StopAsync();
    }

    private static async Task HungCaptureDisposeAsync()
    {
        FakeEnvironment.Reset();
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var controller = NewController();
        Task? stop = null;
        try
        {
            Check(await controller.StartAsync(), "initial start failed");
            var capture = NativeCoreService.Captures.Single();
            capture.DisposeOverride = () => { entered.Set(); release.Wait(); };
            stop = Task.Run(() => controller.StopAsync());
            Check(entered.Wait(TimeSpan.FromSeconds(2)), "dispose was not reached");
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Check(controller.GetSnapshot().CanStop && controller.HasOwnedResources, "lost capture ownership on timeout");
            Check(!await controller.StartAsync(), "opened encoder while old capture disposal was pending");
            Check(NativeCoreService.OpenCaptureCalls == 1, "second capture opened");
            release.Set();
            await controller.StopAsync();
            Check(!controller.HasOwnedResources, "confirmed dispose did not release capture");
            Check(capture.StopEncoderCalls == 1, "retried native encoder stop during/after capture disposal");
        }
        finally { release.Set(); if (stop is not null) await stop; controller.Dispose(); }
    }

    private static async Task QhdProfileAsync()
    {
        FakeEnvironment.Reset();
        MultiverseSettingsService.Instance.Profile = new() { MaxWidth = 2560, MaxHeight = 1440 };
        using var controller = NewController();
        Check(await controller.StartAsync(), "QHD start failed");
        var config = NativeCoreService.Captures.Single().LastConfig!;
        Check(config.MaxWidth == 2560 && config.MaxHeight == 1440, "QHD limit lost before native start");
        await controller.StopAsync();
    }

    private static async Task TwentyStartStopCyclesAsync()
    {
        FakeEnvironment.Reset();
        HostVideoPipelineController controller = NewController();
        try
        {
            for (int cycle = 1; cycle <= 20; cycle++)
            {
                Check(await controller.StartAsync(), $"cycle {cycle}: StartAsync rejected a healthy pipeline");
                ConfirmInitialFrameChain(controller);
                Check(GetState(controller) == HostPipelineState.RUNNING,
                    $"cycle {cycle}: successful attached start did not enter RUNNING");
                Check(controller.GetSnapshot().EncoderRunning, $"cycle {cycle}: encoder is not reported running");
                Check(controller.GetSnapshot().RemoteTransportRunning, $"cycle {cycle}: sender is not reported running");
                await controller.StopAsync();
                Check(GetState(controller) == HostPipelineState.STOPPED, $"cycle {cycle}: stop was not confirmed");
                Check(!controller.HasOwnedResources, $"cycle {cycle}: controller retained lifecycle ownership");
                Check(NativeCoreService.ActiveEncoders == 0, $"cycle {cycle}: encoder remained active");
                Check(RemoteHostVideoTransport.ActiveSenders == 0, $"cycle {cycle}: sender remained active");
            }

            Check(NativeCoreService.OpenCaptureCalls == 20, "twenty cycles must open exactly twenty captures");
            Check(NativeCoreService.Captures.All(c => c.Disposed && c.StopEncoderCalls == 1),
                "every capture must have one confirmed stop and disposal");
            Check(RemoteHostVideoTransport.CreatedCount == 20, "twenty cycles must create exactly twenty senders");
            Check(RemoteHostVideoTransport.StopCount == 20 && RemoteHostVideoTransport.DisposeCount == 20,
                "every sender must have one confirmed stop and disposal");
            Check(NativeCoreService.MaxActiveEncoders == 1, "encoder ownership overlapped during cycles");
            Check(RemoteHostVideoTransport.MaxActiveSenders == 1, "sender ownership overlapped during cycles");
            Check(NativeCoreService.UnsafeDisposeWhileRunning == 0, "a running encoder was disposed");
            Check(RemoteHostVideoTransport.UnsafeDisposeWhileRunning == 0, "a running sender was disposed");
        }
        finally { controller.Dispose(); }
    }

    private static async Task OutputFatalRecoversAsync(bool early = false)
    {
        FakeEnvironment.Reset();
        HostVideoPipelineController controller = NewController();
        try
        {
            Check(await controller.StartAsync(), "healthy initial host start failed");
            NativeCaptureSession oldCapture = NativeCoreService.Captures.Single();
            long stream = RemoteSessionService.Instance.Snapshot.StreamConfig!.Generation;

            void Evidence(ulong arrived)
            {
                SetFatalEvidence(oldCapture, arrived);
                if (early)
                {
                    oldCapture.CaptureStats.LastPublishAgoMs = 2200;
                    oldCapture.EncoderStats.OldestInFlightMs = 2400;
                }
            }
            Evidence(100);
            if (early)
            {
                int senders = RemoteHostVideoTransport.CreatedCount;
                Check(!await controller.RecoverHostVideoAsync(RemoteSessionService.Instance.Snapshot.SessionGeneration,
                    stream, 123, 0), "terminal encoder incorrectly treated as sender-only fault");
                Check(RemoteHostVideoTransport.CreatedCount == senders, "sender churn during terminal encoder fault");
            }
            controller.ObserveEncoderHealth(RemoteSessionService.Instance.GetSnapshot(),
                oldCapture.CaptureStats, oldCapture.EncoderStats);
            Evidence(101);
            controller.ObserveEncoderHealth(RemoteSessionService.Instance.GetSnapshot(),
                oldCapture.CaptureStats, oldCapture.EncoderStats);
            await Task.Delay(1050);
            Evidence(102);
            controller.ObserveEncoderHealth(RemoteSessionService.Instance.GetSnapshot(),
                oldCapture.CaptureStats, oldCapture.EncoderStats);

            await WaitUntilAsync(() => VideoIncidentRecorder.Instance.Events.Contains("ENCODER_RECOVERY_SUCCESS"),
                TimeSpan.FromSeconds(4), "encoder recovery did not confirm the replacement generation");

            Check(NativeCoreService.OpenCaptureCalls == 2, "fatal output worker must open one replacement capture");
            Check(oldCapture.StopEncoderCalls == 1 && oldCapture.Disposed,
                "old encoder must stop and release before replacement");
            Check(NativeCoreService.MaxActiveEncoders == 1, "fatal recovery created overlapping encoders");
            Check(RemoteHostVideoTransport.MaxActiveSenders == 1, "fatal recovery created overlapping senders");
            Check(GetState(controller) == HostPipelineState.RUNNING, "confirmed replacement did not return to RUNNING");
            Check(RemoteSessionService.Instance.Snapshot.StreamConfig!.Generation > stream,
                "encoder recovery did not allocate a new stream generation");
            Check(NativeCoreService.Captures[1].LastConfig is { Fps: 60, BitrateKbps: 20_000, LabInputMode: 10 },
                "replacement did not preserve the effective encoder config");

            await controller.StopAsync();
        }
        finally { controller.Dispose(); }
    }

    private static async Task TransportRecoveryConfirmsChainAsync()
    {
        FakeEnvironment.Reset();
        HostVideoPipelineController controller = NewController();
        try
        {
            Check(await controller.StartAsync(), "healthy initial host start failed");
            ConfirmInitialFrameChain(controller);
            NativeCaptureSession capture = NativeCoreService.Captures.Single();
            int encoderStarts = capture.StartEncoderCalls;
            int encoderStops = capture.StopEncoderCalls;
            int session = RemoteSessionService.Instance.Snapshot.SessionGeneration;
            long stream = RemoteSessionService.Instance.Snapshot.StreamConfig!.Generation;
            long presented = RemoteSessionService.Instance.Snapshot.RemotePresentedFrames;

            bool recovered = await controller.RecoverHostVideoAsync(session, stream,
                recoveryRequestId: 91, presentedBaseline: presented);

            Check(recovered, "transport recovery did not confirm the complete/decoded/presented chain");
            Check(GetState(controller) == HostPipelineState.RUNNING, "confirmed transport recovery did not return to RUNNING");
            Check(RemoteSessionService.Instance.Snapshot.StreamConfig!.Generation == stream,
                "transport-only recovery changed stream generation");
            Check(capture.StartEncoderCalls == encoderStarts && capture.StopEncoderCalls == encoderStops,
                "transport-only recovery restarted the encoder");
            Check(NativeCoreService.OpenCaptureCalls == 1, "transport-only recovery opened another capture");
            Check(RemoteHostVideoTransport.CreatedCount == 2 && RemoteHostVideoTransport.StopCount == 1 &&
                RemoteHostVideoTransport.DisposeCount == 1,
                "transport-only recovery did not replace exactly one sender");
            Check(RemoteHostVideoTransport.MaxActiveSenders == 1,
                "transport-only recovery overlapped sender ownership");
            Check(RemoteSessionService.Instance.Snapshot.RemotePresentedFrames > presented,
                "transport recovery returned before a new client presentation");
            Check(VideoIncidentRecorder.Instance.Events.Contains("TRANSPORT_RECOVERY_SUCCESS"),
                "confirmed transport recovery success was not recorded");

            await controller.StopAsync();
        }
        finally { controller.Dispose(); }
    }

    private static async Task OldGenerationRejectedAsync()
    {
        FakeEnvironment.Reset();
        HostVideoPipelineController controller = NewController();
        try
        {
            Check(await controller.StartAsync(), "healthy initial host start failed");
            ConfirmInitialFrameChain(controller);
            int session = RemoteSessionService.Instance.Snapshot.SessionGeneration;
            long current = RemoteSessionService.Instance.Snapshot.StreamConfig!.Generation;
            int senders = RemoteHostVideoTransport.CreatedCount;
            int stops = RemoteHostVideoTransport.StopCount;
            int idrs = NativeCoreService.Captures.Single().RequestIdrCalls;

            bool accepted = await controller.RecoverHostVideoAsync(session, current - 1,
                recoveryRequestId: 77, presentedBaseline: 0);

            Check(!accepted, "an old stream generation was accepted");
            Check(RemoteHostVideoTransport.CreatedCount == senders, "old generation created a sender");
            Check(RemoteHostVideoTransport.StopCount == stops, "old generation stopped the current sender");
            Check(NativeCoreService.Captures.Single().RequestIdrCalls == idrs,
                "old generation requested an IDR from the current encoder");
            Check(controller.GetSnapshot().RemoteTransportRunning, "old generation disturbed the current sender");

            await controller.StopAsync();
        }
        finally { controller.Dispose(); }
    }

    private static async Task StopFailureNeverOverlapsAsync()
    {
        FakeEnvironment.Reset();
        HostVideoPipelineController controller = NewController();
        try
        {
            Check(await controller.StartAsync(), "healthy initial host start failed");
            NativeCaptureSession capture = NativeCoreService.Captures.Single();
            capture.StopEncoderOverride = () => throw new InvalidOperationException("injected encoder stop failure");
            InjectConfirmedStall(controller, capture);
            int session = RemoteSessionService.Instance.Snapshot.SessionGeneration;
            long stream = RemoteSessionService.Instance.Snapshot.StreamConfig!.Generation;

            bool recovered = await controller.RecoverHostPipelineAsync(session, stream);

            Check(!recovered, "recovery reported success after encoder stop failure");
            Check(GetState(controller) == HostPipelineState.FAULTED, "stop failure did not fault the pipeline");
            Check(NativeCoreService.OpenCaptureCalls == 1, "stop failure opened a second capture");
            Check(NativeCoreService.MaxActiveEncoders == 1 && NativeCoreService.ActiveEncoders == 1,
                "stop failure overlapped or abandoned the still-running encoder");
            Check(!capture.Disposed, "stop failure disposed a potentially running encoder");
            Check(controller.HasOwnedResources, "stop failure dropped ownership of the encoder");
            Check(typeof(HostTransmissionSnapshot).GetProperty("CanStop")?.GetValue(controller.GetSnapshot()) is true,
                "UI cannot retry Stop after a fault retained resources");
            Check(VideoIncidentRecorder.Instance.Events.Contains("ENCODER_RECOVERY_FAILED"),
                "stop failure was not exported as a recovery failure");
        }
        finally { controller.Dispose(); }
    }

    private static async Task StartFailureRollsBackAsync()
    {
        FakeEnvironment.Reset();
        NativeCoreService.OpenCaptureFactory = _ =>
        {
            NativeCaptureSession capture = FakeEnvironment.NewCapture();
            capture.StartEncoderOverride = _ => new EncoderStartResult
            {
                HResult = unchecked((int)0x80004005),
                Message = "injected NVENC start failure"
            };
            return new CaptureOpenResult { HResult = 0, Session = capture };
        };
        HostVideoPipelineController controller = NewController();
        try
        {
            bool started = await controller.StartAsync();
            NativeCaptureSession capture = NativeCoreService.Captures.Single();

            Check(!started, "StartAsync accepted an encoder start failure");
            Check(capture.StartEncoderCalls == 1, "failed encoder start was not attempted exactly once");
            Check(capture.StopEncoderCalls == 1 && capture.Disposed,
                "failed encoder start did not roll back its capture ownership");
            Check(RemoteHostVideoTransport.CreatedCount == 0, "failed encoder start created a sender");
            Check(NativeCoreService.ActiveEncoders == 0, "failed encoder start left an active encoder");
            Check(!controller.HasOwnedResources, "successful rollback retained lifecycle ownership");
            Check(GetState(controller) == HostPipelineState.STOPPED,
                "confirmed encoder rollback must allow retry");
            await controller.StopAsync();
            Check(GetState(controller) == HostPipelineState.STOPPED,
                "explicit stop after a clean failed start did not reset to STOPPED");
        }
        finally { controller.Dispose(); }
    }

    private static HostVideoPipelineController NewController() =>
        (HostVideoPipelineController)(Activator.CreateInstance(typeof(HostVideoPipelineController), nonPublic: true)
            ?? throw new InvalidOperationException("private controller constructor unavailable"));

    private static HostPipelineState GetState(HostVideoPipelineController controller) =>
        GetPrivateField<HostPipelineRecoveryTransaction>(controller, "_pipeline").State;

    private static void InjectConfirmedStall(HostVideoPipelineController controller, NativeCaptureSession capture)
    {
        SetFatalEvidence(capture, 200);
        long stream = RemoteSessionService.Instance.Snapshot.StreamConfig!.Generation;
        SetPrivateField(controller, "_confirmedEvidence", new EncoderStallEvidence(
            Environment.TickCount64,
            RemoteSessionService.Instance.Snapshot.SessionGeneration,
            stream,
            true,
            200,
            50,
            0,
            6000,
            false,
            false,
            4,
            4,
            4,
            6000,
            unchecked((int)0x800705B4),
            false));
    }

    private static void SetFatalEvidence(NativeCaptureSession capture, ulong arrived)
    {
        capture.CaptureStats.BackendFramesArrived = arrived;
        capture.CaptureStats.BackendFramesPublished = 50;
        capture.CaptureStats.BackendLastCallbackAgoMs = 0;
        capture.CaptureStats.LastPublishAgoMs = 6000;
        capture.CaptureStats.BackendStopInProgress = false;
        capture.EncoderStats.Running = false;
        capture.EncoderStats.OutputWorkerAlive = false;
        capture.EncoderStats.InFlightFrames = 4;
        capture.EncoderStats.InputSlots = 4;
        capture.EncoderStats.PendingOutputs = 4;
        capture.EncoderStats.OldestInFlightMs = 6000;
        capture.EncoderStats.LastStatus = unchecked((int)0x800705B4);
        capture.EncoderStats.StopInProgress = false;
    }

    private static void ConfirmInitialFrameChain(HostVideoPipelineController controller)
    {
        NativeCaptureSession capture = NativeCoreService.Captures.Last();
        controller.ObserveEncoderHealth(RemoteSessionService.Instance.GetSnapshot(),
            capture.CaptureStats, capture.EncoderStats);
    }

    private static T GetPrivateField<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new MissingFieldException(owner.GetType().FullName, name));

    private static void SetPrivateField<T>(object owner, string name, T value)
    {
        FieldInfo field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(owner.GetType().FullName, name);
        field.SetValue(owner, value);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string failure)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Check(predicate(), failure);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
