using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiverse.Services;

// Compatibility facade: existing UI bindings still address the same singleton.
// The controller owns every host encoder/capture/sender lifecycle operation.
internal static class HostTransmissionService
{
    internal static HostVideoPipelineController Instance => HostVideoPipelineController.Instance;
    internal static bool IsValueCreated => HostVideoPipelineController.IsValueCreated;
}

internal sealed partial class HostVideoPipelineController
{
    private readonly HostPipelineRecoveryTransaction _pipeline;
    private readonly EncoderStallDetector _stallDetector = new();
    private readonly HostPipelineRecoveryCoordinator _recoveryWork = new();
    private Task? _encoderStopTask;
    private Task? _captureDisposeTask;
    private EncoderStallEvidence _confirmedEvidence;
    private PipelineIdentity? _transportRecoveryIdentity;
    private EncoderConfig? _recoveryConfig;
    private MonitorInfo? _recoveryMonitor;

    private PipelineIdentity CurrentIdentity()
    {
        var s = _session.GetSnapshot();
        return new(s.SessionGeneration, s.StreamConfig?.Generation ?? 0, _session.GetVideoSessionToken(s.SessionGeneration));
    }

    private void ConfirmInitialProgressUnderGate()
    {
        if (_pipeline.State != HostPipelineState.STARTING) return;
        var progress = ((IHostPipelineRecoveryDriver)this).ReadProgress(); var identity = CurrentIdentity();
        if (((IHostPipelineRecoveryDriver)this).IsCurrent(identity) && progress.Published > 0 &&
            progress.Encoded > 0 && progress.Sent > 0 && progress.Complete > 0 && progress.Decoded > 0 &&
            progress.Presented > 0 && progress.LastPresentAgeMs >= 0 && progress.LastPresentAgeMs < 2000)
            _pipeline.SetState(HostPipelineState.RUNNING, identity, "INITIAL_FRAME_CHAIN_CONFIRMED");
    }

    internal void ObserveEncoderHealth(RemoteSessionSnapshot s, CapturePipelineStats c, EncoderStats e)
    {
        if (_pipeline.State == HostPipelineState.STARTING && _gate.Wait(0))
        {
            try
            {
                ConfirmInitialProgressUnderGate();
            }
            finally { _gate.Release(); }
        }
        if (_recoveryWork.TryTakeCompleted(out Task<bool>? done))
            _ = done!.Exception; // Observe unexpected failures without killing watchdog.
        var evidence = new EncoderStallEvidence(Environment.TickCount64, s.SessionGeneration,
            s.StreamConfig?.Generation ?? 0, s.Connected && s.Role == RemoteSessionRole.Host && _armed,
            (long)c.BackendFramesArrived, (long)c.BackendFramesPublished, c.BackendLastCallbackAgoMs,
            c.LastPublishAgoMs, e.Running, e.OutputWorkerAlive, e.InFlightFrames, e.InputSlots,
            e.PendingOutputs, e.OldestInFlightMs, e.LastStatus, e.StopInProgress || c.BackendStopInProgress);
        if (!_stallDetector.Observe(evidence) || _recoveryWork.Running || _pipeline.State == HostPipelineState.FAULTED) return;
        _confirmedEvidence = evidence;
        RecordRecovery("ENCODER_STALL_CONFIRMED", e.LastStatus);
        _recoveryWork.TryStart(() => RecoverHostPipelineAsync(evidence.Session, evidence.Stream), evidence.Now);
    }

    bool IHostPipelineRecoveryDriver.IsCurrent(PipelineIdentity id) => !_disposed &&
        !id.Token.IsCancellationRequested && _session.IsCurrentVideoStream(id.StreamGeneration,
            id.SessionGeneration, RemoteSessionRole.Host);

