using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace Multiverse.Services
{
    internal sealed class RemoteStreamingState
    {
        public NativeCaptureSession? Capture { get; init; }
        public IVideoTransport? Transport { get; init; }
        public NativeNvdecPlayer? Decoder { get; init; }
        public bool EncoderRunning { get; init; }
        public MonitorInfo? HostMonitor { get; init; }
    }

    /// <summary>
    /// Process-wide owner of the active video path. Pages are control panels;
    /// navigating away from a Page must never become a streaming lifecycle event.
    /// </summary>
    internal sealed class RemoteStreamingRuntime : IDisposable
    {
        private static readonly Lazy<RemoteStreamingRuntime> LazyInstance =
            new(() => new RemoteStreamingRuntime());

        public static RemoteStreamingRuntime Instance => LazyInstance.Value;
        internal static bool IsValueCreated => LazyInstance.IsValueCreated;

        private readonly object _sync = new();
        private readonly SemaphoreSlim _recoveryGate = new(1, 1);
        private Task _videoCleanupTask = Task.CompletedTask;
        private readonly List<IVideoTransport> _retiredTransports = new();
        private readonly List<NativeNvdecPlayer> _retiredDecoders = new();
        private readonly List<RetiredWindow> _retiredWindows = new();
        private sealed class RetiredWindow
        {
            internal required RemoteSessionWindow Window;
            internal bool Close;
            internal bool Detached;
        }

        private long _streamGeneration;
        private readonly RemoteSessionService _session = RemoteSessionService.Instance;
        private readonly CancellationTokenSource _lifetime = new();
        private UiThreadOperation? _uiOperation;

        internal void InitializeUi(DispatcherQueue dispatcher)
        {
            if (!dispatcher.HasThreadAccess) throw new InvalidOperationException("A interface precisa ser registrada pela janela principal.");
            _uiOperation = new UiThreadOperation(() => dispatcher.HasThreadAccess, action => dispatcher.TryEnqueue(() => action()));
        }

        private NativeCaptureSession? _capture;
        private IVideoTransport? _transport;
        private NativeNvdecPlayer? _decoder;
        private bool _encoderRunning;
        private uint _decoderWidth;
        private uint _decoderHeight;
        private long _decoderRemoteGeneration;
        private int _decoderRemoteSessionGeneration;
        private RemoteSessionWindow? _sessionWindow;

        // CORE 11.52b: host cursor telemetry belongs to the process-wide
        // streaming runtime, not to DevicesPage. This keeps the remote cursor
        // alive while navigating between pages or hiding Leveling in the tray.
        private MonitorInfo? _hostCursorMonitor;
        private CancellationTokenSource? _cursorTelemetryCancel;
        private Task? _cursorTelemetryTask;
        private long _lastCursorSentAt;
        private bool _lastCursorVisible;
        private double _lastCursorX = -1;
        private double _lastCursorY = -1;

        private long _lastRecoveryAttempt;
        private int _recoveryAttempts;
        private int _recoverySessionGeneration = -1;
        private long _recoveryStreamGeneration = -1;
        private readonly VideoProgressMonitor _progressMonitor = new();
        private readonly ClientTransportRecoveryPolicy _clientTransportRecovery = new();
        private long _senderRecoveryRequestId;
        private readonly HostPipelineRecoveryPolicy _hostPipelineRecoveryPolicy = new();
        private VideoDiagnosticProbe? _latestVideoProbe;
        private long _lastDiagnosticStallAt;
        private bool _disposed;

        public event EventHandler? RuntimeChanged;

        private void RaiseRuntimeChangedSafely()
        {
            EventHandler? handlers = RuntimeChanged;
            if (handlers is null) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try { handler(this, EventArgs.Empty); } catch { }
            }
        }

        // Any explicit owner/state mutation invalidates automatic work that
        // started from an older snapshot. Call only while holding _sync.
        private void AdvanceStreamGenerationLocked()
        {
            unchecked { _streamGeneration++; }
        }

        private void RetireResources(IVideoTransport? transport = null,
            NativeNvdecPlayer? decoder = null, RemoteSessionWindow? window = null,
            bool closeWindow = false)
        {
            if (transport is not null && decoder is not null)
                transport.FrameReceived -= decoder.PushFrame;
            lock (_sync)
            {
                if (transport is not null && !_retiredTransports.Contains(transport))
                    _retiredTransports.Add(transport);
                if (decoder is not null && !_retiredDecoders.Contains(decoder))
                    _retiredDecoders.Add(decoder);
                if (window is not null)
                {
                    RetiredWindow? existing = _retiredWindows.Find(
                        w => ReferenceEquals(w.Window, window));
                    if (existing is null)
                        _retiredWindows.Add(new RetiredWindow { Window = window, Close = closeWindow });
                    else
                        existing.Close |= closeWindow;
                }
            }
        }

        private bool HasRetiredResources
        {
            get { lock (_sync) return _retiredTransports.Count > 0 ||
                _retiredDecoders.Count > 0 || _retiredWindows.Count > 0; }
        }

        // Called only under _recoveryGate. Each resource is removed from the
        // retirement set only after its required shutdown has completed.
        // A timeout retains it for a later explicit cleanup attempt.
        private async Task DrainRetiredResourcesAsync()
        {
            while (true)
            {
                IVideoTransport? transport;
                lock (_sync) transport = _retiredTransports.Count > 0 ? _retiredTransports[0] : null;
                if (transport is null) break;
                transport.Stop();
                transport.Dispose();
                lock (_sync) _retiredTransports.Remove(transport);
            }
            // UI detach must complete before the corresponding native D3D11
            // decoder is released. Do not close the window on a failed detach.
            List<RetiredWindow> windows;
            lock (_sync) windows = new List<RetiredWindow>(_retiredWindows);
            foreach (RetiredWindow entry in windows)
            {
                if (!entry.Detached)
                {
                    await entry.Window.DetachRendererAsync().ConfigureAwait(false);
                    entry.Detached = true;
                }
            }
            while (true)
            {
                NativeNvdecPlayer? decoder;
                lock (_sync) decoder = _retiredDecoders.Count > 0 ? _retiredDecoders[0] : null;
                if (decoder is null) break;
                decoder.Stop();
                decoder.Dispose();
                lock (_sync) _retiredDecoders.Remove(decoder);
            }
            foreach (RetiredWindow entry in windows)
            {
                if (entry.Close)
                    await entry.Window.CloseAsync().ConfigureAwait(false);
                lock (_sync) _retiredWindows.Remove(entry);
            }
        }

        private async Task DisposeStaleTransportAsync(IVideoTransport? transport, NativeNvdecPlayer? decoder)
        {
            if (transport is null) return;
            RetireResources(transport: transport, decoder: null);
            if (decoder is not null)
                transport.FrameReceived -= decoder.PushFrame;
            // The caller owns _recoveryGate. No new receiver may be created
            // until the retired transport has been confirmed stopped.
            await DrainRetiredResourcesAsync().ConfigureAwait(false);
        }

        private RemoteStreamingRuntime()
        {
            _session.SessionChanged += Session_SessionChanged;
            _session.StreamConfigChanged += Session_StreamConfigChanged;
            _session.StreamStopped += Session_StreamStopped;

            // CORE 11.18 INPUT is intentionally independent from the video
            // runtime. Touching the singleton starts XInput/virtual-pad
            // lifecycle without changing capture, NVENC or presenter state.
            _ = RemoteGamepadService.Instance;

            // CORE 11.20 AUDIO: starts only the independent audio session
            // observer. It does not adopt or mutate capture/NVENC state.
            _ = RemoteAudioService.Instance;

            // The permanent IDR handler must exist even when DevicesPage is closed.
            _ = HostTransmissionService.Instance;
            _ = Task.Run(() => VideoDiagnosticLoopAsync(_lifetime.Token));
            _ = Task.Run(() => WatchdogLoopAsync(_lifetime.Token));

            // CORE 11.71: network feedback is session-owned, not page-owned.
            // The host congestion controller must keep receiving loss/RX/FPS
            // feedback even when DevicesPage is unloaded or the app is in tray.
            _ = Task.Run(() => ClientTransportTelemetryLoopAsync(_lifetime.Token));
        }

        public RemoteStreamingState GetState()
        {
            lock (_sync)
            {
                return new RemoteStreamingState
                {
                    Capture = _capture,
                    Transport = _transport,
                    Decoder = _decoder,
                    EncoderRunning = _encoderRunning,
                    HostMonitor = _hostCursorMonitor
                };
            }
        }

        public void AdoptHost(
            NativeCaptureSession capture,
            IVideoTransport? transport,
            bool encoderRunning,
            MonitorInfo? monitor = null)
        {
            lock (_sync)
            {
                AdvanceStreamGenerationLocked();
                _capture = capture;
                if (transport is not null)
                    _transport = transport;
                _encoderRunning = encoderRunning;
                if (monitor is not null)
                    _hostCursorMonitor = monitor;
            }

            if (transport?.Running == true)
                StartHostCursorTelemetry();

            RaiseRuntimeChangedSafely();
        }

        public void AdoptClient(
            IVideoTransport transport,
            NativeNvdecPlayer decoder,
            uint width,
            uint height)
        {
            lock (_sync)
            {
                AdvanceStreamGenerationLocked();
                _transport = transport;
                _decoder = decoder;
                _decoderWidth = width;
                _decoderHeight = height;
            }
            RaiseRuntimeChangedSafely();
        }

        public void UpdateTransport(IVideoTransport? transport)
        {
            lock (_sync)
            {
                AdvanceStreamGenerationLocked();
                _transport = transport;
            }

            if (transport is null)
                StopHostCursorTelemetry();
            else if (transport.Running)
                StartHostCursorTelemetry();

            RaiseRuntimeChangedSafely();
        }

        public void UpdateCapture(NativeCaptureSession? capture, bool encoderRunning)
        {
            lock (_sync)
            {
                AdvanceStreamGenerationLocked();
                _capture = capture;
                _encoderRunning = encoderRunning;
                if (capture is null)
                    _hostCursorMonitor = null;
            }

            if (capture is null || !encoderRunning)
                StopHostCursorTelemetry();

            RaiseRuntimeChangedSafely();
        }

        // Returns with _recoveryGate held when successful. A cleanup that is
        // scheduled while startup is waiting invalidates the attempt; startup
        // releases the gate, waits for that cleanup, and tries again.
        private async Task<bool> WaitForStableClientVideoOwnershipAsync()
        {
            while (true)
            {
                Task cleanup;
                lock (_sync) cleanup = _videoCleanupTask;
                try { await cleanup.ConfigureAwait(false); }
                catch (Exception ex)
                {
                    StartupDiagnostics.RecordException("VIDEO_PREVIOUS_CLEANUP_INCOMPLETE", ex);
                    return false;
                }
                if (_disposed) return false;

                await _recoveryGate.WaitAsync().ConfigureAwait(false);
                bool stable;
                lock (_sync)
                {
                    stable = !_disposed &&
                        ReferenceEquals(cleanup, _videoCleanupTask) &&
                        _retiredTransports.Count == 0 &&
                        _retiredDecoders.Count == 0 &&
                        _retiredWindows.Count == 0;
                }
                if (stable) return true;
                _recoveryGate.Release();
            }
        }

        public async Task<bool> EnsureClientStreamingAsync()
        {
            if (!await WaitForStableClientVideoOwnershipAsync().ConfigureAwait(false))
                return false;

            RemoteSessionSnapshot session = _session.GetSnapshot();
            RemoteStreamConfig? config = session.StreamConfig;
            if (!session.Connected ||
                session.Role != RemoteSessionRole.Client ||
                config is null || config.Width == 0 || config.Height == 0 ||
                config.Generation <= 0)
            {
                _recoveryGate.Release();
                return false;
            }

            NativeNvdecPlayer? decoder = null;
            IVideoTransport? transport = null;
            long generation;
            bool createdDecoder = false;
            bool createdTransport = false;
            bool committed = false;
            string startupStage = "Preparação do vídeo";
            try
            {

                bool dimensionsChanged;
                RemoteSessionWindow? oldWindow = null;
                lock (_sync)
                {
                    generation = _streamGeneration;
                    decoder = _decoder;
                    transport = _transport;
                    dimensionsChanged =
                        (decoder is not null &&
                         (!decoder.Running || _decoderWidth != config.Width ||
                          _decoderHeight != config.Height ||
                          _decoderRemoteGeneration != config.Generation ||
                          _decoderRemoteSessionGeneration != session.SessionGeneration)) ||
                        (decoder is null && transport is not null) ||
                        (transport is not null &&
                         (!transport.Running || transport is not RemoteClientVideoTransport));

                    if (dimensionsChanged)
                    {
                        // Claim old resources before any slow native/UI release.
                        // StopVideoOnly and recovery can no longer reuse an object
                        // that this operation is about to dispose.
                        AdvanceStreamGenerationLocked();
                        generation = _streamGeneration;
                        _decoder = null;
                        _transport = null;
                        _decoderWidth = 0;
                        _decoderHeight = 0;
                        _decoderRemoteGeneration = 0;
                        _decoderRemoteSessionGeneration = 0;
                        oldWindow = _sessionWindow;
                    }
                }

                if (dimensionsChanged)
                {
                    startupStage = "Encerramento do decoder/receptor anterior";
                    RetireResources(transport, decoder, oldWindow, closeWindow: false);
                    await DrainRetiredResourcesAsync().ConfigureAwait(false);
                    decoder = null;
                    transport = null;
                }

                if (decoder is null || !decoder.Running)
                {
                    startupStage = "Abertura do NVDEC";
                    decoder = new NativeNvdecPlayer(
                        config.Width,
                        config.Height,
                        () => _ = _session.RequestRemoteIdrAsync());
                    createdDecoder = true;
                    startupStage = "Inicialização do NVDEC";
                    decoder.Start();
                }

                if (transport is null || !transport.Running ||
                    transport is not RemoteClientVideoTransport)
                {
                    if (transport is not null)
                    {
                        RetireResources(transport: transport);
                        await DrainRetiredResourcesAsync().ConfigureAwait(false);
                        transport = null;
                    }
                    startupStage = "Abertura do receptor UDP";
                    var client = new RemoteClientVideoTransport(
                        _session.LocalVideoPort,
                        _session);
                    client.FrameReceived += decoder.PushFrame;
                    transport = client;
                    createdTransport = true;
                    startupStage = "Bind/inicialização do receptor UDP";
                    client.Start();
                }

                startupStage = "Validação da geração da sessão";
                bool sessionStillCurrent = _session.IsCurrentVideoStream(
                    config.Generation, session.SessionGeneration, RemoteSessionRole.Client);
                bool stale;
                lock (_sync)
                {
                    stale = _disposed || generation != _streamGeneration ||
                        !sessionStillCurrent;
                    if (!stale)
                    {
                        _decoderRemoteGeneration = config.Generation;
                        _decoderRemoteSessionGeneration = session.SessionGeneration;
                        _decoder = decoder;
                        _transport = transport;
                        _decoderWidth = config.Width;
                        _decoderHeight = config.Height;
                        committed = true;
                    }
                }

                if (stale)
                {
                    RetireResources(createdTransport ? transport : null,
                        createdDecoder ? decoder : null);
                    await DrainRetiredResourcesAsync().ConfigureAwait(false);
                    return false;
                }

                // CORE 11.72 bootstrap handshake: receiver + decoder are now
                // committed and listening. Tell the host it is safe to start the
                // sender; the host will force exactly one clean IDR afterwards.
                if (!_session.IsCurrentVideoStream(config.Generation,
                    session.SessionGeneration, RemoteSessionRole.Client))
                    return false;
                startupStage = "Publicação de VIDEO READY";
                await _session.PublishVideoReadyAsync(config.Generation, session.SessionGeneration);
                // UI subscribers must not turn a successful bootstrap into a
                // false video failure after READY has already been published.
                RaiseRuntimeChangedSafely();
                return true;
            }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("CLIENT_STARTUP_ERROR", ex,
                    "stage=" + startupStage + " session=" + session.SessionGeneration +
                    " stream=" + config.Generation);
                VideoIncidentRecorder.Instance.RecordEvent("CLIENT_STARTUP_ERROR",
                    new { stage = startupStage, ex.HResult });
                VideoIncidentRecorder.Instance.MarkIncident("CLIENT_STARTUP_ERROR", 0);
                // An allocation/startup exception must not strand a decoder or
                // receiver that never reached the shared runtime state.
                if (!committed)
                {
                    try
                    {
                        RetireResources(createdTransport ? transport : null,
                            createdDecoder ? decoder : null);
                        await DrainRetiredResourcesAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanup)
                    {
                        StartupDiagnostics.RecordException("CLIENT_STARTUP_CLEANUP_INCOMPLETE", cleanup);
                    }
                }
                string reason = $"{startupStage}: {ex.GetType().Name} " +
                    $"(0x{ex.HResult:X8}): {ex.Message}";
                try
                {
                    await _session.ReportClientVideoStartupFailureAsync(
                        config.Generation, session.SessionGeneration, reason)
                        .ConfigureAwait(false);
                }
                catch { } // Reporting is best-effort; preserve the real failure.
                return false;
            }
            finally
            {
                _recoveryGate.Release();
            }
        }

        public void ShowSessionWindow() => _ = ShowSessionWindowSafelyAsync();

        public async Task PlayWithKeyboardMouseAsync()
        {
            var session=_session.GetSnapshot();
            if(!session.Connected || session.Role!=RemoteSessionRole.Client)
                throw new InvalidOperationException("Conecte-se ao computador em que você vai jogar.");
            var operation=_uiOperation ?? throw new InvalidOperationException("A interface ainda está iniciando.");
            await _recoveryGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await operation.RunAsync(()=>
                {
                    ShowSessionWindowCore();
                    if(_sessionWindow is null)throw new InvalidOperationException("Aguarde o vídeo da transmissão aparecer.");
                    _sessionWindow.PlayWithKeyboardMouse();
                }).ConfigureAwait(false);
            }
            finally { _recoveryGate.Release(); }
        }

        private async Task ShowSessionWindowSafelyAsync()
        {
            try { await ShowSessionWindowAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("CLIENT_WINDOW_ERROR", ex);
                var session = _session.GetSnapshot();
                if (session.StreamConfig is { } config)
                    await _session.ReportClientVideoStartupFailureAsync(config.Generation, session.SessionGeneration,
                        "Janela do cliente: " + ex.Message).ConfigureAwait(false);
            }
        }

        public async Task ShowSessionWindowAsync()
        {
            var operation = _uiOperation ?? throw new InvalidOperationException("A janela principal ainda não registrou a interface de vídeo.");
            await _recoveryGate.WaitAsync().ConfigureAwait(false);
            try { await operation.RunAsync(ShowSessionWindowCore).ConfigureAwait(false); }
            finally { _recoveryGate.Release(); }
        }

        private void ShowSessionWindowCore()
        {
            if (_disposed) return;

            NativeNvdecPlayer? decoder;
            lock (_sync)
                decoder = _decoder;

            if (decoder is null || !decoder.Running)
                return;

            bool created = false;
            if (_sessionWindow is null)
            {
                _sessionWindow = new RemoteSessionWindow(decoder);
                _sessionWindow.Closed += SessionWindow_Closed;
                created = true;
            }
            else
            {
                _sessionWindow.SetDecoder(decoder);
            }

            // CORE 11.20: open as a normal resizable window. Fullscreen is an
            // explicit client choice (F11), and never changes host resolution.
            if (created || !_sessionWindow.IsOpen)
                _sessionWindow.ShowWindowed();
        }

        private void SessionWindow_Closed(object? sender, EventArgs e)
        {
            if (sender is not RemoteSessionWindow window)
                return;

            if (ReferenceEquals(_sessionWindow, window))
                _sessionWindow = null;

            window.Closed -= SessionWindow_Closed;
            window.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeCursorInfo
        {
            public int Size;
            public int Flags;
            public IntPtr Cursor;
            public NativePoint ScreenPosition;
        }

        private const int CursorShowing = 0x00000001;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorInfo(
            ref NativeCursorInfo cursorInfo);

        private void StartHostCursorTelemetry()
        {
            RemoteSessionSnapshot snapshot = _session.GetSnapshot();
            if (!snapshot.Connected || snapshot.Role != RemoteSessionRole.Host)
                return;

            CancellationToken token;

            lock (_sync)
            {
                if (_disposed ||
                    _hostCursorMonitor is null ||
                    _transport?.Running != true ||
                    (_cursorTelemetryTask is not null &&
                     !_cursorTelemetryTask.IsCompleted))
                {
                    return;
                }

                _cursorTelemetryCancel?.Dispose();
                _cursorTelemetryCancel =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        _lifetime.Token);

                _lastCursorSentAt = 0;
                _lastCursorVisible = false;
                _lastCursorX = -1;
                _lastCursorY = -1;
                token = _cursorTelemetryCancel.Token;
                _cursorTelemetryTask =
                    Task.Run(() => CursorTelemetryLoopAsync(token));
            }
        }

        private void StopHostCursorTelemetry()
        {
            CancellationTokenSource? cancel;
            Task? task;

            lock (_sync)
            {
                cancel = _cursorTelemetryCancel;
                task = _cursorTelemetryTask;
                _cursorTelemetryCancel = null;
                _cursorTelemetryTask = null;
                _lastCursorSentAt = 0;
                _lastCursorVisible = false;
                _lastCursorX = -1;
                _lastCursorY = -1;
            }

            if (cancel is not null)
            {
                try { cancel.Cancel(); } catch { }
                if (task is not null)
                {
                    CancellationTokenSource cancelToDispose = cancel;
                    _ = task.ContinueWith(
                        _ => cancelToDispose.Dispose(),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                else
                {
                    cancel.Dispose();
                }
            }

            RemoteSessionSnapshot snapshot = _session.GetSnapshot();
            if (snapshot.Connected && snapshot.Role == RemoteSessionRole.Host)
                _ = _session.PublishCursorAsync(false, 0, 0);
        }

        private async Task ClientTransportTelemetryLoopAsync(CancellationToken token)
        {
            IVideoTransport? previousTransport = null;
            long previousFrames = 0;
            NativeNvdecPlayer? lastPresentedDecoder = null;
            long lastPresentedCount = 0, lastPresentedAt = -1;
            long previousTick = Environment.TickCount64;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    RemoteSessionSnapshot session = _session.GetSnapshot();
                    IVideoTransport? transport;
                    NativeNvdecPlayer? telemetryDecoder;
                    int telemetrySession; long telemetryStream;
                    lock (_sync)
                    {
                        transport = _transport;
                        telemetryDecoder = _decoder;
                        telemetrySession = _decoderRemoteSessionGeneration; telemetryStream = _decoderRemoteGeneration;
                    }

                    if (session.Connected &&
                        session.Role == RemoteSessionRole.Client &&
                        VideoGenerationIdentity.MatchesTelemetry(session.SessionGeneration, session.StreamConfig?.Generation ?? 0,
                            telemetrySession, telemetryStream) && transport?.Running == true)
                    {
                        TransportSnapshot snapshot = transport.GetSnapshot();
                        long now = Environment.TickCount64;

                        if (ReferenceEquals(previousTransport, transport) &&
                            now > previousTick)
                        {
                            long frameDelta = Math.Max(
                                0,
                                snapshot.ReceivedFrames - previousFrames);

                            double frameRate =
                                frameDelta * 1000.0 /
                                Math.Max(1L, now - previousTick);

                            NativeNvdecSnapshot? decoderStats = telemetryDecoder?.GetSnapshot();
                            long presentedCount = decoderStats is null ? 0 : SafeCount(decoderStats.D3D11PresentedFrames);
                            if (!ReferenceEquals(lastPresentedDecoder, telemetryDecoder))
                            { lastPresentedDecoder = telemetryDecoder; lastPresentedCount = 0; lastPresentedAt = -1; }
                            if (presentedCount > lastPresentedCount) lastPresentedAt = now;
                            lastPresentedCount = presentedCount;
                            await _session.PublishTransportTelemetryAsync(
                                session.SessionGeneration, session.StreamConfig!.Generation,
                                snapshot.ReceivedPackets,
                                snapshot.ReceivedBytes,
                                snapshot.SequenceGaps,
                                snapshot.IncompleteFramesDropped,
                                snapshot.PendingFrames,
                                snapshot.DecodeDispatchDrops,
                                decoderStats?.StaleFramesDropped ?? 0,
                                decoderStats?.WaitingForKeyFrameDrops ?? 0,
                                decoderStats is null ? 0 :
                                    SafeCount(decoderStats.D3D11DroppedPresents) +
                                    SafeCount(decoderStats.D3D11ReplacedPendingFrames),
                                frameRate,
                                decoderStats is null ? 0 :
                                    SafeCount(decoderStats.D3D11PresentedFrames),
                                decoderStats?.NetworkFramesReceived ?? 0,
                                decoderStats is null ? 0 : SafeCount(decoderStats.DecodedPictures),
                                lastPresentedAt < 0 ? -1 : now - lastPresentedAt).ConfigureAwait(false);
                        }

                        previousTransport = transport;
                        previousFrames = snapshot.ReceivedFrames;
                        previousTick = now;
                    }
                    else
                    {
                        previousTransport = null;
                        previousFrames = 0;
                        previousTick = Environment.TickCount64;
                    }

                    await Task.Delay(200, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Telemetry is advisory. It must never become a reason to
                    // tear down the otherwise healthy video/control session.
                    try
                    {
                        await Task.Delay(250, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task CursorTelemetryLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    RemoteSessionSnapshot session = _session.GetSnapshot();
                    IVideoTransport? transport;
                    MonitorInfo? monitor;

                    lock (_sync)
                    {
                        transport = _transport;
                        monitor = _hostCursorMonitor;
                    }

                    if (session.Connected &&
                        session.Role == RemoteSessionRole.Host &&
                        transport?.Running == true &&
                        monitor is not null)
                    {
                        PublishCurrentCursorIfNeeded(monitor);
                    }

                    await Task.Delay(16, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Cursor telemetry is best-effort. It must never tear down
                    // video/control transport or become a streaming failure.
                    try
                    {
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private void PublishCurrentCursorIfNeeded(MonitorInfo monitor)
        {
            NativeCursorInfo info =
                new()
                {
                    Size = Marshal.SizeOf<NativeCursorInfo>()
                };

            bool apiOk = GetCursorInfo(ref info);
            long cursorX = info.ScreenPosition.X;
            long cursorY = info.ScreenPosition.Y;
            long monitorRight = (long)monitor.Left + monitor.Width;
            long monitorBottom = (long)monitor.Top + monitor.Height;

            bool inside =
                apiOk &&
                cursorX >= monitor.Left &&
                cursorY >= monitor.Top &&
                cursorX < monitorRight &&
                cursorY < monitorBottom;

            bool visible =
                apiOk &&
                (info.Flags & CursorShowing) != 0 &&
                inside;

            double x =
                visible
                    ? Math.Clamp(
                        (cursorX - monitor.Left) /
                        Math.Max(1.0, monitor.Width - 1.0),
                        0.0,
                        1.0)
                    : 0.0;

            double y =
                visible
                    ? Math.Clamp(
                        (cursorY - monitor.Top) /
                        Math.Max(1.0, monitor.Height - 1.0),
                        0.0,
                        1.0)
                    : 0.0;

            long now = Environment.TickCount64;
            bool changed =
                visible != _lastCursorVisible ||
                Math.Abs(x - _lastCursorX) >= 0.0005 ||
                Math.Abs(y - _lastCursorY) >= 0.0005;

            if (!changed &&
                _lastCursorSentAt != 0 &&
                now - _lastCursorSentAt < 180)
            {
                return;
            }

            _lastCursorSentAt = now;
            _lastCursorVisible = visible;
            _lastCursorX = x;
            _lastCursorY = y;

            _ = _session.PublishCursorAsync(visible, x, y);
        }

        private static long SafeCount(ulong value) =>
            value > long.MaxValue ? long.MaxValue : (long)value;

        private async Task VideoDiagnosticLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(250, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try
                {
                    RemoteSessionSnapshot session = _session.GetSnapshot();
                    IVideoTransport? transport;
                    NativeCaptureSession? capture;
                    NativeNvdecPlayer? decoder;
                    long generation;
                    lock (_sync)
                    {
                        transport = _transport;
                        capture = _capture;
                        decoder = _decoder;
                        generation = _streamGeneration;
                    }
                    long now = Environment.TickCount64;
                    TransportSnapshot? t = transport?.GetSnapshot();
                    CapturePipelineStats? c = capture?.GetCapturePipelineStatsDiagnostic();
                    EncoderStats? e = capture?.GetEncoderStatsDiagnostic();
                    NativeNvdecSnapshot? d = decoder?.GetSnapshot();
                    if (!session.Connected || session.StreamConfig is null)
                        _progressMonitor.Reset();
                    var sample = new VideoProgressSample
                    {
                        NowMs = now,
                        SessionGeneration = session.SessionGeneration,
                        StreamGeneration = session.StreamConfig?.Generation ?? 0,
                        TransportId = generation,
                        Connected = session.Connected,
                        Host = session.Role == RemoteSessionRole.Host,
                        Running = t?.Running == true,
                        DecoderRunning = d?.Running == true,
                        PresenterAttached = d?.D3D11Attached == true,
                        ExpectedFps = session.StreamConfig?.Fps ?? 60,
                        Packets = session.Role == RemoteSessionRole.Host ? t?.SentPackets ?? 0 : t?.ReceivedPackets ?? 0,
                        Frames = session.Role == RemoteSessionRole.Host ? t?.SentFrames ?? 0 : t?.ReceivedFrames ?? 0,
                        Decoded = d is null ? 0 : SafeCount(d.DecodedPictures),
                        Presented = d is null ? 0 : SafeCount(d.D3D11PresentedFrames),
                        Captured = c is null ? 0 : SafeCount(c.BackendFramesPublished),
                        Encoded = e is null ? 0 : SafeCount(e.EncodedFrames),
                        Gaps = t?.SequenceGaps ?? 0,
                        Incomplete = t?.IncompleteFramesDropped ?? 0,
                        WaitingForKeyFrame = d?.WaitingForKeyFrameDrops ?? 0,
                        WaitingForReference = d?.AwaitingKeyFrame == true ||
                            t?.AwaitingKeyFrame == true,
                        QueueResets = e is null ? 0 : SafeCount(e.QueueResets)
                    };
                    VideoProgressDecision progress = _progressMonitor.Observe(sample);
                    var probe = new VideoDiagnosticProbe
                    {
                        CollectedAt = now, RuntimeGeneration = generation,
                        Session = session, Transport = transport, Decoder = decoder,
                        TransportStats = t, CaptureStats = c, EncoderStats = e,
                        DecoderStats = d, Progress = progress
                    };
                    Volatile.Write(ref _latestVideoProbe, probe);
                    VideoIncidentRecorder.Instance.Sample(session, t, c, e, d, progress,
                        _session.GetControlDiagnostics(), generation);
                    if (!string.IsNullOrEmpty(progress.Reason))
                        VideoIncidentRecorder.Instance.MarkIncident(progress.Reason,
                            progress.IncidentDurationMs);
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.RecordException("DIAGNOSTIC_SAMPLE_ERROR", ex);
                    VideoIncidentRecorder.Instance.RecordEvent("DIAGNOSTIC_SAMPLE_ERROR", new { ex.HResult });
                }
            }
        }

        private async Task WatchdogLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(250, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (_disposed) break;
                VideoDiagnosticProbe? probe = Volatile.Read(ref _latestVideoProbe);
                long now = Environment.TickCount64;
                // Destructive host recovery is disabled in this candidate.
                // There is no abandoned native task or false success to poll.
                if (probe is null || now - probe.CollectedAt > 2000)
                {
                    if (now - _lastDiagnosticStallAt >= 2000)
                    {
                        _lastDiagnosticStallAt = now;
                        VideoIncidentRecorder.Instance.MarkIncident("DIAGNOSTIC_SAMPLER_STALL",
                            probe is null ? 0 : now - probe.CollectedAt);
                    }
                    // Never perform a destructive recovery using stale telemetry.
                    continue;
                }
                RemoteSessionSnapshot session = _session.GetSnapshot();
                if (_recoverySessionGeneration != session.SessionGeneration ||
                    _recoveryStreamGeneration != (session.StreamConfig?.Generation ?? 0))
                {
                    _recoverySessionGeneration = session.SessionGeneration;
                    _recoveryStreamGeneration = session.StreamConfig?.Generation ?? 0;
                    _recoveryAttempts = 0;
                    _lastRecoveryAttempt = 0;
                    _clientTransportRecovery.Reset();
                }
                if (!session.Connected || session.StreamConfig is null)
                {
                    _clientTransportRecovery.Reset();
                    _hostPipelineRecoveryPolicy.Reset();
                    _recoveryAttempts = 0;
                    _lastRecoveryAttempt = 0;
                    continue;
                }
                IVideoTransport? transport;
                NativeNvdecPlayer? decoder;
                long generation;
                lock (_sync)
                {
                    transport = _transport;
                    decoder = _decoder;
                    generation = _streamGeneration;
                }
                if (generation != probe.RuntimeGeneration ||
                    !ReferenceEquals(transport, probe.Transport) ||
                    session.SessionGeneration != probe.Session.SessionGeneration ||
                    session.StreamConfig.Generation != probe.Session.StreamConfig?.Generation)
                    continue;

                VideoProgressDecision? progress = probe.Progress;
                bool client = session.Role == RemoteSessionRole.Client;
                bool host = session.Role == RemoteSessionRole.Host;
                if (client && progress?.RequestIdr == true)
                {
                    try
                    {
                        await _session.RequestRemoteIdrAsync(
                            session.StreamConfig.Generation, session.SessionGeneration).ConfigureAwait(false);
                        VideoIncidentRecorder.Instance.RecordEvent("CLIENT_IDR_REQUESTED");
                    }
                    catch (Exception ex)
                    {
                        StartupDiagnostics.RecordException("CLIENT_IDR_ERROR", ex,
                            "session=" + session.SessionGeneration + " stream=" + session.StreamConfig.Generation);
                        VideoIncidentRecorder.Instance.RecordEvent("CLIENT_IDR_ERROR", new { ex.HResult });
                    }
                }
                if (host && HostTransmissionService.IsValueCreated &&
                    probe.CaptureStats is CapturePipelineStats c &&
                    probe.EncoderStats is EncoderStats e)
                {
                    HostTransmissionService.Instance.ObserveEncoderHealth(session, c, e);
                }

                // Client recovery is driven by complete-frame and presentation
                // progress, not Running=true or the success of a socket reopen.
                if (client)
                {
                    await ObserveClientTransportRecoveryAsync(session, probe).ConfigureAwait(false);
                    continue;
                }
                _clientTransportRecovery.Reset();
                bool hostFailure = host && HostTransmissionService.IsValueCreated &&
                    (transport?.Running != true ||
                     (transport is RemoteHostVideoTransport h &&
                      _session.GetRemoteVideoEndpoint() is IPEndPoint endpoint &&
                      (!h.IsCurrentStream || !h.RemoteEndpoint.Equals(endpoint))));
                if (!hostFailure)
                {
                    _recoveryAttempts = 0;
                    continue;
                }
                long recoveryDelayMs = _recoveryAttempts < 3 ? 1500 :
                    Math.Min(8000, 1500 + (_recoveryAttempts - 2) * 1000);
                if (_lastRecoveryAttempt != 0 && now - _lastRecoveryAttempt < recoveryDelayMs)
                    continue;
                _lastRecoveryAttempt = now;
                _recoveryAttempts = Math.Min(_recoveryAttempts + 1, 8);
                bool recovered = false;
                try
                {
                    recovered = await HostTransmissionService.Instance.RecoverHostVideoAsync(
                        session.SessionGeneration, session.StreamConfig.Generation).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.RecordException("VIDEO_RECOVERY_ERROR", ex,
                        "session=" + session.SessionGeneration + " stream=" + session.StreamConfig.Generation +
                        " runtime=" + generation + " attempt=" + _recoveryAttempts);
                    VideoIncidentRecorder.Instance.RecordEvent("VIDEO_RECOVERY_ERROR", new { ex.HResult });
                }
                if (recovered)
                {
                    _recoveryAttempts = 0;
                    VideoIncidentRecorder.Instance.RecordEvent("HOST_SENDER_RECOVERY_STARTED");
                }
            }
        }

        private async Task ObserveClientTransportRecoveryAsync(RemoteSessionSnapshot session,
            VideoDiagnosticProbe probe)
        {
            if (session.StreamConfig is null) return;
            NativeNvdecSnapshot? d = probe.DecoderStats;
            TransportSnapshot? t = probe.TransportStats;
            NativeNvdecPlayer? activeDecoder = probe.Decoder;
            long now = Environment.TickCount64;
            int sessionGeneration = session.SessionGeneration;
            long streamGeneration = session.StreamConfig.Generation;

            // A dead decoder is a different failure from a silent UDP path.
            // Use the existing confirmed-shutdown bootstrap; never destroy a
            // running decoder merely because the sender has stopped delivering.
            if (activeDecoder?.Running != true)
            {
                _clientTransportRecovery.Reset();
                if (_lastRecoveryAttempt != 0 && now - _lastRecoveryAttempt < 5000) return;
                if (_recoveryAttempts >= 3) return;
                _lastRecoveryAttempt = now;
                _recoveryAttempts++;
                bool started = await EnsureClientStreamingAsync().ConfigureAwait(false);
                VideoIncidentRecorder.Instance.RecordEvent("CLIENT_DECODER_RESTART_RESULT",
                    new { session = sessionGeneration, stream = streamGeneration, started });
                return;
            }
            if (d is null || t is null || activeDecoder is null) return;

            var decision = _clientTransportRecovery.Observe(new ClientTransportRecoverySample
            {
                NowMs = now, SessionGeneration = sessionGeneration,
                StreamGeneration = streamGeneration, Connected = session.Connected,
                ReceiverRunning = t.Running, DecoderRunning = d.Running,
                PresenterAttached = d.D3D11Attached,
                WaitingForReference = d.AwaitingKeyFrame || t.AwaitingKeyFrame,
                Packets = t.ReceivedPackets, Frames = t.ReceivedFrames,
                Presented = SafeCount(d.D3D11PresentedFrames)
            });
            if (decision.FirstPresented)
            {
                _recoveryAttempts = 0;
                VideoIncidentRecorder.Instance.RecordEvent("CLIENT_FIRST_FRAME_PRESENTED",
                    new { session = sessionGeneration, stream = streamGeneration,
                        startupMs = decision.StartupMs });
            }
            if (decision.Action == ClientTransportRecoveryAction.None) return;
            if (decision.Action == ClientTransportRecoveryAction.Recovered)
            {
                _recoveryAttempts = 0;
                VideoIncidentRecorder.Instance.RecordEvent("CLIENT_VIDEO_PROGRESS_RECOVERED",
                    new { session = sessionGeneration, stream = streamGeneration,
                        noFrameMs = decision.NoFrameMs, noPresentMs = decision.NoPresentMs });
                return;
            }
            if (decision.Action == ClientTransportRecoveryAction.Exhausted)
            {
                VideoIncidentRecorder.Instance.MarkIncident("CLIENT_RECOVERY_BUDGET_EXHAUSTED",
                    decision.NoPresentMs);
                VideoIncidentRecorder.Instance.RecordEvent("CLIENT_RECOVERY_MANUAL_REQUIRED",
                    new { session = sessionGeneration, stream = streamGeneration,
                        attempts = decision.RemoteAttempts });
                return;
            }

            bool succeeded = false;
            long requestId = 0;
            try
            {
                if (!_session.IsCurrentVideoStream(streamGeneration, sessionGeneration,
                        RemoteSessionRole.Client)) return;
                if (decision.Action == ClientTransportRecoveryAction.ReopenReceiver)
                {
                    // Recheck the current snapshot immediately before touching
                    // the socket. A recovered session must not be interrupted.
                    RemoteStreamingState current = GetState();
                    if (current.Transport is not RemoteClientVideoTransport ||
                        !ReferenceEquals(current.Decoder, activeDecoder))
                        return;
                    NativeNvdecSnapshot latest = activeDecoder.GetSnapshot();
                    if (SafeCount(latest.D3D11PresentedFrames) >
                        SafeCount(d.D3D11PresentedFrames))
                    {
                        _clientTransportRecovery.Observe(new ClientTransportRecoverySample
                        {
                            NowMs = Environment.TickCount64,
                            SessionGeneration = sessionGeneration, StreamGeneration = streamGeneration,
                            Connected = true, ReceiverRunning = current.Transport.Running,
                            DecoderRunning = latest.Running, PresenterAttached = latest.D3D11Attached,
                            WaitingForReference = latest.AwaitingKeyFrame || t.AwaitingKeyFrame,
                            Packets = t.ReceivedPackets, Frames = t.ReceivedFrames,
                            Presented = SafeCount(latest.D3D11PresentedFrames)
                        });
                        return;
                    }
                    succeeded = await TryRecoverTransportAsync(session, activeDecoder,
                        SafeCount(d.D3D11PresentedFrames)).ConfigureAwait(false);
                }
                else
                {
                    // No local resource is destroyed by this request. The host
                    // validates it again after taking its own ownership gate.
                    requestId = Interlocked.Increment(ref _senderRecoveryRequestId);
                    VideoIncidentRecorder.Instance.RecordEvent("CLIENT_SENDER_ROTATION_REQUESTED",
                        new { session = sessionGeneration, stream = streamGeneration,
                            requestId, attempt = decision.RemoteAttempts + 1 });
                    succeeded = await _session.AwaitSenderRecoveryResultAsync(streamGeneration,
                        sessionGeneration, requestId, SafeCount(d.D3D11PresentedFrames),
                        TimeSpan.FromMilliseconds(HostPipelineRecoveryTransaction.SenderResponseTimeoutMs), _lifetime.Token).ConfigureAwait(false);
                    VideoIncidentRecorder.Instance.RecordEvent("CLIENT_SENDER_ROTATION_ACK",
                        new { session = sessionGeneration, stream = streamGeneration,
                            requestId, accepted = succeeded });
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested ||
                !_session.IsCurrentVideoStream(streamGeneration, sessionGeneration, RemoteSessionRole.Client))
            {
                // A replacement stream or intentional stop supersedes this
                // request. Do not report a transport failure or invent an ACK.
                VideoIncidentRecorder.Instance.RecordEvent("CLIENT_RECOVERY_SUPERSEDED",
                    new { session = sessionGeneration, stream = streamGeneration, requestId });
            }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("CLIENT_COORDINATED_RECOVERY_ERROR", ex);
                VideoIncidentRecorder.Instance.RecordEvent("CLIENT_COORDINATED_RECOVERY_ERROR",
                    new { session = sessionGeneration, stream = streamGeneration,
                        requestId, ex.HResult });
            }
            finally
            {
                // A frame can arrive while a socket operation awaits its gate.
                // Observe that progress before classifying a failed operation.
                try
                {
                    if (_session.IsCurrentVideoStream(streamGeneration, sessionGeneration,
                            RemoteSessionRole.Client) &&
                        ReferenceEquals(GetState().Decoder, activeDecoder))
                    {
                        NativeNvdecSnapshot latest = activeDecoder.GetSnapshot();
                        if (SafeCount(latest.D3D11PresentedFrames) >
                            SafeCount(d.D3D11PresentedFrames))
                        {
                            _clientTransportRecovery.Observe(new ClientTransportRecoverySample
                            {
                                NowMs = Environment.TickCount64,
                                SessionGeneration = sessionGeneration, StreamGeneration = streamGeneration,
                                Connected = true, ReceiverRunning = true,
                                DecoderRunning = latest.Running, PresenterAttached = latest.D3D11Attached,
                                WaitingForReference = latest.AwaitingKeyFrame,
                                Packets = t.ReceivedPackets, Frames = t.ReceivedFrames,
                                Presented = SafeCount(latest.D3D11PresentedFrames)
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.RecordException("CLIENT_RECOVERY_PROGRESS_CHECK_ERROR", ex);
                }
                _clientTransportRecovery.Complete(decision.Action, succeeded,
                    Environment.TickCount64, sessionGeneration, streamGeneration);
            }
        }

        private async Task<bool> TryRecoverTransportAsync(RemoteSessionSnapshot session,
            NativeNvdecPlayer expectedDecoder, long expectedPresented)
        {
            // Only the client owns UDP receiver recovery. The host service is
            // the sole authority for sender creation/replacement.
            if (session.Role != RemoteSessionRole.Client || session.StreamConfig is null ||
                HasRetiredResources)
                return false;
            if (!await _recoveryGate.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
                return false;
            if (HasRetiredResources)
            {
                _recoveryGate.Release();
                return false;
            }
            IVideoTransport? replacement = null;
            NativeNvdecPlayer? decoder = null;
            long generation = -1;
            try
            {
                if (!_session.IsCurrentVideoStream(session.StreamConfig.Generation,
                    session.SessionGeneration, RemoteSessionRole.Client)) return false;
                IVideoTransport? old;
                lock (_sync)
                {
                    generation = _streamGeneration;
                    old = _transport;
                    decoder = _decoder;
                    if (_disposed || old is not RemoteClientVideoTransport ||
                        decoder is null || !decoder.Running ||
                        !ReferenceEquals(decoder, expectedDecoder))
                        return false;
                    // The expensive shutdown can only proceed while the same
                    // decoder is still stalled. The receiver's own counters
                    // may have reset; presentation is the stable reference.
                    if (SafeCount(decoder.GetSnapshot().D3D11PresentedFrames) > expectedPresented)
                        return false;
                    // Claim the old receiver before native/socket teardown.
                    AdvanceStreamGenerationLocked();
                    generation = _streamGeneration;
                    _transport = null;
                }
                RetireResources(transport: old);
                if (decoder is not null) old.FrameReceived -= decoder.PushFrame;
                await DrainRetiredResourcesAsync().ConfigureAwait(false);
                var client = new RemoteClientVideoTransport(_session.LocalVideoPort, _session);
                replacement = client;
                if (decoder is null) throw new InvalidOperationException("Decoder owner missing");
                client.FrameReceived += decoder.PushFrame;
                client.Start();
                bool current = _session.IsCurrentVideoStream(session.StreamConfig.Generation,
                    session.SessionGeneration, RemoteSessionRole.Client);
                bool stale;
                lock (_sync)
                {
                    stale = _disposed || generation != _streamGeneration || !current ||
                        !ReferenceEquals(_decoder, decoder);
                    if (!stale) _transport = replacement;
                }
                if (stale)
                {
                    await DisposeStaleTransportAsync(replacement, decoder).ConfigureAwait(false);
                    replacement = null;
                    return false;
                }
                replacement = null; // Ownership has moved to the runtime.
                try { await _session.RequestRemoteIdrAsync(
                    session.StreamConfig.Generation, session.SessionGeneration).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    StartupDiagnostics.RecordException("CLIENT_RECOVERY_IDR_ERROR", ex,
                        "session=" + session.SessionGeneration + " stream=" + session.StreamConfig.Generation);
                    VideoIncidentRecorder.Instance.RecordEvent("CLIENT_RECOVERY_IDR_ERROR", new { ex.HResult });
                }
                try { RaiseRuntimeChangedSafely(); } catch { }
                VideoIncidentRecorder.Instance.RecordEvent("CLIENT_RECEIVER_REBOUND",
                    new { session = session.SessionGeneration, stream = session.StreamConfig.Generation,
                        runtime = generation });
                return true;
            }
            catch (Exception ex)
            {
                try { await DisposeStaleTransportAsync(replacement, decoder).ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    StartupDiagnostics.RecordException("CLIENT_RECEIVER_CLEANUP_INCOMPLETE", cleanup);
                }
                StartupDiagnostics.RecordException("CLIENT_RECEIVER_RECOVERY_ERROR", ex,
                    "session=" + session.SessionGeneration + " stream=" + session.StreamConfig.Generation +
                    " runtime=" + generation);
                VideoIncidentRecorder.Instance.RecordEvent("CLIENT_RECEIVER_RECOVERY_ERROR", new { ex.HResult });
                return false;
            }
            finally { _recoveryGate.Release(); }
        }

        public void StopVideoOnly(bool disposeDecoder = true)
        {
            BeginVideoTeardown(disposeDecoder);
        }

        private Task BeginVideoTeardown(bool disposeDecoder)
        {
            StopHostCursorTelemetry();
            Task cleanup;
            lock (_sync)
            {
                AdvanceStreamGenerationLocked();
                IVideoTransport? transport = _transport;
                _transport = null;
                if (HostTransmissionService.IsValueCreated &&
                    HostTransmissionService.Instance.IsUsingTransport(transport))
                    transport = null; // Host service retains its own sender.
                NativeNvdecPlayer? decoder = null;
                RemoteSessionWindow? window = null;
                if (disposeDecoder)
                {
                    decoder = _decoder;
                    _decoder = null;
                    _decoderWidth = 0;
                    _decoderHeight = 0;
                    _decoderRemoteGeneration = 0;
                    _decoderRemoteSessionGeneration = 0;
                    window = _sessionWindow;
                    _sessionWindow = null;
                }
                RetireResources(transport, decoder, window, closeWindow: disposeDecoder);
                Task previous = _videoCleanupTask;
                cleanup = Task.Run(() => CleanupVideoResourcesAsync(previous));
                _videoCleanupTask = cleanup;
            }
            RaiseRuntimeChangedSafely();
            return cleanup;
        }

        private async Task CleanupVideoResourcesAsync(Task previousCleanup)
        {
            // A previous failure does not erase its retained resources. An
            // explicit Stop can retry them, but normal startup remains blocked.
            try { await previousCleanup.ConfigureAwait(false); }
            catch (Exception ex) { StartupDiagnostics.RecordException("VIDEO_PREVIOUS_CLEANUP", ex); }
            await _recoveryGate.WaitAsync().ConfigureAwait(false);
            try { await DrainRetiredResourcesAsync().ConfigureAwait(false); }
            finally { _recoveryGate.Release(); }
        }

        private void Session_StreamConfigChanged(object? sender, EventArgs e)
        {
            VideoIncidentRecorder.Instance.RecordEvent("STREAM_CONFIG_CHANGED");
            _ = HandleClientStreamStartedAsync();
        }

        private async Task HandleClientStreamStartedAsync()
        {
            try
            {
                RemoteSessionSnapshot snapshot = _session.GetSnapshot();
                if (!snapshot.Connected ||
                    snapshot.Role != RemoteSessionRole.Client ||
                    snapshot.StreamConfig is null)
                {
                    return;
                }

                if (!await EnsureClientStreamingAsync().ConfigureAwait(false))
                    return;

                if (MultiverseSettingsService.Instance.GetAppPreferences().AutoOpenClientWindow)
                    await ShowSessionWindowAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RemoteSessionSnapshot current = _session.GetSnapshot();
                RemoteStreamConfig? config = current.StreamConfig;
                if (current.Connected && current.Role == RemoteSessionRole.Client &&
                    config is not null)
                {
                    try
                    {
                        await _session.ReportClientVideoStartupFailureAsync(
                            config.Generation, current.SessionGeneration,
                            $"Inicialização/janela do cliente: {ex.GetType().Name} " +
                            $"(0x{ex.HResult:X8}): {ex.Message}").ConfigureAwait(false);
                    }
                    catch { }
                }
            }
        }

        private void Session_StreamStopped(object? sender, EventArgs e)
        {
            RemoteSessionSnapshot snapshot = _session.GetSnapshot();
            if (snapshot.Connected && snapshot.Role == RemoteSessionRole.Client)
            {
                VideoIncidentRecorder.Instance.RecordEvent("STREAM_STOPPED");
                BeginVideoTeardown(disposeDecoder: true);
            }
        }

        private void Session_SessionChanged(object? sender, EventArgs e)
        {
            if (_session.GetSnapshot().Connected)
                return;

            VideoIncidentRecorder.Instance.RecordEvent("SESSION_DISCONNECTED");
            VideoIncidentRecorder.Instance.MarkIncident("SESSION_DISCONNECTED", 0);
            int disconnectedGeneration = _session.GetSnapshot().SessionGeneration;
            Task videoCleanup = BeginVideoTeardown(disposeDecoder: true);

            NativeCaptureSession? capture;
            lock (_sync)
            {
                capture = _capture;
                _encoderRunning = false;
                _hostCursorMonitor = null;
            }

            _ = HandleSessionDisconnectedAsync(videoCleanup, capture, disconnectedGeneration);
        }

        private async Task HandleSessionDisconnectedAsync(
            Task videoCleanup, NativeCaptureSession? capture, int disconnectedGeneration)
        {
            try { await videoCleanup.ConfigureAwait(false); }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("VIDEO_DISCONNECT_CLEANUP_INCOMPLETE", ex);
                return;
            }
            RemoteSessionSnapshot current = _session.GetSnapshot();
            if (current.Connected || current.SessionGeneration != disconnectedGeneration) return;
            try
            {
                if (HostTransmissionService.IsValueCreated &&
                    HostTransmissionService.Instance.IsUsingCapture(capture))
                {
                    // The service rechecks the session after acquiring its gate.
                    await HostTransmissionService.Instance.StopForDisconnectedSessionAsync(
                        disconnectedGeneration).ConfigureAwait(false);
                }

            }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("VIDEO_DISCONNECT_ENCODER_STOP_INCOMPLETE", ex);
            }
            RaiseRuntimeChangedSafely();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session.SessionChanged -= Session_SessionChanged;
            _session.StreamConfigChanged -= Session_StreamConfigChanged;
            _session.StreamStopped -= Session_StreamStopped;
            StopHostCursorTelemetry();
            _lifetime.Cancel();
            VideoIncidentRecorder.Instance.RecordEvent("RUNTIME_DISPOSE");
            Task cleanup = BeginVideoTeardown(disposeDecoder: true);
            _ = cleanup.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception is not null)
                    StartupDiagnostics.RecordException("RUNTIME_DISPOSE_INCOMPLETE", t.Exception);
                _lifetime.Dispose();
            }, TaskScheduler.Default);
            // WinUI teardown is asynchronous. Retain native resources until
            // it completes instead of blocking the UI dispatcher or dropping
            // the last owner after a timeout.
        }
    }
}
