using System;
using System.Threading.Tasks;

namespace Multiverse.Services;

internal sealed partial class HostVideoPipelineController
{
    private bool _manualMode;
    internal bool CanUseManualPipeline(NativeCaptureSession? capture) => !_disposed && (!_armed || _manualMode) &&
        !_recoveryRequiresManualStop && _pipeline.State != HostPipelineState.FAULTED &&
        (_capture is null || (_manualMode && ReferenceEquals(_capture, capture)));
    internal bool IsAutomaticCapture(NativeCaptureSession? capture) => IsUsingCapture(capture) && !_manualMode;
    internal bool IsAutomaticTransport(IVideoTransport? transport) => IsUsingTransport(transport) && !_manualMode;

    internal async Task<CaptureOpenResult> OpenPreviewAsync(MonitorInfo monitor)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _capture is not null || _transport is not null || _recoveryRequiresManualStop)
                throw new InvalidOperationException("O pipeline anterior ainda possui recursos.");
            var result = await Task.Run(() => NativeCoreService.OpenCapture(monitor.DeviceName)).ConfigureAwait(false);
            if (result.Session is not null)
            {
                _capture = result.Session; _ownsCapture = true; _manualMode = true;
                _activeMonitor = monitor; _monitorDeviceName = monitor.DeviceName;
                Runtime.UpdateCapture(_capture, false);
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    private void RequireManualOwner(NativeCaptureSession capture)
    {
        if (_disposed || !_manualMode || !ReferenceEquals(_capture, capture))
            throw new InvalidOperationException("Intenção manual obsoleta ou transmissão principal ativa.");
    }
    internal EncoderStartResult StartManualEncoder(NativeCaptureSession capture, EncoderConfig config)
    {
        if (!_gate.Wait(0)) return new() { HResult = unchecked((int)0x800700AA), Message = "Pipeline ocupado." };
        try
        {
            RequireManualOwner(capture);
            if (_encoderStopTask is not null || _captureDisposeTask is not null || _recoveryRequiresManualStop || _pipeline.State == HostPipelineState.FAULTED)
                throw new InvalidOperationException("Parada anterior não confirmada.");
            var result = capture.StartEncoder(CopyConfig(config));
            if (result.Success) { _activeEncoderConfig = CopyConfig(config); Runtime.UpdateCapture(capture, true); }
            return result;
        }
        finally { _gate.Release(); }
    }
    internal async Task StopManualEncoderAsync(NativeCaptureSession capture, bool disposeCapture = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireManualOwner(capture);
            _armed = false; _suppressAutoStartUntilDisconnect = true;
            StopCurrentTransport();
            if (disposeCapture || _captureDisposeTask is not null) await StopCurrentEncoderAsync().ConfigureAwait(false);
            else
            {
                await ConfirmEncoderStopAsync(capture).ConfigureAwait(false);
                _activeEncoderConfig = null; _recoveryRequiresManualStop = false;
                Runtime.UpdateCapture(capture, false);
            }
            _pipeline.SetState(HostPipelineState.STOPPED, CurrentIdentity(), "MANUAL_STOP_CONFIRMED");
        }
        catch (Exception ex) { BlockIncompleteShutdown("MANUAL_STOP_INCOMPLETE", ex); throw; }
        finally { _gate.Release(); }
    }
    internal async Task<IVideoTransport?> StartManualRemoteTransportAsync(NativeCaptureSession capture)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireManualOwner(capture);
            if (_recoveryRequiresManualStop || _activeEncoderConfig is null) throw new InvalidOperationException("Encoder indisponível.");
            _armed = true;
            await AttachRemoteTransportCoreAsync().ConfigureAwait(false);
            return _transport;
        }
        catch (Exception ex) { BlockIncompleteShutdown("MANUAL_SENDER_START_FAILED", ex); throw; }
        finally { _gate.Release(); }
    }
    internal IVideoTransport StartManualLoopback(NativeCaptureSession capture, Action<ReassembledVideoFrame> receive)
    {
        if (!_gate.Wait(0)) throw new InvalidOperationException("Pipeline ocupado.");
        try
        {
            RequireManualOwner(capture);
            if (_recoveryRequiresManualStop || _transport is not null) throw new InvalidOperationException("Sender anterior não liberado.");
            var transport = new LoopbackVideoTransport(capture); _transport = transport;
            transport.FrameReceived += receive;
            transport.Start(); Runtime.UpdateTransport(transport);
            return transport;
        }
        catch (Exception ex) { BlockIncompleteShutdown("LOOPBACK_START_FAILED", ex); throw; }
        finally { _gate.Release(); }
    }
    internal void StopManualTransport(IVideoTransport transport)
    {
        if (!_gate.Wait(0)) throw new InvalidOperationException("Pipeline ocupado.");
        try
        {
            if (!_manualMode || !ReferenceEquals(_transport, transport))
                throw new InvalidOperationException("Sender pertence a outra intenção.");
            _armed = false; _suppressAutoStartUntilDisconnect = true;
            StopCurrentTransport();
        }
        catch (Exception ex) { BlockIncompleteShutdown("MANUAL_SENDER_STOP_INCOMPLETE", ex); throw; }
        finally { _gate.Release(); }
    }
    internal int TryRequestIdr(NativeCaptureSession? capture, PipelineIdentity? expected = null)
    {
        if (capture is null || !_gate.Wait(0)) return unchecked((int)0x800700AA);
        try
        {
            if (_disposed || !ReferenceEquals(_capture, capture) || _encoderStopTask is not null || _captureDisposeTask is not null || _recoveryRequiresManualStop ||
                (expected is { } id && !((IHostPipelineRecoveryDriver)this).IsCurrent(id))) return unchecked((int)0x80004005);
            return capture.RequestIdr();
        }
        finally { _gate.Release(); }
    }
    private async Task ConfirmEncoderStopAsync(NativeCaptureSession capture)
    {
        _encoderStopTask ??= Task.Run(capture.StopEncoder);
        try { await _encoderStopTask.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
        catch (TimeoutException) { RecordRecovery("ENCODER_STOP_TIMEOUT", unchecked((int)0x800705B4)); throw; }
        _encoderStopTask = null;
    }

    internal async Task RefreshStreamGeometryAsync(NativeCaptureSession? capture)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_armed || capture is null || !ReferenceEquals(_capture, capture) || _recoveryRequiresManualStop) return;
            var identity = CurrentIdentity(); var config = _session.GetSnapshot().StreamConfig;
            var encoder = capture.GetEncoderStatsDiagnostic();
            if (!((IHostPipelineRecoveryDriver)this).IsCurrent(identity) || config is null || encoder is null ||
                (config.Width == encoder.Width && config.Height == encoder.Height)) return;
            StopCurrentTransport();
            await AttachRemoteTransportCoreAsync(identity).ConfigureAwait(false);
        }
        catch (Exception ex) { BlockIncompleteShutdown("GEOMETRY_BOOTSTRAP_FAILED", ex); }
        finally { _gate.Release(); }
    }
}