    bool IHostPipelineRecoveryDriver.NeedsRecovery(PipelineIdentity id, bool encoder)
    {
        if (!_armed || _recoveryRequiresManualStop || _capture is null || _activeEncoderConfig is null) return false;
        if (!encoder) return _transportRecoveryIdentity is { } requested && requested == id;
        var c = _capture.GetCapturePipelineStatsDiagnostic(); var e = _capture.GetEncoderStatsDiagnostic();
        if (c is null || e is null) return false;
        var old = _confirmedEvidence;
        if (old.Session != id.SessionGeneration || old.Stream != id.StreamGeneration ||
            Environment.TickCount64 - old.Now > 2000 || c.BackendFramesPublished != (ulong)old.Published ||
            c.BackendFramesArrived < (ulong)old.Arrived) return false;
        var fresh = old with { Now = Environment.TickCount64, Active = _armed,
            CallbackAgeMs = c.BackendLastCallbackAgoMs, PublishAgeMs = c.LastPublishAgoMs,
            EncoderRunning = e.Running, OutputWorkerAlive = e.OutputWorkerAlive, InFlight = e.InFlightFrames, Capacity = e.InputSlots,
            PendingOutputs = e.PendingOutputs, OldestAgeMs = e.OldestInFlightMs, Status = e.LastStatus };
        return fresh.CallbackAgeMs >= 0 && fresh.CallbackAgeMs <= 1000 &&
            fresh.PublishAgeMs >= EncoderStallDetector.RequiredPublishSilenceMs(fresh) &&
            !e.StopInProgress && !c.BackendStopInProgress && EncoderStallDetector.HasEncoderFault(fresh);
    }

    PipelineProgress IHostPipelineRecoveryDriver.ReadProgress()
    {
        var s = _session.GetSnapshot(); var c = _capture?.GetCapturePipelineStatsDiagnostic();
        var e = _capture?.GetEncoderStatsDiagnostic(); var t = _transport?.GetSnapshot();
        return new(s.SessionGeneration, s.StreamConfig?.Generation ?? 0, (long)(c?.BackendFramesPublished ?? 0),
            (long)(e?.EncodedFrames ?? 0), t?.SentFrames ?? 0, s.RemoteCompleteFrames,
            s.RemoteDecodedFrames, s.RemotePresentedFrames, s.RemoteLastPresentAgeMs);
    }

