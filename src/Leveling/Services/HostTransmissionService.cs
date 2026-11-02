using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    internal sealed class HostTransmissionSnapshot
    {
        public bool Armed { get; init; }
        public bool CanStop { get; init; }
        public bool EncoderRunning { get; init; }
        public bool RemoteTransportRunning { get; init; }
        public string MonitorDeviceName { get; init; } = "";
        public string StatusText { get; init; } = "Parado";
        public string LastError { get; init; } = "";
    }

    /// <summary>
    /// Safe one-click host orchestration. It intentionally does not start a preview:
    /// preview readback is diagnostic UI work and must not be required to stream.
    /// </summary>
    internal sealed partial class HostVideoPipelineController : IDisposable, IHostPipelineRecoveryDriver
    {
        private static readonly Lazy<HostVideoPipelineController> LazyInstance =
            new(() => new HostVideoPipelineController());

        public static HostVideoPipelineController Instance => LazyInstance.Value;
        internal static bool IsValueCreated => LazyInstance.IsValueCreated;

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _bootstrapSync = new();
        private CancellationTokenSource? _bootstrapCancellation;

        private readonly RemoteSessionService _session = RemoteSessionService.Instance;
        private RemoteStreamingRuntime Runtime => RemoteStreamingRuntime.Instance;
        private readonly MultiverseSettingsService _settings = MultiverseSettingsService.Instance;
        private RemoteAudioService Audio => RemoteAudioService.Instance;

        private NativeCaptureSession? _capture;
        private IVideoTransport? _transport;
        private bool _ownsCapture;
        private EncoderConfig? _activeEncoderConfig;
        private volatile bool _recoveryRequiresManualStop;
        private volatile bool _armed;
        private bool _suppressAutoStartUntilDisconnect;
        private volatile bool _disposed;
        private long _lastPermanentIdrAt;
        private readonly HostSenderRecoveryPolicy _senderRecoveryPolicy = new();
        private MonitorInfo? _activeMonitor;
        private string _monitorDeviceName = "";
        private string _statusText = "Parado";
        private string _lastError = "";

        public event EventHandler? StateChanged;

        private HostVideoPipelineController()
        {
            _pipeline = new HostPipelineRecoveryTransaction(this, _gate);
            _session.SessionChanged += Session_SessionChanged;
            _session.RemoteIdrRequested += Session_RemoteIdrRequested;
            _session.RemoteSenderRecoveryRequested += Session_RemoteSenderRecoveryRequested;
        }

        private void Session_RemoteSenderRecoveryRequested(int sessionGeneration,
            long streamGeneration, long requestId, long presentedBaseline)
        {
            // Never block the TCP reader on socket teardown or a native call.
            _ = Task.Run(async () =>
            {
                bool accepted = false;
                try
                {
                    accepted = await RecoverHostVideoAsync(sessionGeneration,
                        streamGeneration, requestId, presentedBaseline).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.RecordException("HOST_SENDER_RECOVERY_REQUEST_ERROR", ex);
                    VideoIncidentRecorder.Instance.RecordEvent("HOST_SENDER_RECOVERY_REQUEST_ERROR",
                        new { sessionGeneration, streamGeneration, requestId, ex.HResult });
                }
                try
                {
                    await _session.PublishSenderRecoveryResultAsync(sessionGeneration,
                        streamGeneration, requestId, accepted).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.RecordException("HOST_SENDER_RECOVERY_RESULT_SEND_ERROR", ex);
                    VideoIncidentRecorder.Instance.RecordEvent("HOST_SENDER_RECOVERY_RESULT_SEND_ERROR",
                        new { sessionGeneration, streamGeneration, requestId, accepted, ex.HResult });
                }
            });
        }

        private void Session_RemoteIdrRequested()
        {
            if (_disposed) return;
            RemoteSessionSnapshot session = _session.GetSnapshot();
            if (!session.Connected || session.Role != RemoteSessionRole.Host) return;
            // The runtime also supports manually-started capture on DevicesPage.
            // The permanent service, not a loaded Page, owns this notification.
            RemoteStreamingState state = Runtime.GetState();
            NativeCaptureSession? capture = state.Capture;
            if (capture is null || !state.EncoderRunning) return;
            long now = Environment.TickCount64;
            while (true)
            {
                long previous = Interlocked.Read(ref _lastPermanentIdrAt);
                if (previous != 0 && now - previous < 500) return;
                if (Interlocked.CompareExchange(ref _lastPermanentIdrAt, now, previous) == previous) break;
            }
            try
            {
                int result = TryRequestIdr(capture, new PipelineIdentity(session.SessionGeneration,
                    session.StreamConfig?.Generation ?? 0, _session.GetVideoSessionToken(session.SessionGeneration)));
                if (result < 0)
                {
                    VideoIncidentRecorder.Instance.RecordEvent("HOST_IDR_ERROR",
                        new { HResult = result, session = session.SessionGeneration });
                    return;
                }
                VideoIncidentRecorder.Instance.RecordEvent("HOST_IDR_REQUESTED",
                    new { HResult = result, session = session.SessionGeneration });
            }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("HOST_IDR_ERROR", ex);
                VideoIncidentRecorder.Instance.RecordEvent("HOST_IDR_ERROR", new { ex.HResult });
            }
        }

        internal bool HasOwnedResources => _disposed || _gate.CurrentCount == 0 ||
            _armed || _capture is not null || _transport is not null ||
            _recoveryRequiresManualStop;

        internal bool OwnsCapture(NativeCaptureSession? capture) =>
            capture is not null && ReferenceEquals(_capture, capture) && _ownsCapture;

        internal bool IsUsingCapture(NativeCaptureSession? capture) =>
            capture is not null && ReferenceEquals(_capture, capture);

        internal bool IsUsingTransport(IVideoTransport? transport) =>
            transport is not null && ReferenceEquals(_transport, transport);

        internal Task StopForDisconnectedSessionAsync(int sessionGeneration) =>
            HandleSessionDisconnectedAsync(sessionGeneration);

        public HostTransmissionSnapshot GetSnapshot()
        {
            return new HostTransmissionSnapshot
            {
                Armed = _armed,
                CanStop = _armed || _capture is not null || _transport is not null || _recoveryRequiresManualStop,
                EncoderRunning = _capture?.GetEncoderStatsDiagnostic()?.Running == true,
                RemoteTransportRunning = _transport?.Running == true,
                MonitorDeviceName = _monitorDeviceName,
                StatusText = _statusText,
                LastError = _lastError
            };
        }

        public async Task<bool> StartAsync()
        {
            if (_disposed)
                return false;

            await _gate.WaitAsync();
            int startingSession = _session.GetSnapshot().SessionGeneration;
            try
            {
                if (_recoveryRequiresManualStop || _pipeline.State == HostPipelineState.FAULTED)
                {
                    _lastError = "Pare a transmissão antes de iniciar após uma recuperação incompleta.";
                    _statusText = _lastError;
                    RaiseStateChangedSafely();
                    return false;
                }
                _suppressAutoStartUntilDisconnect = false;
                _pipeline.SetState(HostPipelineState.STARTING, CurrentIdentity(), "START_INTENT");
                _lastError = "";

                if (_capture is not null && _capture.GetEncoderStats()?.Running != true)
                {
                    StopCurrentTransport();
                    await StopCurrentEncoderAsync(stopExternal: _ownsCapture).ConfigureAwait(false);
                }
                if (_capture is null || _capture.GetEncoderStats()?.Running != true)
                {
                    if (!TryAdoptExistingRuntimeCapture())
                    {
                        if (!TryCreateCaptureAndEncoder(out string error))
                        {
                            _armed = false;
                            _statusText = "Não foi possível iniciar a transmissão.";
                            _lastError = error;
                            _pipeline.SetState(HostPipelineState.FAULTED, CurrentIdentity(), "START_FAILED");
                            RaiseStateChangedSafely();
                            return false;
                        }
                    }
                }

                _armed = true; _manualMode = false;
                _statusText = "Transmissão preparada.";
                Runtime.UpdateCapture(_capture, encoderRunning: true);

                RemoteSessionSnapshot session = _session.GetSnapshot();
                if (session.Connected && session.Role == RemoteSessionRole.Host)
                    await AttachRemoteTransportCoreAsync();
                else
                    _statusText = "Pronto para transmitir • aguardando conexão de um cliente.";

                ConfirmInitialProgressUnderGate();
                RaiseStateChangedSafely();
                return true;
            }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("HOST_START_ERROR", ex);
                VideoIncidentRecorder.Instance.RecordEvent("HOST_START_ERROR", new { ex.HResult });
                VideoIncidentRecorder.Instance.MarkIncident("HOST_START_ERROR", 0);
                await AbortVideoBootstrapAsync(startingSession);
                if (!_recoveryRequiresManualStop) _lastError = ex.Message;
                if (!_recoveryRequiresManualStop)
                    _statusText = "Falha ao iniciar transmissão.";
                RaiseStateChangedSafely();
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task StopAsync()
        {
            CancelBootstrap();
            // An explicit Stop is also the retry path after an incomplete
            // shutdown, including a failed application Dispose.
            await _gate.WaitAsync();
            try
            {
                _armed = false;
                _pipeline.SetState(HostPipelineState.STOPPING, CurrentIdentity(), "STOP_INTENT");
                _suppressAutoStartUntilDisconnect = true;

                RemoteSessionSnapshot session = _session.GetSnapshot();
                if (session.Connected && session.Role == RemoteSessionRole.Host)
                {
                    try { await _session.PublishStreamStoppedAsync(); } catch { }
                }

                Audio.StopHostCaptureOnly();

                try
                {
                    StopCurrentTransport();
                    await StopCurrentEncoderAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    BlockIncompleteShutdown("HOST_STOP_INCOMPLETE", ex);
                    return;
                }

                _recoveryRequiresManualStop = false;
                _pipeline.SetState(HostPipelineState.STOPPED, CurrentIdentity(), "STOP_CONFIRMED");
                _statusText = "Transmissão parada.";
                _lastError = "";
                RaiseStateChangedSafely();
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task RollbackStartFailureAsync()
        {
            _armed = false;
            try
            {
                StopCurrentTransport();
                // An adopted manual capture is not owned by this failed start.
                await StopCurrentEncoderAsync(stopExternal: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                BlockIncompleteShutdown("HOST_START_ROLLBACK_INCOMPLETE", ex);
            }
        }

        private async Task AbortVideoBootstrapAsync(int sessionGeneration)
        {
            // Retire the advertised generation so the client stops retrying a
            // stream that never acquired a sender. Never retire a reconnected session.
            _armed = false;
            _suppressAutoStartUntilDisconnect = true;
            try { await _session.PublishStreamStoppedAsync(sessionGeneration); }
            catch (Exception ex) { StartupDiagnostics.RecordException("HOST_BOOTSTRAP_STOP_NOTIFY_ERROR", ex); }
            await RollbackStartFailureAsync();
            _pipeline.SetState(_recoveryRequiresManualStop ? HostPipelineState.FAULTED : HostPipelineState.STOPPED,
                CurrentIdentity(), _recoveryRequiresManualStop ? "BOOTSTRAP_ROLLBACK_RETAINED" : "BOOTSTRAP_ROLLBACK_CONFIRMED");
        }

        private bool TryAdoptExistingRuntimeCapture()
        {
            RemoteStreamingState state = Runtime.GetState();
            if (state.Capture is null || !state.EncoderRunning ||
                state.Capture.GetEncoderStats()?.Running != true)
                return false;

            _capture = state.Capture;
            _transport = state.Transport;
            _ownsCapture = false;
            _activeEncoderConfig = null;
            _activeMonitor = state.HostMonitor;
            _monitorDeviceName =
                _activeMonitor?.DeviceName ??
                _settings.GetStreamProfile().MonitorDeviceName;
            return true;
        }

        private void SynchronizeTransportFromRuntime()
        {
            NativeCaptureSession? capture = _capture;
            if (capture is null) return;
            RemoteStreamingState state = Runtime.GetState();
            if (!ReferenceEquals(state.Capture, capture)) return;
            IVideoTransport? authoritative = state.Transport;
            if (ReferenceEquals(_transport, authoritative)) return;
            // Never overwrite the only reference to an old sender before its
            // worker has terminated. A failed stop blocks the replacement.
            ConfirmedResource.StopAndRelease(ref _transport,
                t => t.Stop(), t => t.Dispose());
            _transport = authoritative;
        }

        private void StopCurrentTransport()
        {
            SynchronizeTransportFromRuntime();
            IVideoTransport? transport = _transport;
            if (transport is null) return;
            // Keep both owners pointing at the same resource until Stop and
            // Dispose succeed. A timeout must not orphan a live sender.
            ConfirmedResource.StopAndRelease(ref _transport,
                t => t.Stop(), t => t.Dispose());
            if (ReferenceEquals(Runtime.GetState().Transport, transport))
                Runtime.UpdateTransport(null);
        }

        private async Task StopCurrentEncoderAsync(bool stopExternal = true)
        {
            NativeCaptureSession? capture = _capture;
            if (capture is null) return;
            bool owns = _ownsCapture;
            if (!owns && !stopExternal)
            {
                _capture = null;
                _ownsCapture = false;
                _activeEncoderConfig = null;
                _activeMonitor = null;
                return;
            }
            // A completed native call is required before dropping ownership.
            // No timeout cancels or abandons a potentially active GPU handle.
            // Once capture disposal has started, never re-enter the native
            // encoder on that capture, even if the previous wait timed out.
            if (_captureDisposeTask is null)
                await ConfirmEncoderStopAsync(capture).ConfigureAwait(false);
            if (ReferenceEquals(Runtime.GetState().Capture, capture))
                Runtime.UpdateCapture(owns ? null : capture, encoderRunning: false);
            if (owns)
            {
                _captureDisposeTask ??= Task.Run(capture.Dispose);
                await _captureDisposeTask.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                _captureDisposeTask = null;
            }
            _capture = null;
            _ownsCapture = false;
            _activeEncoderConfig = null;
            _activeMonitor = null;
            _monitorDeviceName = "";
            _recoveryRequiresManualStop = false;
        }

        private void BlockIncompleteShutdown(string stage, Exception ex)
        {
            _armed = false;
            _suppressAutoStartUntilDisconnect = true;
            _recoveryRequiresManualStop = true;
            _pipeline.SetState(HostPipelineState.FAULTED, CurrentIdentity(), stage);
            _lastError = ex.Message;
            _statusText = "Parada incompleta. Reinício bloqueado; tente Parar novamente ou reinicie o aplicativo.";
            StartupDiagnostics.RecordException(stage, ex);
            VideoIncidentRecorder.Instance.MarkIncident(stage, 0);
            RaiseStateChangedSafely();
        }

        internal async Task<bool> RecoverHostVideoAsync(int sessionGeneration, long streamGeneration,
            long recoveryRequestId = 0, long presentedBaseline = 0)
        {
            var identity = new PipelineIdentity(sessionGeneration, streamGeneration, _session.GetVideoSessionToken(sessionGeneration));
            try { await _gate.WaitAsync(identity.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            try
            {
                if (_disposed || !_armed || _recoveryRequiresManualStop || _capture is null ||
                    !((IHostPipelineRecoveryDriver)this).IsCurrent(identity)) return false;
                SynchronizeTransportFromRuntime();
                // Transport recovery cannot make progress while NVENC owns all
                // stalled slots. Leave the gate/budget for encoder recovery.
                var encoder = _capture.GetEncoderStatsDiagnostic();
                if (encoder is not null &&
                    (EncoderStallDetector.IsTerminalOutputTimeout(encoder.Running,
                        encoder.OutputWorkerAlive, encoder.LastStatus, encoder.InputSlots,
                        encoder.InFlightFrames, encoder.PendingOutputs, encoder.OldestInFlightMs) ||
                     (!encoder.OutputWorkerAlive && encoder.InputSlots > 0 &&
                      encoder.InFlightFrames >= encoder.InputSlots && encoder.OldestInFlightMs >= 5000)))
                { RecordRecovery("TRANSPORT_REJECTED_ENCODER_STALL", encoder.LastStatus); return false; }
                var endpoint = _session.GetRemoteVideoEndpoint();
                if (endpoint is null) return false;
                if (recoveryRequestId == 0 && _transport is RemoteHostVideoTransport current &&
                    current.Running && current.IsCurrentStream && current.RemoteEndpoint.Equals(endpoint)) return true;
                if (recoveryRequestId > 0)
                {
                    if (!_senderRecoveryPolicy.TryRegisterRequest(sessionGeneration, streamGeneration, recoveryRequestId, out string rejection))
                    { RecordRecovery("TRANSPORT_REQUEST_REJECTED_" + rejection, 0); return false; }
                    var latest = _session.GetSnapshot();
                    if (latest.RemoteTelemetryAgeMs >= 0 && latest.RemoteTelemetryAgeMs < 750 && latest.RemotePresentedFrames > presentedBaseline)
                    { RecordRecovery("TRANSPORT_CANCELLED_HEALTHY", 0); return false; }
                    if (!_senderRecoveryPolicy.TryConsumeAttempt(Environment.TickCount64, out rejection))
                    { RecordRecovery("TRANSPORT_REQUEST_REJECTED_" + rejection, 0); return false; }
                }
                _transportRecoveryIdentity = identity;
                return await _pipeline.RecoverWhileGateHeldAsync(identity, false).ConfigureAwait(false);
            }
            finally { _transportRecoveryIdentity = null; _gate.Release(); }
        }

        internal Task<bool> RecoverHostPipelineAsync(int sessionGeneration, long streamGeneration) =>
            _pipeline.RecoverAsync(new PipelineIdentity(sessionGeneration, streamGeneration,
                _session.GetVideoSessionToken(sessionGeneration)), true);

        private bool TryCreateCaptureAndEncoder(out string error)
        {
            error = "";
            if (_capture is not null || _transport is not null)
                throw new InvalidOperationException("Uma captura anterior ainda possui recursos. Pare-a antes de iniciar outra.");
            MultiverseStreamProfile profile = _settings.GetStreamProfile();
            HardwareSnapshot hardware = NativeCoreService.ReadHardware();

            if (!hardware.NativeCoreLoaded)
            {
                error = string.IsNullOrWhiteSpace(hardware.Error)
                    ? "O núcleo nativo do Leveling não está disponível."
                    : hardware.Error!;
                return false;
            }

            MonitorInfo? monitor = hardware.Monitors.FirstOrDefault(
                item =>
                    item.DesktopDuplicationReady &&
                    !string.IsNullOrWhiteSpace(profile.MonitorDeviceName) &&
                    string.Equals(
                        item.DeviceName,
                        profile.MonitorDeviceName,
                        StringComparison.OrdinalIgnoreCase));

            monitor ??= hardware.Monitors.FirstOrDefault(
                item => item.DesktopDuplicationReady && item.IsPrimary);
            monitor ??= hardware.Monitors.FirstOrDefault(
                item => item.DesktopDuplicationReady);

            if (monitor is null)
            {
                error = "Nenhum monitor compatível com captura foi encontrado.";
                return false;
            }

            CaptureOpenResult open = NativeCoreService.OpenCapture(monitor.DeviceName);
            if (open.Session is null)
            {
                error = open.Error ?? "Falha ao abrir a captura do monitor.";
                return false;
            }

            NativeCaptureSession capture = open.Session;
            EncoderConfig config = new()
            {
                Fps = profile.Fps,
                MaxWidth = profile.MaxWidth,
                MaxHeight = profile.MaxHeight,
                BitrateKbps = profile.BitrateKbps,
                Preset = profile.Preset,
                UltraLowLatency = profile.UltraLowLatency,
                LabInputMode = profile.PipelineMode
            };
            _capture = capture;
            _ownsCapture = true;
            _activeEncoderConfig = config;
            EncoderStartResult encoder = capture.StartEncoder(config);
            if (!encoder.Success)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(encoder.Message)
                    ? "O NVENC não iniciou." : encoder.Message);

            _activeMonitor = monitor;
            _monitorDeviceName = monitor.DeviceName;
            _settings.SaveMonitor(monitor.DeviceName);
            return true;
        }

        private CancellationTokenSource BeginBootstrap()
        {
            lock (_bootstrapSync)
            {
                var cts = new CancellationTokenSource();
                _bootstrapCancellation = cts;
                return cts;
            }
        }

        private void CancelBootstrap()
        {
            lock (_bootstrapSync)
            {
                try { _bootstrapCancellation?.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        private void FinishBootstrap(CancellationTokenSource cts)
        {
            lock (_bootstrapSync)
            {
                if (ReferenceEquals(_bootstrapCancellation, cts))
                    _bootstrapCancellation = null;
            }
            cts.Dispose();
        }

        private async Task AttachRemoteTransportCoreAsync(PipelineIdentity? expected = null, Action<long>? allocated = null)
        {
            NativeCaptureSession? capture = _capture;
            if (!_armed || capture is null) return;
            if (expected is { } required && !_session.IsCurrentVideoStream(required.StreamGeneration,
                required.SessionGeneration, RemoteSessionRole.Host)) return;

            SynchronizeTransportFromRuntime();
            if (_transport?.Running == true)
            {
                IPEndPoint? currentEndpoint = _session.GetRemoteVideoEndpoint();
                if (_transport is RemoteHostVideoTransport host &&
                    host.IsCurrentStream &&
                    currentEndpoint is not null && host.RemoteEndpoint.Equals(currentEndpoint))
                {
                    _statusText = "Transmitindo.";
                    return;
                }
                StopCurrentTransport();
            }

            RemoteSessionSnapshot session = _session.GetSnapshot();
            if (!session.Connected || session.Role != RemoteSessionRole.Host)
            {
                _statusText = "Pronto para transmitir • aguardando conexão de um cliente.";
                return;
            }

            IPEndPoint? remote = _session.GetRemoteVideoEndpoint();
            if (remote is null)
                throw new InvalidOperationException("A sessão não possui um endpoint UDP remoto.");

            MultiverseStreamProfile profile = _settings.GetStreamProfile();
            EncoderStats? encoder = capture.GetEncoderStats();
            uint streamWidth = encoder?.Width > 0 ? encoder.Width : capture.SourceWidth;
            uint streamHeight = encoder?.Height > 0 ? encoder.Height : capture.SourceHeight;
            uint fps = encoder?.TargetFps > 0 ? encoder.TargetFps : profile.Fps;
            uint bitrate = encoder?.BitrateKbps > 0 ? encoder.BitrateKbps : profile.BitrateKbps;

            int sessionGeneration = expected?.SessionGeneration ?? session.SessionGeneration;
            if (session.SessionGeneration != sessionGeneration) return;
            long streamGeneration = await _session.PublishStreamConfigAsync(
                streamWidth, streamHeight, fps, bitrate, sessionGeneration);
            if (streamGeneration <= 0)
                throw new InvalidOperationException("A sessão mudou antes de publicar o vídeo.");

            allocated?.Invoke(streamGeneration);

            // Start automatic audio in parallel with video bootstrap. Audio never
            // gates NVENC/UDP startup.
            _ = Audio.EnsureAutomaticForTransmissionAsync();

            // A bounded, cancellable handshake replaces the old fixed delay.
            // Never send the first GOP to a receiver that did not acknowledge it.
            using CancellationTokenSource bootstrap = BeginBootstrap();
            try
            {
                if (!await _session.WaitForRemoteVideoReadyAsync(
                    streamGeneration, sessionGeneration, TimeSpan.FromSeconds(5),
                    bootstrap.Token))
                {
                    string reason = _session.GetVideoStartupFailure(
                        streamGeneration, sessionGeneration);
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(reason)
                            ? "O cliente não confirmou VIDEO READY em 5 s. Abra Diagnósticos no cliente para verificar a preparação de NVDEC/UDP."
                            : "O cliente falhou ao preparar o vídeo: " + reason);
                }
            }
            finally
            {
                FinishBootstrap(bootstrap);
            }

            session = _session.GetSnapshot();
            if (!_armed || !session.Connected || session.Role != RemoteSessionRole.Host ||
                !_session.IsCurrentVideoStream(streamGeneration, sessionGeneration,
                    RemoteSessionRole.Host))
                return;

            SynchronizeTransportFromRuntime();
            if (_transport?.Running == true)
            {
                if (_transport is RemoteHostVideoTransport ready && ready.IsCurrentStream &&
                    ready.RemoteEndpoint.Equals(_session.GetRemoteVideoEndpoint()))
                {
                    _statusText = "Transmitindo.";
                    return;
                }
                StopCurrentTransport();
            }

            remote = _session.GetRemoteVideoEndpoint();
            if (remote is null)
                throw new InvalidOperationException("A sessão não possui um endpoint UDP remoto após o bootstrap.");

            var transport = new RemoteHostVideoTransport(capture, remote, _session);
            _transport = transport;
            transport.Start();
            int idr = capture.RequestIdr();
            if (idr < 0) throw new InvalidOperationException("RequestIdr rejected: 0x" + idr.ToString("X8"));

            Runtime.AdoptHost(
                capture,
                transport,
                encoderRunning: true,
                _activeMonitor);
            _statusText = "Transmitindo.";
        }

        private void Session_SessionChanged(object? sender, EventArgs e)
        {
            if (_disposed)
                return;

            RemoteSessionSnapshot snapshot = _session.GetSnapshot();

            if (!_armed)
            {
                if (!snapshot.Connected)
                {
                    _suppressAutoStartUntilDisconnect = false;
                    return;
                }

                MultiverseAppPreferences preferences = _settings.GetAppPreferences();
                if (!_suppressAutoStartUntilDisconnect && !_recoveryRequiresManualStop &&
                    snapshot.Role == RemoteSessionRole.Host &&
                    preferences.AutoStartHostStream)
                {
                    _ = StartAsync();
                }
                return;
            }

            if (!snapshot.Connected)
            {
                CancelBootstrap();
                _ = HandleSessionDisconnectedAsync(snapshot.SessionGeneration);
                return;
            }

            if (snapshot.Role == RemoteSessionRole.Host &&
                (_transport?.Running != true ||
                 (_transport is RemoteHostVideoTransport host &&
                  _session.GetRemoteVideoEndpoint() is IPEndPoint endpoint &&
                  (!host.IsCurrentStream || !host.RemoteEndpoint.Equals(endpoint)))))
                _ = TryAttachRemoteAsync();
        }

        private async Task HandleSessionDisconnectedAsync(int disconnectedSessionGeneration)
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed)
                    return;

                RemoteSessionSnapshot current = _session.GetSnapshot();
                if (current.Connected || current.SessionGeneration != disconnectedSessionGeneration)
                    return;

                _armed = false;
                Audio.StopHostCaptureOnly();

                try
                {
                    StopCurrentTransport();
                    await StopCurrentEncoderAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    BlockIncompleteShutdown("HOST_DISCONNECT_STOP_INCOMPLETE", ex);
                    return;
                }

                _statusText = "Conexão encerrada • transmissão parada.";
                _lastError = "";
                RaiseStateChangedSafely();
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task TryAttachRemoteAsync()
        {
            await _gate.WaitAsync();
            int startingSession = _session.GetSnapshot().SessionGeneration;
            try
            {
                if (!_armed || _disposed)
                    return;

                try
                {
                    await AttachRemoteTransportCoreAsync();
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.RecordException("HOST_ATTACH_ERROR", ex);
                    await AbortVideoBootstrapAsync(startingSession);
                    _lastError = ex.Message;
                    _statusText = "Falha ao iniciar o transporte remoto.";
                }

                RaiseStateChangedSafely();
            }
            finally
            {
                _gate.Release();
            }
        }

        private void RaiseStateChangedSafely()
        {
            EventHandler? handlers = StateChanged;
            if (handlers is null)
                return;

            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try { handler(this, EventArgs.Empty); } catch { }
            }
        }

        public void Dispose()
        {
            CancelBootstrap();
            // A previous Dispose may have timed out. Do not abandon the
            // remaining owner: repeated disposal must retry the teardown.
            _disposed = true;
            _session.SessionChanged -= Session_SessionChanged;
            _session.RemoteIdrRequested -= Session_RemoteIdrRequested;
            _session.RemoteSenderRecoveryRequested -= Session_RemoteSenderRecoveryRequested;
            if (!_gate.Wait(TimeSpan.FromSeconds(2)))
            {
                VideoIncidentRecorder.Instance.RecordEvent("HOST_DISPOSE_DEFERRED");
                return;
            }
            try
            {
                try
                {
                    StopCurrentTransport();
                    // Do not abandon the owner while a native Stop is blocked.
                    StopCurrentEncoderAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    BlockIncompleteShutdown("HOST_DISPOSE_INCOMPLETE", ex);
                }
            }
            finally { _gate.Release(); }
        }
    }
}