    void IHostPipelineRecoveryDriver.BlockSubmissions()
    {
        _recoveryConfig = _activeEncoderConfig is null ? null : CopyConfig(_activeEncoderConfig);
        _recoveryMonitor = _activeMonitor;
        _capture?.BlockEncoderSubmissions();
    }
    Task IHostPipelineRecoveryDriver.StopSenderAsync() { StopCurrentTransport(); return Task.CompletedTask; }
    Task IHostPipelineRecoveryDriver.StopEncoderAsync() => StopCurrentEncoderAsync();
    Task IHostPipelineRecoveryDriver.StartEncoderAsync()
    {
        if (_capture is not null || _encoderStopTask is not null || _captureDisposeTask is not null || _recoveryConfig is null || _recoveryMonitor is null)
            throw new InvalidOperationException("Encoder anterior sem parada confirmada ou configuração indisponível.");
        var open = NativeCoreService.OpenCapture(_recoveryMonitor.DeviceName);
        _capture = open.Session ?? throw new InvalidOperationException(open.Error ?? "Capture open failed");
        _ownsCapture = true; _activeEncoderConfig = CopyConfig(_recoveryConfig); _activeMonitor = _recoveryMonitor;
        _monitorDeviceName = _recoveryMonitor.DeviceName;
        var start = _capture.StartEncoder(_activeEncoderConfig);
        if (!start.Success) throw new InvalidOperationException(start.Message);
        Runtime.UpdateCapture(_capture, true);
        return Task.CompletedTask;
    }
    async Task<PipelineIdentity> IHostPipelineRecoveryDriver.StartSenderAsync(PipelineIdentity id, bool newGeneration)
    {
        // This method runs under the same gate as start/stop/UI intentions.
        if (!newGeneration)
        {
            if (!((IHostPipelineRecoveryDriver)this).IsCurrent(id)) return id;
            var capture = _capture ?? throw new InvalidOperationException("Capture owner lost");
            var endpoint = _session.GetRemoteVideoEndpoint() ?? throw new InvalidOperationException("Peer missing");
            var replacement = new RemoteHostVideoTransport(capture, endpoint, _session);
            _transport = replacement;
            replacement.Start();
            Runtime.AdoptHost(capture, replacement, true, _activeMonitor);
            return id;
        }
        PipelineIdentity allocated = id;
        try
        {
            await AttachRemoteTransportCoreAsync(id, generation => allocated = id with { StreamGeneration = generation }).ConfigureAwait(false);
            return allocated;
        }
        catch (Exception ex) { throw new PipelineBootstrapException(allocated, ex); }
    }
    int IHostPipelineRecoveryDriver.RequestIdr() => _capture?.RequestIdr() ?? unchecked((int)0x80004005);
    async Task IHostPipelineRecoveryDriver.FaultAsync(PipelineIdentity id, string reason)
    {
        _armed = false; _recoveryRequiresManualStop = true; _suppressAutoStartUntilDisconnect = true;
        _capture?.BlockEncoderSubmissions();
        _lastError = reason; _statusText = "Pipeline FAULTED: parada ou recuperação não confirmada. Use Parar para tentar liberar recursos.";
        // A hung native task keeps its capture and handle. Stop the sender if
        // possible, but never drop references or launch a replacement on error.
        try { StopCurrentTransport(); } catch (Exception ex) { StartupDiagnostics.RecordException("FAULTED_SENDER_STOP", ex); }
        try { await _session.PublishStreamStoppedAsync(id.SessionGeneration, id.StreamGeneration).ConfigureAwait(false); }
        catch (Exception ex) { StartupDiagnostics.RecordException("FAULTED_STREAM_STOP", ex); }
        RaiseStateChangedSafely();
    }
    void IHostPipelineRecoveryDriver.Record(string name, HostPipelineState previous, HostPipelineState next,
        PipelineIdentity id, long attempt, string reason, int status)
    {
        // Diagnostic accessors avoid the control/consumer gate held by native stop.
        var c = _capture?.GetCapturePipelineStatsDiagnostic(); var e = _capture?.GetEncoderStatsDiagnostic();
        var t = _transport?.GetSnapshot(); var s = _session.GetSnapshot();
        VideoIncidentRecorder.Instance.RecordEvent(name, new {
            monotonicMs = Environment.TickCount64, utc = DateTimeOffset.UtcNow,
            sessionGeneration = id.SessionGeneration, streamGeneration = id.StreamGeneration,
            recoveryAttemptId = attempt, previous = previous.ToString(), next = next.ToString(), reason,
            captureArrived = c?.BackendFramesArrived, capturePublished = c?.BackendFramesPublished,
            nvencRunning = e?.Running, outputWorkerAlive = e?.OutputWorkerAlive,
            inFlight = e?.InFlightFrames, pendingOutputs = e?.PendingOutputs, oldestMs = e?.OldestInFlightMs,
            senderRunning = t?.Running, sent = t?.SentFrames, remotePresented = s.RemotePresentedFrames,
            remotePresentAgeMs = s.RemoteLastPresentAgeMs, status, nativeStatus = e?.LastStatus,
            stopPending = _encoderStopTask is { IsCompleted: false }, stopStage = e?.StopStage });
    }
    private void RecordRecovery(string name, int status) => ((IHostPipelineRecoveryDriver)this).Record(name,
        _pipeline.State, _pipeline.State, CurrentIdentity(), _pipeline.Attempts, name, status);
    private static EncoderConfig CopyConfig(EncoderConfig c) => new() { Fps = c.Fps, BitrateKbps = c.BitrateKbps,
        Preset = c.Preset, UltraLowLatency = c.UltraLowLatency, MaxWidth = c.MaxWidth, MaxHeight = c.MaxHeight, LabInputMode = c.LabInputMode };
}
