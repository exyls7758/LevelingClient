using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Multiverse.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using WinRT;

namespace Multiverse.Views
{
    public sealed partial class DevicesPage : Page
    {
        private NativeCaptureSession? _capture;
        private MonitorInfo? _activeMonitor;
        private CancellationTokenSource? _cancel;
        private Task? _previewLoopTask;
        private WriteableBitmap? _bitmap;

        private readonly Stopwatch _stats = new();

        private ulong _lastCapture;
        private ulong _lastEncoded;
        private ulong _lastSubmitted;
        private ulong _lastBytes;
        private long _lastEncoderSampleTimestamp;

        // CORE 11.1A DIAG: observational cadence sample only. These are UI-side
        // deltas of counters that already exist in the native capture pipeline.
        // No capture/encoder scheduling or power policy is changed by this build.
        private ulong _lastCadenceBackendFramesArrived;
        private ulong _lastCadenceBackendFramesDelivered;
        private ulong _lastCadenceBackendFramesPublished;
        private ulong _lastCadenceConsumedByEncoder;
        private ulong _lastCadenceBackendPacingDrops;
        private ulong _lastCadenceReplacedInRing;
        private ulong _lastCadenceRingBusyDrops;
        private ulong _lastCopyGpuReadyAtSubmit;
        private ulong _lastCopyGpuNotReadyAtSubmit;
        private uint _lastRemoteClockLateTicks;
        private bool _cadenceSampleInitialized;

        // CORE 11.12 RING/FENCE ISOLATION PROBE: deltas of native counters.
        private ulong _lastProbeRingAcquireCalls;
        private ulong _lastProbeRingNoReady;
        private ulong _lastProbeRingSelected;
        private ulong _lastProbeCaptureFenceReady;
        private ulong _lastProbeCaptureFencePending;
        private ulong _lastProbeNoReusableSlots;
        private bool _ringFenceProbeInitialized;

        // CORE 11.17c STABILITY SNAPSHOT: correlates WGC arrival, delivery-worker,
        // ring and NVENC telemetry with the native adaptive pacing state. It keeps
        // stable pressure/recovery snapshots so transient GPU contention remains
        // visible after the pipeline recovers.
        private enum HostStabilityProbeState
        {
            Warming,
            Healthy,
            Pressure,
            Recovering
        }

        private HostStabilityProbeState _hostStabilityState =
            HostStabilityProbeState.Warming;

        private int _hostPressureStreak;
        private int _hostHealthyStreak;
        private int _hostStabilityTransitions;
        private int _hostStabilityValidSamples;
        private long _hostStabilityProbeStartedAt;
        private double _hostWorstOutputFps = double.PositiveInfinity;
        private string _lastPressureSnapshot = "nenhuma";
        private string _lastRecoverySnapshot = "nenhuma";
        private string _worstStabilitySnapshot = "nenhuma";

        // CORE 11.1D DIAG: keep the UI alive while native StopEncoder blocks,
        // so the exact native shutdown checkpoint remains visible on screen.
        private bool _stopDiagnosticInProgress;
        private string _managedStopStage = "IDLE";
        private long _managedStopStageStartedAt;

        private int _previewFrames;
        private bool _encoderRunning;

        private IVideoTransport? _transport;
        private NativeNvdecPlayer? _decoder;

        private readonly RemoteSessionService
            _remoteSession =
                RemoteSessionService.Instance;

        private readonly RemoteStreamingRuntime
            _streamRuntime =
                RemoteStreamingRuntime.Instance;

        private readonly RemoteGamepadService
            _remoteGamepad =
                RemoteGamepadService.Instance;

        private readonly RemoteAudioService
            _remoteAudio =
                RemoteAudioService.Instance;

        private bool _refreshingAudioProcessList;
        private bool _refreshingAudioOutputDeviceList;
        private bool _loadingAudioClientUi;

        private readonly MultiverseSettingsService
            _settings =
                MultiverseSettingsService.Instance;

        private readonly DispatcherQueueTimer
            _remoteClientStatsTimer;

        private readonly DispatcherQueueTimer
            _remoteCursorRenderTimer;

        private readonly DispatcherQueueTimer
            _encoderSettingsApplyTimer;

        private bool _loadingEncoderUiSettings;
        private bool _encoderSettingsApplyInProgress;

        private uint _encoderTargetFps = 60;
        private uint _encoderTargetBitrateKbps = 20000;

        private uint _lastPublishedStreamWidth;
        private uint _lastPublishedStreamHeight;
        private uint _activeClientStreamWidth;
        private uint _activeClientStreamHeight;

        private long _lastDecoderRendered;
        private bool _loopbackHeadless;

        private long _lastTransportTxBytes;
        private long _lastTransportRxBytes;
        private long _lastTransportTxPackets;
        private long _lastTransportRxPackets;
        private long _lastTransportRxFrames;
        private long _lastTransportTxFrames;

        private long _lastLocalIdrServedAt;

        private const long LocalIdrCooldownMs =
            700;

        private readonly DispatcherQueueTimer
            _decoderResizeTimer;

        private ulong
            _lastRendererSubmittedObserved;

        private ulong
            _lastRendererPresentedObserved;

        private int
            _rendererStallIntervals;

        private int
            _rendererRecoveryAttempts;

        private long
            _lastRendererRecoveryAt;

        private bool
            _rendererRecoveryInProgress;

        private const int
            RendererStallIntervalsBeforeRecovery = 3;

        private const int
            MaxRendererRecoveryAttempts = 3;

        private const long
            RendererRecoveryCooldownMs = 5000;

        public DevicesPage()
        {
            InitializeComponent();
            ResetHostStabilityProbe();
            ResetRingFenceIsolationProbe();
            ConfigureRemoteStreamingProfileUi();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            ClientDecodeSwapChainPanel.SizeChanged +=
                ClientDecodeSwapChainPanel_SizeChanged;

            ClientDecodeFrame.SizeChanged +=
                ClientDecodeFrame_SizeChanged;

            _decoderResizeTimer =
                DispatcherQueue.CreateTimer();

            _decoderResizeTimer.Interval =
                TimeSpan.FromMilliseconds(
                    110);

            _decoderResizeTimer.IsRepeating =
                false;

            _decoderResizeTimer.Tick +=
                DecoderResizeTimer_Tick;

            _remoteClientStatsTimer =
                DispatcherQueue.CreateTimer();

            _remoteClientStatsTimer.Interval =
                TimeSpan.FromMilliseconds(
                    250);

            _remoteClientStatsTimer.IsRepeating =
                true;

            _remoteClientStatsTimer.Tick +=
                RemoteClientStatsTimer_Tick;

            _remoteCursorRenderTimer =
                DispatcherQueue.CreateTimer();

            _remoteCursorRenderTimer.Interval =
                TimeSpan.FromMilliseconds(
                    16);

            _remoteCursorRenderTimer.IsRepeating =
                true;

            _remoteCursorRenderTimer.Tick +=
                RemoteCursorRenderTimer_Tick;

            _encoderSettingsApplyTimer =
                DispatcherQueue.CreateTimer();

            _encoderSettingsApplyTimer.Interval =
                TimeSpan.FromMilliseconds(
                    300);

            _encoderSettingsApplyTimer.IsRepeating =
                false;

            _encoderSettingsApplyTimer.Tick +=
                EncoderSettingsApplyTimer_Tick;

            LoadEncoderUiSettings();

            EncoderFpsComboBox.SelectionChanged +=
                EncoderSettings_SelectionChanged;

            EncoderBitrateComboBox.SelectionChanged +=
                EncoderSettings_SelectionChanged;

            EncoderPresetComboBox.SelectionChanged +=
                EncoderSettings_SelectionChanged;

            EncoderTuningComboBox.SelectionChanged +=
                EncoderSettings_SelectionChanged;

            EncoderLabModeComboBox.SelectionChanged +=
                EncoderSettings_SelectionChanged;

            _remoteSession.Start();
        }

        private async void OnLoaded(
            object sender,
            RoutedEventArgs e)
        {
            FriendRequestPromptCoordinator.Instance.Attach(this);
            try
            {
                UpdateClientDecodeAspectRatio();

                _remoteSession.SessionChanged +=
                    RemoteSession_SessionChanged;

                _remoteSession.StreamConfigChanged +=
                    RemoteSession_StreamConfigChanged;

                _streamRuntime.RuntimeChanged +=
                    StreamRuntime_RuntimeChanged;

                _remoteAudio.AudioChanged +=
                    RemoteAudio_AudioChanged;

                RefreshAudioProcessList();
                RefreshAudioOutputDeviceList();
                UpdateAudioUi();

                RestoreRuntimeState();
                await RefreshHardwareAsync();
                RestoreRuntimeState();

                UpdateRemoteModeUi();

                if (_transport?.Running == true)
                    _remoteClientStatsTimer.Start();

                RemoteSessionSnapshot session =
                    _remoteSession.GetSnapshot();

                if (session.Role == RemoteSessionRole.Client &&
                    session.Connected &&
                    _remoteSession.GetStreamConfig() is not null)
                {
                    await StartRemoteClientTransportAsync();
                }
            }
            catch (Exception ex)
            {
                EncoderStatusTextBlock.Text =
                    $"Falha ao carregar Dispositivos: {ex.Message}";
                TransportStatusTextBlock.Text =
                    "A sessão existente foi preservada. Tente abrir a página novamente.";
            }
        }

        private async void OnUnloaded(
            object sender,
            RoutedEventArgs e)
        {
            FriendRequestPromptCoordinator.Instance.Detach(this);
            _decoderResizeTimer.Stop();
            _remoteClientStatsTimer.Stop();
            _remoteCursorRenderTimer.Stop();
            _encoderSettingsApplyTimer.Stop();

            _remoteSession.SessionChanged -=
                RemoteSession_SessionChanged;

            _remoteSession.StreamConfigChanged -=
                RemoteSession_StreamConfigChanged;

            _streamRuntime.RuntimeChanged -=
                StreamRuntime_RuntimeChanged;

            _remoteAudio.AudioChanged -=
                RemoteAudio_AudioChanged;

            RemoteSessionSnapshot session =
                _remoteSession.GetSnapshot();

            // CORE 11: navigation is not a streaming lifecycle event. The
            // process-wide runtime owns an active remote path while this Page
            // is unloaded, so Settings/Home/Sessions cannot tear the stream down.
            // Preserve even a temporarily-dead transport while the runtime
            // watchdog is rebuilding it.
            RemoteStreamingState runtimeState =
                _streamRuntime.GetState();
            bool serviceOwned = HostTransmissionService.IsValueCreated &&
                HostTransmissionService.Instance.IsUsingCapture(_capture);
            if ((session.Connected || serviceOwned) &&
                (runtimeState.Capture is not null ||
                 runtimeState.Transport is not null ||
                 runtimeState.Decoder is not null ||
                 _capture is not null ||
                 _transport is not null ||
                 _decoder is not null))
            {
                // Stop only this page's preview/readback coroutine. It is not
                // part of the native capture/encoder lifetime and retaining it
                // would keep an unloaded Page alive indefinitely.
                _cancel?.Cancel();
                _cancel?.Dispose();
                _cancel = null;
                _bitmap = null;
                PreviewImage.Source = null;

                if (session.Role == RemoteSessionRole.Host &&
                    _capture is not null && !serviceOwned)
                {
                    _streamRuntime.AdoptHost(
                        _capture,
                        _transport,
                        _encoderRunning,
                        _activeMonitor);
                }
                else if (session.Role == RemoteSessionRole.Client &&
                         _transport is not null &&
                         _decoder is not null)
                {
                    uint width = _activeClientStreamWidth;
                    uint height = _activeClientStreamHeight;
                    RemoteStreamConfig? config =
                        _remoteSession.GetStreamConfig();
                    if (config is not null)
                    {
                        width = config.Width;
                        height = config.Height;
                    }

                    _streamRuntime.AdoptClient(
                        _transport,
                        _decoder,
                        width,
                        height);

                    // The dedicated presenter remains alive independently
                    // from the navigation frame, like Parsec's session view.
                    _streamRuntime.ShowSessionWindow();
                }

                return;
            }

            await StopPreviewAsync();
        }

        private void RestoreRuntimeState()
        {
            RemoteStreamingState state =
                _streamRuntime.GetState();

            if (state.Capture is not null)
                _capture = state.Capture;

            if (state.Transport is not null)
                _transport = state.Transport;

            if (state.Decoder is not null)
                _decoder = state.Decoder;

            _encoderRunning =
                state.EncoderRunning;

            RemoteStreamConfig? config =
                _remoteSession.GetStreamConfig();
            if (config is not null &&
                _remoteSession.GetSnapshot().Role == RemoteSessionRole.Client)
            {
                _activeClientStreamWidth = config.Width;
                _activeClientStreamHeight = config.Height;
            }
        }

        private void StreamRuntime_RuntimeChanged(
            object? sender,
            EventArgs e)
        {
            DispatcherQueue.TryEnqueue(
                () =>
                {
                    RestoreRuntimeState();

                    if (_transport?.Running == true)
                    {
                        string mode =
                            _transport.GetSnapshot().Mode;

                        if (string.Equals(
                                mode,
                                "REMOTE_CLIENT",
                                StringComparison.Ordinal))
                        {
                            _remoteClientStatsTimer.Start();
                        }
                        else if (string.Equals(
                                     mode,
                                     "REMOTE_HOST",
                                     StringComparison.Ordinal))
                        {
                            _remoteClientStatsTimer.Start();
                        }
                    }

                    UpdateRemoteModeUi();
                });
        }

        private async void RefreshButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!ManualPipelineAvailable()) return;
            await StopPreviewAsync();
            if (_capture is not null) return;
            await RefreshHardwareAsync();
        }

        private async Task RefreshHardwareAsync()
        {
            CoreStatusTitle.Text =
                "Verificando hardware...";

            CoreStatusDescription.Text =
                "Consultando Direct3D, DXGI e NVENC.";

            CoreStatusBadge.Text =
                "Verificando";

            CoreStatusDot.Fill =
                Brush(120, 120, 120);

            GpuListPanel.Children.Clear();
            MonitorListPanel.Children.Clear();
            MonitorComboBox.Items.Clear();

            HardwareSnapshot hardware =
                await Task.Run(
                    NativeCoreService.ReadHardware);

            if (!hardware.NativeCoreLoaded)
            {
                CoreStatusTitle.Text =
                    "Core indisponível";

                CoreStatusDescription.Text =
                    hardware.Error ?? "Erro";

                CoreStatusBadge.Text =
                    "Erro";

                CoreStatusDot.Fill =
                    Brush(245, 92, 92);

                return;
            }

            CoreStatusTitle.Text =
                $"Leveling Core API v{hardware.ApiVersion}";

            CoreStatusDescription.Text =
                "Desktop Duplication + NVENC async preparados.";

            CoreStatusBadge.Text =
                "Pronto";

            CoreStatusDot.Fill =
                Brush(239, 43, 86);

            foreach (GpuInfo gpu in hardware.Gpus)
            {
                GpuListPanel.Children.Add(
                    Card(
                        gpu.Name,
                        $"VRAM dedicada: {gpu.DedicatedMemoryGb:0.#} GB"));
            }

            int primaryIndex = 0;

            for (int i = 0;
                 i < hardware.Monitors.Count;
                 i++)
            {
                MonitorInfo monitor =
                    hardware.Monitors[i];

                MonitorListPanel.Children.Add(
                    Card(
                        $"{monitor.DeviceName}" +
                        $"{(monitor.IsPrimary ? " • Principal" : "")}",

                        $"{monitor.Width} × {monitor.Height} • " +
                        $"{monitor.RefreshRate} Hz • Captura: " +
                        $"{(monitor.DesktopDuplicationReady ? "Disponível" : "Indisponível")}"));

                MonitorComboBox.Items.Add(
                    new ComboBoxItem
                    {
                        Content =
                            monitor.ToString(),

                        Tag = monitor
                    });

                if (monitor.IsPrimary)
                    primaryIndex = i;
            }

            if (hardware.Monitors.Count > 0)
            {
                string savedMonitor =
                    _settings.GetString(
                        "Multiverse.Capture.Monitor",
                        "");

                int selectedIndex = primaryIndex;

                if (!string.IsNullOrWhiteSpace(savedMonitor))
                {
                    for (int i = 0; i < MonitorComboBox.Items.Count; i++)
                    {
                        if (MonitorComboBox.Items[i] is ComboBoxItem combo &&
                            combo.Tag is MonitorInfo candidate &&
                            string.Equals(
                                candidate.DeviceName,
                                savedMonitor,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                }

                MonitorComboBox.SelectedIndex = selectedIndex;
                StartPreviewButton.IsEnabled = true;
            }

            NvencSupportTextBlock.Text =
                hardware.NvencCompiled
                    ? hardware.NvencRuntimeAvailable
                        ? "SDK integrado • nvEncodeAPI64.dll disponível • modo assíncrono com 4 buffers."
                        : "SDK integrado, mas o driver/NVENC runtime não foi encontrado."
                    : "Core compilado sem SDK NVENC. Confira NV_CODEC_SDK e recompile.";
        }

        private async void StartPreviewButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!ManualPipelineAvailable()) return;
            await StopPreviewAsync();
            if (_capture is not null || _transport is not null) return;

            if (MonitorComboBox.SelectedItem
                    is not ComboBoxItem item ||
                item.Tag is not MonitorInfo monitor)
            {
                return;
            }

            CaptureOpenResult open =
                await HostTransmissionService.Instance.OpenPreviewAsync(monitor);

            if (open.Session is null)
            {
                EncoderStatusTextBlock.Text =
                    open.Error ??
                    "Falha ao iniciar captura.";

                return;
            }

            _capture =
                open.Session;

            _streamRuntime.UpdateCapture(
                _capture,
                encoderRunning: false);

            _settings.SaveMonitor(monitor.DeviceName);

            _activeMonitor =
                monitor;

            _bitmap =
                new WriteableBitmap(
                    (int)_capture.PreviewWidth,
                    (int)_capture.PreviewHeight);

            PreviewImage.Source =
                _bitmap;

            PreviewPlaceholderPanel.Visibility =
                Visibility.Collapsed;

            SourceResolutionTextBlock.Text =
                $"{_capture.SourceWidth} × {_capture.SourceHeight}";

            StartPreviewButton.IsEnabled =
                false;

            StopPreviewButton.IsEnabled =
                true;

            MonitorComboBox.IsEnabled =
                false;

            StartEncoderButton.IsEnabled =
                true;

            _lastCapture = 0;
            _lastEncoded = 0;
            _lastSubmitted = 0;
            _lastBytes = 0;
            _lastEncoderSampleTimestamp = 0;
            ResetMouseCadenceDiagnosticsSample();

            _previewFrames = 0;
            _encoderRunning = false;

            StopTransport();

            ResetEncoderStatsText();
            ResetTransportStatsText();
            ResetDecoderStatsText();
            CapturePipelineTelemetryTextBlock.Text =
                "CAPTURE PIPELINE  •  aguardando dados";

            CapturePipelineDetailTextBlock.Text =
                "CAPTURE DETAIL  •  aguardando dados";

            _stats.Restart();

            _cancel =
                new CancellationTokenSource();

            _previewLoopTask =
                PreviewLoopAsync(
                    _cancel.Token);

            try
            {
                await _previewLoopTask;
            }
            catch (OperationCanceledException)
            {
                // Encerramento normal.
            }
            catch (Exception ex)
            {
                EncoderStatusTextBlock.Text =
                    ex.Message;
            }
            finally
            {
                _previewLoopTask = null;
            }
        }

        private async void StopPreviewButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            await StopPreviewAsync();
        }

        private async Task PreviewLoopAsync(
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested &&
                   _capture is not null &&
                   _bitmap is not null)
            {
                // During streaming the diagnostic preview is intentionally
                // frozen. ReadLatestPreview requests a GPU->CPU staging readback
                // in the native core; even at 2 FPS that can stall the capture
                // context when a foreground game saturates the GPU.
                if (_encoderRunning || _stopDiagnosticInProgress)
                {
                    UpdateStats();
                    await Task.Delay(
                        250,
                        cancellationToken);
                    continue;
                }

                CaptureReadResult result =
                    await Task.Run(
                        _capture.ReadLatestPreview,
                        cancellationToken);

                if (result.HResult < 0)
                    break;

                if (result.HasNewFrame)
                {
                    using Stream stream =
                        _bitmap.PixelBuffer.AsStream();

                    stream.Position = 0;

                    await stream.WriteAsync(
                        _capture.Buffer,
                        0,
                        _capture.Buffer.Length,
                        cancellationToken);

                    _bitmap.Invalidate();

                    _previewFrames++;

                    FrameIdTextBlock.Text =
                        $"#{result.FrameId}";
                }

                UpdateStats();

                // Sem encode, 30 FPS é suficiente para o preview diagnóstico.
                await Task.Delay(
                    33,
                    cancellationToken);
            }
        }

        private void ResetMouseCadenceDiagnosticsSample()
        {
            _lastCadenceBackendFramesArrived = 0;
            _lastCadenceBackendFramesDelivered = 0;
            _lastCadenceBackendFramesPublished = 0;
            _lastCadenceConsumedByEncoder = 0;
            _lastCadenceBackendPacingDrops = 0;
            _lastCadenceReplacedInRing = 0;
            _lastCadenceRingBusyDrops = 0;
            _lastCopyGpuReadyAtSubmit = 0;
            _lastCopyGpuNotReadyAtSubmit = 0;
            _lastRemoteClockLateTicks = 0;
            _cadenceSampleInitialized = false;
        }

        private void SetManagedStopStage(string stage)
        {
            _managedStopStage = stage;
            _managedStopStageStartedAt = Stopwatch.GetTimestamp();
        }

        private double ManagedStopStageAgeMs()
        {
            return _managedStopStageStartedAt == 0
                ? 0.0
                : Stopwatch.GetElapsedTime(
                    _managedStopStageStartedAt,
                    Stopwatch.GetTimestamp()).TotalMilliseconds;
        }

        private static string FormatProgressAge(double milliseconds)
        {
            return milliseconds < 0
                ? "--"
                : $"{milliseconds:0.0} ms";
        }

        private static string WgcStopStageName(uint stage) =>
            stage switch
            {
                0 => "IDLE",
                1 => "ENTER/DISABLE",
                2 => "REVOKE_FRAME",
                3 => "REVOKE_CLOSED",
                4 => "SESSION_CLOSE",
                5 => "FRAMEPOOL_CLOSE",
                6 => "CALLBACK_DRAIN",
                7 => "DELIVERY_WORKER",
                8 => "RELEASE",
                9 => "COMPLETE",
                _ => $"UNKNOWN({stage})"
            };

        private static string EncoderStopStageName(uint stage) =>
            stage switch
            {
                0 => "IDLE",
                1 => "ENTER",
                2 => "PRODUCERS_DISABLED",
                3 => "GPU_PRIORITY_RESET",
                4 => "WGC_STOP_BEGIN",
                5 => "WGC_STOP_END",
                6 => "WORKER_STOP_SIGNAL",
                7 => "WORKER_JOIN",
                8 => "WORKER_JOINED",
                9 => "NVENC_STOP",
                10 => "NVENC_STOPPED",
                11 => "CLEANUP",
                12 => "COMPLETE",
                _ => $"UNKNOWN({stage})"
            };

        private static string NvencStopStageName(uint stage) =>
            stage switch
            {
                0 => "IDLE",
                1 => "ENTER",
                2 => "FLUSH_BEGIN",
                3 => "FLUSH_RETURNED",
                4 => "OUTPUT_STOP_SIGNAL",
                5 => "OUTPUT_WORKER_JOIN",
                6 => "OUTPUT_WORKER_JOINED",
                7 => "RESOURCE_TEARDOWN",
                8 => "COMPLETE",
                _ => $"UNKNOWN({stage})"
            };

        private static string WgcStartStageName(uint stage) =>
            stage switch
            {
                0 => "IDLE",
                1 => "ENTER",
                2 => "STOP_PREVIOUS",
                3 => "IS_SUPPORTED",
                4 => "QUERY_DXGI_DEVICE",
                5 => "CREATE_WINRT_DEVICE",
                6 => "CREATE_ITEM",
                7 => "CREATE_FRAMEPOOL",
                8 => "CREATE_SESSION",
                9 => "CONFIGURE_SESSION",
                10 => "REGISTER_CALLBACKS",
                11 => "START_CAPTURE",
                12 => "COMPLETE",
                _ => $"UNKNOWN({stage})"
            };

        private static string FenceInitStageName(uint stage) =>
            stage switch
            {
                0 => "IDLE",
                1 => "QUERY_CAPTURE_DEVICE5",
                2 => "QUERY_ENCODE_DEVICE5",
                3 => "QUERY_CAPTURE_CONTEXT4",
                4 => "QUERY_ENCODE_CONTEXT4",
                5 => "CREATE_CAPTURE_FENCE",
                6 => "SHARE_CAPTURE_FENCE",
                7 => "OPEN_CAPTURE_FENCE_ENCODE",
                8 => "CREATE_RELEASE_FENCE",
                9 => "SHARE_RELEASE_FENCE",
                10 => "OPEN_RELEASE_FENCE_CAPTURE",
                11 => "CREATE_SHARED_TEXTURE",
                12 => "GET_SHARED_TEXTURE_HANDLE",
                13 => "OPEN_SHARED_TEXTURE_ENCODE",
                14 => "COMPLETE",
                _ => $"STAGE_{stage}"
            };

        private static string WgcCallbackStageName(uint stage) =>
            stage switch
            {
                0 => "IDLE",
                1 => "ENTER",
                2 => "DRAIN_POOL",
                3 => "PUBLISH_LATEST",
                _ => $"UNKNOWN({stage})"
            };

        private static string WgcCallbackSubstageName(uint stage) =>
            stage switch
            {
                0 => "NONE",
                1 => "TRY_GET_NEXT",
                2 => "GOT_FRAME",
                _ => $"UNKNOWN({stage})"
            };

        private static string CoreWgcStageName(uint stage) =>
            stage switch
            {
                0 => "IDLE",
                1 => "ENTER",
                2 => "INGRESS_PACER",
                3 => "FRAME_ID",
                4 => "ENSURE_RING",
                5 => "RING_PUBLISH",
                6 => "COMMIT",
                _ => $"UNKNOWN({stage})"
            };

        private static string RingPublishStageName(uint stage) =>
            stage switch
            {
                0 => "IDLE",
                1 => "SELECT_SLOT",
                2 => "REFRESH_RELEASE_FENCE",
                3 => "RESERVE_SLOT",
                4 => "COPY_RESOURCE",
                5 => "SIGNAL_CAPTURE_FENCE",
                6 => "FLUSH_CAPTURE",
                7 => "COMMIT",
                _ => $"UNKNOWN({stage})"
            };

        private static string EncoderWorkerStageName(uint stage) =>
            stage switch
            {
                0 => "IDLE",
                1 => "LOOP_TOP",
                2 => "APPLY_COMMANDS",
                3 => "NVENC_PUMP",
                4 => "REFRESH_STATS",
                5 => "SYNTHETIC",
                6 => "ACQUIRE_RING",
                7 => "ENSURE_ENCODER",
                8 => "SUBMIT_FRAME",
                9 => "RELEASE_RING",
                10 => "LEGACY_WAIT",
                11 => "LEGACY_SELECT",
                12 => "LEGACY_ENSURE",
                13 => "LEGACY_SUBMIT",
                14 => "LEGACY_RELEASE",
                15 => "REMOTE_FRAME_DEADLINE",
                _ => $"UNKNOWN({stage})"
            };

        private static string BuildPipelineLivenessText(
            CapturePipelineStats? capture,
            EncoderStats? encoder)
        {
            if (capture is null)
                return "PIPELINE LIVENESS  •  capture snapshot indisponível";

            string hint = "NONE";
            if (!capture.BackendRunning &&
                encoder is not null &&
                encoder.Running)
            {
                hint = "WGC_SESSION_NOT_RUNNING";
            }
            else if (capture.BackendRunning &&
                     capture.BackendLastCallbackAgoMs >= 0 &&
                     capture.BackendLastCallbackAgoMs < 250 &&
                     capture.LastPublishAgoMs > 750)
            {
                hint = "WGC→PUBLISH";
            }
            else if (capture.LastPublishAgoMs >= 0 &&
                     capture.LastPublishAgoMs < 250 &&
                     capture.LastConsumeAgoMs > 750)
            {
                hint = "RING→ENCODER_WORKER";
            }
            else if (encoder is not null &&
                     capture.LastConsumeAgoMs >= 0 &&
                     capture.LastConsumeAgoMs < 250 &&
                     encoder.LastSubmitAgoMs > 750)
            {
                hint = "CONSUME→NVENC_SUBMIT";
            }
            else if (encoder is not null &&
                     encoder.LastSubmitAgoMs >= 0 &&
                     encoder.LastSubmitAgoMs < 250 &&
                     encoder.LastOutputAgoMs > 750)
            {
                hint = "NVENC_SUBMIT→OUTPUT";
            }
            else if (encoder is not null &&
                     encoder.Running &&
                     !encoder.EncoderWorkerAlive)
            {
                hint = "ENCODER_WORKER_EXITED";
            }
            else if (encoder is not null &&
                     encoder.Running &&
                     !encoder.OutputWorkerAlive)
            {
                hint = "OUTPUT_WORKER_EXITED";
            }

            return
                $"PIPELINE LIVENESS  •  WGC run {(capture.BackendRunning ? "YES" : "NO")}  •  cb-inflight {capture.BackendCallbacksInFlight}  •  " +
                $"last-cb {FormatProgressAge(capture.BackendLastCallbackAgoMs)}  •  cb-done {FormatProgressAge(capture.BackendLastCallbackCompleteAgoMs)}  •  " +
                $"publish {FormatProgressAge(capture.LastPublishAgoMs)}  •  consume {FormatProgressAge(capture.LastConsumeAgoMs)}  •  " +
                $"enc-worker {(capture.EncoderWorkerAlive ? "ALIVE" : "DEAD")}  •  " +
                $"submit {(encoder is null ? "--" : FormatProgressAge(encoder.LastSubmitAgoMs))}  •  " +
                $"output {(encoder is null ? "--" : FormatProgressAge(encoder.LastOutputAgoMs))}  •  " +
                $"out-worker {(encoder is null ? "--" : (encoder.OutputWorkerAlive ? "ALIVE" : "DEAD"))}  •  hint {hint}\n" +
                $"CALLBACK STAGE DIAG  •  WGC {WgcCallbackStageName(capture.BackendCallbackStage)}/{WgcCallbackSubstageName(capture.BackendCallbackSubstage)} " +
                $"/ {FormatProgressAge(capture.BackendCallbackStageAgeMs)}  •  drain {capture.BackendCurrentDrainFrames}  •  " +
                $"core {CoreWgcStageName(capture.CoreWgcStage)} / {FormatProgressAge(capture.CoreWgcStageAgeMs)}  •  " +
                $"ring {RingPublishStageName(capture.RingPublishStage)} / {FormatProgressAge(capture.RingPublishStageAgeMs)}  •  " +
                $"worker {EncoderWorkerStageName(capture.EncoderWorkerStage)} / {FormatProgressAge(capture.EncoderWorkerStageAgeMs)}";
        }

        private void UpdateStopDiagnostics()
        {
            NativeCaptureSession? captureSession = _capture;
            if (captureSession is null)
                return;

            CapturePipelineStats? capture =
                captureSession.GetCapturePipelineStatsDiagnostic();
            EncoderStats? encoder =
                captureSession.GetEncoderStatsDiagnostic();

            string wgcStop =
                capture is null
                    ? "--"
                    : $"{WgcStopStageName(capture.BackendStopStage)} / {FormatProgressAge(capture.BackendStopStageAgeMs)}";

            string encoderStop =
                encoder is null
                    ? "--"
                    : $"{EncoderStopStageName(encoder.StopStage)} / {FormatProgressAge(encoder.StopStageAgeMs)}";

            string nvencStop =
                encoder is null
                    ? "--"
                    : $"{NvencStopStageName(encoder.NvencStopStage)} / {FormatProgressAge(encoder.NvencStopStageAgeMs)}";

            EncoderDiagnosticsTextBlock.Text =
                BuildPipelineLivenessText(capture, encoder) + "\n" +
                $"STOP DIAG  •  managed {_managedStopStage} / {ManagedStopStageAgeMs():0} ms  •  " +
                $"native {encoderStop}  •  NVENC {nvencStop}  •  WGC {wgcStop}  •  " +
                $"stop-active {(encoder?.StopInProgress == true ? "YES" : "NO")} / WGC {(capture?.BackendStopInProgress == true ? "YES" : "NO")}";
        }

        private void UpdateStats()
        {
            if (_stats.Elapsed.TotalSeconds < 0.75)
            {
                return;
            }

            double seconds =
                _stats.Elapsed.TotalSeconds;

            RuntimeTelemetryTextBlock.Text = RemotePipelineTelemetry.Instance.GetDisplayText();
            UpdateGamepadUi();
            UpdateAudioUi();

            if (_stopDiagnosticInProgress)
            {
                UpdateStopDiagnostics();
                _stats.Restart();
                return;
            }

            if (_capture is not null)
            {
                double cadenceWgcArrivalFps = 0.0;
                double cadenceWgcFps = 0.0;
                double cadencePublishFps = 0.0;
                double cadenceConsumeFps = 0.0;
                double cadencePacingDropFps = 0.0;
                double cadenceReplaceFps = 0.0;
                double cadenceBusyDropFps = 0.0;
                double copyGpuReadyAtSubmitFps = 0.0;
                double copyGpuNotReadyAtSubmitFps = 0.0;
                double cadenceWgcToPublishGapFps = 0.0;
                double cadencePublishToConsumeGapFps = 0.0;
                double remoteClockLateFps = 0.0;
                bool cadenceSampleValid = false;
                string cadenceBackend = "--";
                uint cadenceTargetFps = 0;

                ulong captureFrames =
                    _capture.GetFrameCount();

                CaptureFpsTextBlock.Text =
                    $"{(captureFrames - _lastCapture) / seconds:0.0} upd/s";

                PreviewFpsTextBlock.Text =
                    $"{_previewFrames / seconds:0.0} FPS";

                _lastCapture =
                    captureFrames;

                _previewFrames = 0;

                CapturePipelineStats? capturePipeline =
                    _capture.GetCapturePipelineStats();

                if (capturePipeline is not null)
                {
                    cadenceBackend =
                        capturePipeline.CaptureBackend == 1
                            ? "WGC"
                            : "DXGI";
                    cadenceTargetFps = capturePipeline.TargetPublishFps;

                    bool cadenceCountersMonotonic =
                        !_cadenceSampleInitialized ||
                        (capturePipeline.BackendFramesArrived >= _lastCadenceBackendFramesArrived &&
                         capturePipeline.BackendFramesDelivered >= _lastCadenceBackendFramesDelivered &&
                         capturePipeline.BackendFramesPublished >= _lastCadenceBackendFramesPublished &&
                         capturePipeline.ConsumedByEncoder >= _lastCadenceConsumedByEncoder &&
                         capturePipeline.BackendPacingDrops >= _lastCadenceBackendPacingDrops &&
                         capturePipeline.ReplacedInRing >= _lastCadenceReplacedInRing &&
                         capturePipeline.RingBusyDrops >= _lastCadenceRingBusyDrops &&
                         capturePipeline.CopyGpuReadyAtSubmit >= _lastCopyGpuReadyAtSubmit &&
                         capturePipeline.CopyGpuNotReadyAtSubmit >= _lastCopyGpuNotReadyAtSubmit &&
                         capturePipeline.RemoteClockLateTicks >= _lastRemoteClockLateTicks);

                    if (_cadenceSampleInitialized &&
                        cadenceCountersMonotonic &&
                        seconds > 0.05)
                    {
                        cadenceWgcArrivalFps =
                            (capturePipeline.BackendFramesArrived - _lastCadenceBackendFramesArrived) / seconds;
                        cadenceWgcFps =
                            (capturePipeline.BackendFramesDelivered - _lastCadenceBackendFramesDelivered) / seconds;
                        cadencePublishFps =
                            (capturePipeline.BackendFramesPublished - _lastCadenceBackendFramesPublished) / seconds;
                        cadenceConsumeFps =
                            (capturePipeline.ConsumedByEncoder - _lastCadenceConsumedByEncoder) / seconds;
                        cadencePacingDropFps =
                            (capturePipeline.BackendPacingDrops - _lastCadenceBackendPacingDrops) / seconds;
                        cadenceReplaceFps =
                            (capturePipeline.ReplacedInRing - _lastCadenceReplacedInRing) / seconds;
                        cadenceBusyDropFps =
                            (capturePipeline.RingBusyDrops - _lastCadenceRingBusyDrops) / seconds;
                        copyGpuReadyAtSubmitFps =
                            (capturePipeline.CopyGpuReadyAtSubmit - _lastCopyGpuReadyAtSubmit) / seconds;
                        copyGpuNotReadyAtSubmitFps =
                            (capturePipeline.CopyGpuNotReadyAtSubmit - _lastCopyGpuNotReadyAtSubmit) / seconds;
                        remoteClockLateFps =
                            (capturePipeline.RemoteClockLateTicks - _lastRemoteClockLateTicks) / seconds;

                        cadenceWgcToPublishGapFps =
                            Math.Max(0.0, cadenceWgcFps - cadencePublishFps);
                        cadencePublishToConsumeGapFps =
                            Math.Max(0.0, cadencePublishFps - cadenceConsumeFps);
                        cadenceSampleValid = true;
                    }

                    _lastCadenceBackendFramesArrived = capturePipeline.BackendFramesArrived;
                    _lastCadenceBackendFramesDelivered = capturePipeline.BackendFramesDelivered;
                    _lastCadenceBackendFramesPublished = capturePipeline.BackendFramesPublished;
                    _lastCadenceConsumedByEncoder = capturePipeline.ConsumedByEncoder;
                    _lastCadenceBackendPacingDrops = capturePipeline.BackendPacingDrops;
                    _lastCadenceReplacedInRing = capturePipeline.ReplacedInRing;
                    _lastCadenceRingBusyDrops = capturePipeline.RingBusyDrops;
                    _lastCopyGpuReadyAtSubmit = capturePipeline.CopyGpuReadyAtSubmit;
                    _lastCopyGpuNotReadyAtSubmit = capturePipeline.CopyGpuNotReadyAtSubmit;
                    _lastRemoteClockLateTicks = capturePipeline.RemoteClockLateTicks;
                    _cadenceSampleInitialized = cadenceCountersMonotonic;

                    CapturePipelineTelemetryTextBlock.Text =
                        $"CAPTURE PIPELINE  •  accum {capturePipeline.LastAccumulatedFrames}  •  " +
                        $"miss {capturePipeline.DxgiMissedFrames}  •  ring {capturePipeline.RingReadyFrames}/4  •  " +
                        $"pub {capturePipeline.PublishedToRing} / enc {capturePipeline.ConsumedByEncoder}  •  " +
                        $"pace {capturePipeline.PublishPacingDrops}@{capturePipeline.TargetPublishFps}  •  " +
                        $"replace {capturePipeline.ReplacedInRing}  •  busy {capturePipeline.RingBusyDrops}  •  " +
                        $"acquire {capturePipeline.LastAcquireMs:0.00} ms  •  copy {capturePipeline.LastRingCopyMs:0.00} ms";

                    string captureHr =
                        capturePipeline.LastCaptureStatus >= 0
                            ? "OK"
                            : $"0x{unchecked((uint)capturePipeline.LastCaptureStatus):X8}";

                    CapturePipelineDetailTextBlock.Text =
                        $"CAPTURE DETAIL  •  present {capturePipeline.DesktopPresents}  •  ptr {capturePipeline.PointerOnlyUpdates}  •  " +
                        $"timeout {capturePipeline.AcquireTimeouts}  •  recover {capturePipeline.DuplicationResets}  •  " +
                        $"enc-restart {capturePipeline.EncoderRestarts}  •  acquire avg {capturePipeline.AverageAcquireMs:0.00} ms  •  " +
                        $"copy avg {capturePipeline.AverageRingCopyMs:0.00} ms  •  status {captureHr}\n" +
                        $"CORE11 FENCE RING  •  active {(capturePipeline.Core10IsolationActive ? "sim" : "não")}  •  ready {capturePipeline.SharedReadyFrames}/3  •  " +
                        $"CPU-wait {capturePipeline.SharedCaptureWaitMs:0.000}/{capturePipeline.SharedEncodeWaitMs:0.000} ms  •  " +
                        $"fence-fail cap/enc {capturePipeline.SharedCaptureSyncTimeouts}/{capturePipeline.SharedEncodeSyncTimeouts}  •  unsafe {capturePipeline.SharedSyncAbandoned}\n" +
                        $"FENCE INIT  •  stage {FenceInitStageName(capturePipeline.FenceInitStage)}  •  " +
                        $"hr {(capturePipeline.LastFenceInitStatus >= 0 ? "OK" : $"0x{unchecked((uint)capturePipeline.LastFenceInitStatus):X8}")}\n" +
                        $"CORE 11.17g WGC14 DELIVERY  •  backend {(capturePipeline.CaptureBackend == 1 ? "WGC" : "DXGI")}  •  " +
                        $"arrived {capturePipeline.BackendFramesArrived} / delivered {capturePipeline.BackendFramesDelivered} / pub {capturePipeline.BackendFramesPublished}  •  " +
                        $"pending-replace {capturePipeline.BackendPendingFramesReplaced}  •  worker {(capturePipeline.BackendDeliveryWorkerAlive ? "ON" : "OFF")}  •  " +
                        $"delivery-delay {capturePipeline.BackendLastDeliveryDelayMs:0.00} ms  •  pace {capturePipeline.BackendPacingDrops}  •  " +
                        $"empty {capturePipeline.BackendEmptyCallbacks}  •  restart {capturePipeline.BackendRestarts}  •  " +
                        $"callback {capturePipeline.LastBackendCallbackMs:0.000}/{capturePipeline.AverageBackendCallbackMs:0.000} ms  •  " +
                        $"age {capturePipeline.LastBackendFrameAgeMs:0.00}/{capturePipeline.AverageBackendFrameAgeMs:0.00} ms  •  " +
                        $"status {(capturePipeline.LastBackendStatus >= 0 ? "OK" : $"0x{unchecked((uint)capturePipeline.LastBackendStatus):X8}")}\n" +
                        $"COPY GPU  •  EVENT {capturePipeline.CopyGpuProbeCompleted}/{capturePipeline.CopyGpuProbeSignals} done  •  pending {capturePipeline.CopyGpuProbePending} (max {capturePipeline.CopyGpuProbeMaxPending})  •  " +
                        $"lat {capturePipeline.CopyGpuLastLatencyMs:0.00}/{capturePipeline.CopyGpuAverageLatencyMs:0.00}/{capturePipeline.CopyGpuMaxLatencyMs:0.00} ms last/avg/max  •  oldest {capturePipeline.CopyGpuOldestPendingMs:0.0} ms  •  " +
                        $"ready-at-submit {capturePipeline.CopyGpuReadyAtSubmit} / not-ready {capturePipeline.CopyGpuNotReadyAtSubmit}  •  " +
                        $"probe {(capturePipeline.CopyGpuProbeStatus >= 0 ? "OK" : $"0x{unchecked((uint)capturePipeline.CopyGpuProbeStatus):X8}")}\n" +
                        $"WGC START  •  stage {WgcStartStageName(capturePipeline.BackendStartStage)}  •  " +
                        $"hr {(capturePipeline.LastBackendStartStatus >= 0 ? "OK" : $"0x{unchecked((uint)capturePipeline.LastBackendStartStatus):X8}")}";
                }

                EncoderStats? encoder =
                    _capture.GetEncoderStats();

                if (encoder is not null &&
                    encoder.Running)
                {
                    long encoderSampleNow = Stopwatch.GetTimestamp();
                    double encoderSampleSeconds =
                        _lastEncoderSampleTimestamp != 0
                            ? Stopwatch.GetElapsedTime(
                                _lastEncoderSampleTimestamp,
                                encoderSampleNow).TotalSeconds
                            : 0.0;

                    bool countersValid =
                        encoder.EncodedFrames >= _lastEncoded &&
                        encoder.SubmittedFrames >= _lastSubmitted &&
                        encoder.EncodedBytes >= _lastBytes;

                    double outputFps = 0.0;
                    double submitFps = 0.0;
                    double outputMbps = 0.0;

                    if (countersValid && encoderSampleSeconds > 0.05)
                    {
                        outputFps =
                            (encoder.EncodedFrames - _lastEncoded) /
                            encoderSampleSeconds;
                        submitFps =
                            (encoder.SubmittedFrames - _lastSubmitted) /
                            encoderSampleSeconds;
                        outputMbps =
                            (encoder.EncodedBytes - _lastBytes) * 8.0 /
                            encoderSampleSeconds / 1_000_000.0;
                    }

                    EncoderFpsTextBlock.Text =
                        _lastEncoderSampleTimestamp == 0
                            ? "-- FPS"
                            : $"{outputFps:0.0} FPS";

                    EncoderOutputBitrateTextBlock.Text =
                        $"{outputMbps:0.0} Mbps";

                    EncoderLatencyTextBlock.Text =
                        $"{encoder.AverageOutputLatencyMs:0.00} ms";

                    EncoderSubmitTextBlock.Text =
                        $"{encoder.AverageSubmitMs:0.00} ms";

                    EncoderQueueTextBlock.Text =
                        $"{encoder.InFlightFrames}/{encoder.InputSlots} • " +
                        $"{encoder.NoFreeSlotDrops}";

                    string inputMode =
                        encoder.InputMode switch
                        {
                            1 => "NV12/DXVA",
                            2 => "ARGB/D3D11",
                            _ => "desconhecido"
                        };

                    string requestedLab =
                        encoder.RequestedInputMode switch
                        {
                            1 => "LEGACY/NV12",
                            2 => "LEGACY/ARGB",
                            3 => "SINTÉTICO",
                            4 => "CORE10/DXGI",
                            5 => "CORE10.1/WGC-BGRA",
                            10 => "CORE11.16/WGC-SAME-DEVICE-DIRECT",
                            9 => "CORE11.15/NATIVE-HOST",
                            8 => "CORE11.14/WGC-RGB-SCALE",
                            7 => "CORE11.13/WGC-DIRECT-RGB",
                            6 => "CORE11/WGC-NV12",
                            _ => "AUTO"
                        };

                    string sourceMode =
                        encoder.SourceMode == 1
                            ? "sintético"
                            : "captura";

                    string encoderHr =
                        encoder.LastStatus >= 0
                            ? "OK"
                            : $"0x{unchecked((uint)encoder.LastStatus):X8}";

                    string gpuHeadroomState = "WARMING";
                    double avoidedGpuCopiesFps = 0.0;
                    if (capturePipeline is not null && cadenceSampleValid && cadenceTargetFps > 0)
                    {
                        avoidedGpuCopiesFps = Math.Max(0.0, cadenceWgcFps - cadencePublishFps);
                        double sourceFloor = cadenceTargetFps * 0.85;
                        double pipelineFloor = cadenceTargetFps * 0.85;
                        if (cadenceWgcFps < sourceFloor)
                        {
                            gpuHeadroomState = "SOURCE-LIMITED";
                        }
                        else if (outputFps < pipelineFloor &&
                                 (cadenceConsumeFps < pipelineFloor || remoteClockLateFps >= 1.0))
                        {
                            gpuHeadroomState = "PIPELINE-PRESSURE";
                        }
                        else
                        {
                            gpuHeadroomState = "OK";
                        }
                    }

                    string gpuSchedulingMode =
                        (encoder.Core10PipelineMode == 9 || encoder.Core10PipelineMode == 10) &&
                        encoder.CaptureGpuPriority == 0 &&
                        encoder.EncodeGpuPriority == 0
                            ? "FAIR/NEUTRAL"
                            : (encoder.CaptureGpuPriority >= 2 && encoder.EncodeGpuPriority >= 3
                                ? "BOOST"
                                : "NORMAL");

                    string processGpuRequestedClass = encoder.ProcessGpuPriorityRequested switch
                    {
                        0 => "IDLE",
                        1 => "BELOW_NORMAL",
                        2 => "NORMAL",
                        3 => "ABOVE_NORMAL",
                        4 => "HIGH",
                        5 => "REALTIME",
                        _ => $"UNKNOWN({encoder.ProcessGpuPriorityRequested})"
                    };
                    string processGpuClass = encoder.ProcessGpuPriorityEffective switch
                    {
                        0 => "IDLE",
                        1 => "BELOW_NORMAL",
                        2 => "NORMAL",
                        3 => "ABOVE_NORMAL",
                        4 => "HIGH",
                        5 => "REALTIME",
                        _ => $"UNKNOWN({encoder.ProcessGpuPriorityEffective})"
                    };
                    string processGpuSetState =
                        encoder.ProcessGpuPrioritySetStatus == 0
                            ? (encoder.ProcessGpuPriorityRequested == encoder.ProcessGpuPriorityEffective
                                ? "OK"
                                : "MISMATCH")
                            : (encoder.ProcessGpuPrioritySetStatus == 1
                                ? "FALLBACK"
                                : $"FAIL 0x{unchecked((uint)encoder.ProcessGpuPrioritySetStatus):X8}");

                    EncoderDiagnosticsTextBlock.Text =
                        $"ENCODER DIAG  •  lab {requestedLab}  •  source {sourceMode}  •  input {inputMode}  •  submit fps {submitFps:0.0} / out fps {outputFps:0.0}  •  " +
                        $"submit {encoder.SubmittedFrames} / out {encoder.EncodedFrames}  •  need-in {encoder.NeedMoreInputAccepted}  •  busy-retry {encoder.EncoderBusyRetries}  •  " +
                        $"no-slot {encoder.NoFreeSlotDrops}  •  pending {encoder.PendingOutputs}/{encoder.InputSlots}  •  oldest {encoder.OldestInFlightMs:0.00} ms  •  " +
                        $"q {encoder.QueuedPackets} pkt / {encoder.QueuedBytes / 1024.0:0.0} KB  •  cmd {encoder.PendingCommands}  •  " +
                        $"statswait {encoder.StatsReadWaitMs:0.000} ms  •  maperr {encoder.MapFailures}  •  encerr {encoder.EncodeErrors}  •  " +
                        $"lockbusy {encoder.LockBusyRetries}  •  flush {encoder.FlushTimeouts}  •  status {encoderHr}  •  " +
                        $"pipeline {encoder.Core10PipelineMode}  •  capture-device {(encoder.CaptureDeviceIsolated ? "ISOLADO" : "LEGADO")}  •  " +
                        $"GPU-SCHED {gpuSchedulingMode}  •  cap/enc {encoder.CaptureGpuPriority}/{encoder.EncodeGpuPriority}  •  verify 0x{unchecked((uint)encoder.GpuPrioritySetStatus):X8}  •  " +
                        $"PROC-GPU req {processGpuRequestedClass}({encoder.ProcessGpuPriorityRequested}) → {processGpuClass}({encoder.ProcessGpuPriorityEffective})  •  {processGpuSetState}  •  restore 0x{unchecked((uint)encoder.ProcessGpuPriorityRestoreStatus):X8}\n" +
                        $"PRESSURE OBSERVER  •  {AdaptivePressureStateName(capturePipeline?.AdaptivePressureState ?? 0)}  •  fixed target {capturePipeline?.AdaptiveTargetFps ?? 0} fps  •  " +
                        $"streak P/H {capturePipeline?.AdaptivePressureStreak ?? 0}/{capturePipeline?.AdaptiveHealthyStreak ?? 0}  •  " +
                        $"transitions {capturePipeline?.AdaptiveTransitions ?? 0}  •  ring-occ {capturePipeline?.CaptureRingOccupiedFrames ?? 0}/4\n" +
                        $"GPU HEADROOM  •  {gpuHeadroomState}  •  ingress-cap {capturePipeline?.IngressPublishCapFps ?? 0} fps  •  " +
                        $"GPU-copies {cadencePublishFps:0.0}/s  •  avoided {avoidedGpuCopiesFps:0.0}/s  •  " +
                        $"deadline-late {remoteClockLateFps:0.0}/s ({capturePipeline?.RemoteClockLateTicks ?? 0} total)  •  " +
                        $"copy-CPU {(capturePipeline?.AverageRingCopyMs ?? 0.0):0.00} ms  •  VP {encoder.AveragePreprocessMs:0.00} ms\n" +
                        $"COPY GPU EVENT  •  ready/not-ready {copyGpuReadyAtSubmitFps:0.0}/{copyGpuNotReadyAtSubmitFps:0.0}/s  •  " +
                        $"pending {capturePipeline?.CopyGpuProbePending ?? 0}  •  oldest {(capturePipeline?.CopyGpuOldestPendingMs ?? 0.0):0.0} ms  •  " +
                        $"lat {(capturePipeline?.CopyGpuLastLatencyMs ?? 0.0):0.00}/{(capturePipeline?.CopyGpuAverageLatencyMs ?? 0.0):0.00}/{(capturePipeline?.CopyGpuMaxLatencyMs ?? 0.0):0.00} ms last/avg/max\n" +
                        $"ENCODER STAGES  •  prep {encoder.LastPreprocessMs:0.00}/{encoder.AveragePreprocessMs:0.00}  •  " +
                        $"map {encoder.LastMapMs:0.00}/{encoder.AverageMapMs:0.00}  •  " +
                        $"encode-call {encoder.LastEncodeCallMs:0.00}/{encoder.AverageEncodeCallMs:0.00}  •  " +
                        $"lock {encoder.LastLockBitstreamMs:0.00}/{encoder.AverageLockBitstreamMs:0.00} ms (last/avg)\n" +
                        (cadenceSampleValid
                            ? $"FRAME CADENCE DIAG  •  CORE 11.17g WGC14 fixed-target deadline-aware {cadenceTargetFps} Hz  •  {cadenceBackend} in {cadenceWgcArrivalFps:0.0} / deliver {cadenceWgcFps:0.0} fps  •  ring-pub {cadencePublishFps:0.0}  •  unique {cadenceConsumeFps:0.0}  •  submit {submitFps:0.0}  •  out {outputFps:0.0}  •  target {cadenceTargetFps}  •  " +
                              $"pace-drop {cadencePacingDropFps:0.0}/s  •  replace {cadenceReplaceFps:0.0}/s  •  busy {cadenceBusyDropFps:0.0}/s  •  " +
                              $"gap deliver→ring {cadenceWgcToPublishGapFps:0.0}  •  ring-skip {cadencePublishToConsumeGapFps:0.0}"
                            : "FRAME CADENCE DIAG  •  deadline-aware aquecendo amostra... mantenha a tela estável por ~1 s") +
                        "\n" +
                        BuildPipelineLivenessText(capturePipeline, encoder);

                    UpdateHostStabilityProbe(
                        encoderSampleSeconds > 0.05 &&
                        (cadenceSampleValid || encoder.RequestedInputMode == 3),
                        cadenceBackend,
                        cadenceTargetFps,
                        cadenceWgcArrivalFps,
                        cadenceWgcFps,
                        cadencePublishFps,
                        cadenceConsumeFps,
                        submitFps,
                        outputFps,
                        cadenceBusyDropFps,
                        remoteClockLateFps,
                        copyGpuNotReadyAtSubmitFps,
                        capturePipeline?.CopyGpuProbePending ?? 0,
                        capturePipeline?.CopyGpuOldestPendingMs ?? 0.0,
                        capturePipeline?.CopyGpuLastLatencyMs ?? 0.0,
                        capturePipeline?.CopyGpuAverageLatencyMs ?? 0.0,
                        capturePipeline?.AdaptivePressureState ?? 0,
                        capturePipeline?.AdaptiveTargetFps ?? 0,
                        capturePipeline?.AdaptiveTransitions ?? 0,
                        encoder);

                    UpdateRingFenceIsolationProbe(
                        capturePipeline,
                        encoder,
                        encoderSampleSeconds);

                    _lastEncoded =
                        encoder.EncodedFrames;
                    _lastSubmitted =
                        encoder.SubmittedFrames;
                    _lastBytes =
                        encoder.EncodedBytes;
                    _lastEncoderSampleTimestamp =
                        encoderSampleNow;

                    RemoteSessionSnapshot liveSession =
                        _remoteSession.GetSnapshot();

                    if (liveSession.Connected &&
                        liveSession.Role ==
                            RemoteSessionRole.Host &&
                        _transport?.Running == true &&
                        string.Equals(
                            _transport.GetSnapshot().Mode,
                            "REMOTE_HOST",
                            StringComparison.Ordinal) &&
                        encoder.Width > 0 &&
                        encoder.Height > 0 &&
                        (encoder.Width !=
                            _lastPublishedStreamWidth ||
                         encoder.Height !=
                            _lastPublishedStreamHeight))
                    {
                        // Fullscreen/exclusive e alguns loadings podem trocar o
                        // modo do output. O native core recria o NVENC; aqui
                        // avisamos o cliente para ele recriar o NVDEC com o
                        // tamanho correto e pedimos uma IDR limpa.
                        _lastPublishedStreamWidth =
                            encoder.Width;
                        _lastPublishedStreamHeight =
                            encoder.Height;

                        _ = PublishRecoveredStreamConfigAsync(
                            encoder.Width,
                            encoder.Height);
                    }

                    if (encoder.LastStatus < 0)
                    {
                        EncoderStatusTextBlock.Text =
                            $"NVENC reportou HRESULT 0x{encoder.LastStatus:X8}.";
                    }
                }
            }

            UpdateTransportStats(
                seconds);

            UpdateDecoderStats(
                seconds);

            _stats.Restart();
        }

        private async Task PublishRecoveredStreamConfigAsync(
            uint width,
            uint height)
        {
            try
            {
                await HostTransmissionService.Instance.RefreshStreamGeometryAsync(_capture);

                RequestLocalIdrDebounced(
                    force: true);
            }
            catch
            {
                // O keepalive/transporte seguem vivos; o próximo ciclo de
                // telemetria tentará novamente se o modo ainda estiver diferente.
                _lastPublishedStreamWidth = 0;
                _lastPublishedStreamHeight = 0;
            }
        }

        private void UpdateTransportStats(
            double seconds)
        {
            if (_transport is null ||
                seconds <= 0)
            {
                return;
            }

            TransportSnapshot snapshot =
                _transport.GetSnapshot();

            long txBytes =
                snapshot.SentBytes -
                _lastTransportTxBytes;

            long rxBytes =
                snapshot.ReceivedBytes -
                _lastTransportRxBytes;

            long txPackets =
                snapshot.SentPackets -
                _lastTransportTxPackets;

            long rxPackets =
                snapshot.ReceivedPackets -
                _lastTransportRxPackets;

            long rxFrames =
                snapshot.ReceivedFrames -
                _lastTransportRxFrames;

            long txFrames =
                snapshot.SentFrames -
                _lastTransportTxFrames;

            double txMbps =
                txBytes *
                8.0 /
                seconds /
                1_000_000.0;

            double rxMbps =
                rxBytes *
                8.0 /
                seconds /
                1_000_000.0;

            bool hostMode =
                string.Equals(
                    snapshot.Mode,
                    "REMOTE_HOST",
                    StringComparison.Ordinal);

            double packetRate =
                (hostMode
                    ? txPackets
                    : rxPackets) /
                seconds;

            double frameRate =
                (hostMode
                    ? txFrames
                    : rxFrames) /
                seconds;

            TransportTxTextBlock.Text =
                $"{txMbps:0.0} Mbps";

            TransportRxTextBlock.Text =
                $"{rxMbps:0.0} Mbps";

            TransportFrameRateTextBlock.Text =
                $"{frameRate:0.0} FPS";

            TransportPacketRateTextBlock.Text =
                $"{packetRate:0} pkt/s";

            double networkLatency =
                snapshot.RoundTripMs > 0
                    ? snapshot.RoundTripMs
                    : snapshot.AverageLoopbackOneWayMs;

            TransportLatencyTextBlock.Text =
                snapshot.RoundTripMs > 0
                    ? $"{networkLatency:0.0} ms RTT"
                    : $"{networkLatency:0.00} ms";

            TransportDropTextBlock.Text =
                $"{snapshot.IncompleteFramesDropped} / " +
                $"{snapshot.SequenceGaps}";

            TransportPendingTextBlock.Text =
                snapshot.PendingFrames
                    .ToString();

            _lastTransportTxBytes =
                snapshot.SentBytes;

            _lastTransportRxBytes =
                snapshot.ReceivedBytes;

            _lastTransportTxPackets =
                snapshot.SentPackets;

            _lastTransportRxPackets =
                snapshot.ReceivedPackets;

            _lastTransportRxFrames =
                snapshot.ReceivedFrames;

            _lastTransportTxFrames =
                snapshot.SentFrames;

            // CORE 11.71: feedback de rede remoto é publicado pelo
            // RemoteStreamingRuntime durante toda a sessão. A página apenas
            // exibe os contadores; navegar para outra aba não pode desligar
            // o congestion feedback do host.

            if (hostMode &&
                snapshot.Running &&
                string.IsNullOrWhiteSpace(
                    snapshot.LastError))
            {
                RemoteSessionSnapshot remote =
                    _remoteSession.GetSnapshot();

                double configuredVideoMbps =
                    _encoderTargetBitrateKbps /
                    1000.0;

                TransportStatusTextBlock.Text =
                    $"Sessão real • vídeo alvo {configuredVideoMbps:0.#} Mbps • " +
                    $"NVENC {snapshot.EncoderTargetMbps:0.0} Mbps • " +
                    $"rede alvo {snapshot.CurrentPacingMbps:0.0}/" +
                    $"{snapshot.TargetPacingMbps:0.0} Mbps • " +
                    $"TX {snapshot.ActualSendMbps:0.0} Mbps • " +
                    $"RX remoto {remote.RemoteRxMbps:0.0} Mbps • " +
                    $"late {snapshot.PacerLatenessMs:0.00} ms • " +
                    $"{snapshot.CongestionState} • " +
                    $"loss {snapshot.EstimatedPacketLossPercent:0.00}%";
            }
            else if (
                string.Equals(
                    snapshot.Mode,
                    "REMOTE_CLIENT",
                    StringComparison.Ordinal) &&
                snapshot.Running &&
                string.IsNullOrWhiteSpace(
                    snapshot.LastError))
            {
                TransportStatusTextBlock.Text =
                    $"Receptor ativo • fila RX {snapshot.PendingFrames} • " +
                    $"loss {snapshot.EstimatedPacketLossPercent:0.00}% • " +
                    $"decode drops {snapshot.DecodeDispatchDrops}";
            }

            if (!string.IsNullOrWhiteSpace(
                    snapshot.LastError))
            {
                TransportStatusTextBlock.Text =
                    snapshot.LastError;
            }
        }

        private void UpdateDecoderStats(
            double seconds)
        {
            if (_decoder is null ||
                seconds <= 0)
            {
                return;
            }

            NativeNvdecSnapshot snapshot =
                _decoder.GetSnapshot();

            long displayed =
                checked(
                    _loopbackHeadless
                        ? (long)snapshot.DisplayedFrames
                        : (long)snapshot.D3D11PresentedFrames);

            double displayedFps =
                (displayed - _lastDecoderRendered) /
                seconds;

            DecoderFpsLabelTextBlock.Text =
                _loopbackHeadless
                    ? "NVDEC display FPS • HEADLESS"
                    : "D3D11 present FPS";

            DecoderRenderedLabelTextBlock.Text =
                _loopbackHeadless
                    ? "Frames processados NVDEC"
                    : "Frames apresentados D3D11";

            DecoderFpsTextBlock.Text =
                $"{displayedFps:0.0} FPS";

            DecoderLagTextBlock.Text =
                $"{snapshot.AverageDecodeLatencyMs:0.00} ms";

            DecoderDropTextBlock.Text =
                $"{snapshot.QueueDepth}/{snapshot.MaxQueueDepth} • " +
                $"{snapshot.StaleFramesDropped}";

            DecoderSubmittedTextBlock.Text =
                snapshot.NativeFramesSubmitted.ToString();

            DecoderRenderedTextBlock.Text =
                (_loopbackHeadless
                    ? snapshot.DisplayedFrames
                    : snapshot.D3D11PresentedFrames)
                .ToString();

            DecoderPresentWaitTextBlock.Text =
                $"{snapshot.D3D11PresentWaitMs:0.00} ms";

            DecoderLatestSkipTextBlock.Text =
                snapshot.D3D11ReplacedPendingFrames.ToString();

            DecoderPresentErrorTextBlock.Text =
                snapshot.D3D11PresentFailures.ToString();

            if (_loopbackHeadless)
            {
                DecoderPresentWaitTextBlock.Text =
                    "DESATIVADO";

                DecoderLatestSkipTextBlock.Text =
                    "0";

                DecoderPresentErrorTextBlock.Text =
                    "0";

                DecoderProbeStageTextBlock.Text =
                    "HEADLESS • D3D11/Present não criado";

                DecoderProbeForegroundTextBlock.Text =
                    "N/A • isolamento local";

                DecoderProbeQueueTextBlock.Text =
                    "N/A";

                DecoderProbeStateLockTextBlock.Text =
                    "N/A";

                DecoderProbePresentCallTextBlock.Text =
                    "N/A";

                DecoderProbeFrameAgeTextBlock.Text =
                    "N/A";

                DecoderProbeActivityTextBlock.Text =
                    "N/A";

                DecoderProbeCountersTextBlock.Text =
                    $"HEADLESS • received {snapshot.NetworkFramesReceived} • " +
                    $"submit {snapshot.NativeFramesSubmitted} • " +
                    $"decoded {snapshot.DecodedPictures} • " +
                    $"display {snapshot.DisplayedFrames} • " +
                    $"queue {snapshot.QueueDepth}/{snapshot.MaxQueueDepth} • " +
                    $"stale {snapshot.StaleFramesDropped}";

                DecoderStatusTextBlock.Text =
                    $"CORE 11.8 HEADLESS • NVDEC {snapshot.AverageDecodeLatencyMs:0.00} ms • " +
                    $"D3D11/Present/DWM removidos do teste local.";

                ClientDecodePlaceholder.Text =
                    $"HEADLESS ATIVO • {displayedFps:0.0} FPS NVDEC • sem D3D11/Present/DWM";

                ClientDecodePlaceholder.Visibility =
                    Visibility.Visible;

                _lastDecoderRendered =
                    displayed;

                return;
            }

            DecoderProbeQueueTextBlock.Text =
                $"{snapshot.D3D11LastQueueToPresenterMs:0.00} / " +
                $"{snapshot.D3D11AverageQueueToPresenterMs:0.00} ms";

            DecoderProbeStateLockTextBlock.Text =
                $"{snapshot.D3D11LastStateLockWaitMs:0.00} / " +
                $"{snapshot.D3D11AverageStateLockWaitMs:0.00} ms";

            DecoderProbePresentCallTextBlock.Text =
                $"{snapshot.D3D11LastPresentCallMs:0.00} / " +
                $"{snapshot.D3D11AveragePresentCallMs:0.00} ms";

            DecoderProbeFrameAgeTextBlock.Text =
                $"{snapshot.D3D11LastFrameAgeAtPresentMs:0.00} / " +
                $"{snapshot.D3D11AverageFrameAgeAtPresentMs:0.00} ms";

            DecoderProbeActivityTextBlock.Text =
                $"{snapshot.D3D11PresenterActivityAgeMs:0.00} ms atrás";

            DecoderProbeCountersTextBlock.Text =
                $"deq {snapshot.D3D11PresenterDequeuedFrames} • " +
                $"signal {snapshot.D3D11FrameLatencySignals} • " +
                $"wait-skip {snapshot.D3D11FrameLatencyBypasses} • " +
                $"timeout {snapshot.D3D11PresentWaitTimeouts} • " +
                $"waitfail {snapshot.D3D11FrameLatencyWaitFailures} • " +
                $"attempt {snapshot.D3D11PresentAttempts} • " +
                $"ok {snapshot.D3D11PresentedFrames} • " +
                $"still {snapshot.D3D11PresentStillDrawing} • " +
                $"fail {snapshot.D3D11PresentFailures}";

            string foregroundLabel =
                snapshot.D3D11ForegroundProcessId == 0
                    ? "sem foreground"
                    : snapshot.D3D11ForegroundIsCurrentProcess
                        ? $"LEVELING • PID {snapshot.D3D11ForegroundProcessId}"
                        : $"OUTRO PROCESSO • PID {snapshot.D3D11ForegroundProcessId}";

            DecoderProbeForegroundTextBlock.Text =
                foregroundLabel;

            string presenterStage =
                snapshot.D3D11PresenterStage switch
                {
                    0 => "STOPPED",
                    1 => "QUEUE",
                    2 => "DXGI_PROBE",
                    3 => "STATE_MUTEX",
                    4 => "DRAW",
                    5 => "PRESENT",
                    _ => $"UNKNOWN({snapshot.D3D11PresenterStage})"
                };

            DecoderProbeStageTextBlock.Text =
                $"{presenterStage} • Present in-flight " +
                (snapshot.D3D11PresentInFlight
                    ? "SIM"
                    : "NÃO");

            _lastDecoderRendered =
                displayed;

            if (!snapshot.FormatReady)
            {
                DecoderStatusTextBlock.Text =
                    "NVDEC iniciado. Aguardando SPS/PPS + IDR.";
            }
            else if (
                snapshot.NativeFramesSubmitted > 120 &&
                snapshot.D3D11PresentedFrames == 0 &&
                !string.IsNullOrWhiteSpace(
                    snapshot.LastError))
            {
                DecoderStatusTextBlock.Text =
                    $"D3D11: {snapshot.LastError} • " +
                    $"attached {snapshot.D3D11Attached} • " +
                    $"submit {snapshot.D3D11SubmittedFrames}";
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.LastError) &&
                     snapshot.LastError.Contains(
                         "falh",
                         StringComparison.OrdinalIgnoreCase))
            {
                DecoderStatusTextBlock.Text =
                    snapshot.LastError;
            }
            else
            {
                DecoderStatusTextBlock.Text =
                    $"CORE 11.17f WAITABLE-GATED • attached {snapshot.D3D11Attached} • " +
                    $"submit {snapshot.D3D11SubmittedFrames} • " +
                    $"NVDEC {snapshot.AverageDecodeLatencyMs:0.00} ms • " +
                    $"GPU copy {snapshot.D3D11GpuCopyMs:0.00} ms • " +
                    $"queue→presenter {snapshot.D3D11AverageQueueToPresenterMs:0.00} ms • " +
                    $"DXGI wait {snapshot.D3D11PresentWaitMs:0.00} ms • " +
                    $"StateMutex {snapshot.D3D11AverageStateLockWaitMs:0.00} ms • " +
                    $"Present() {snapshot.D3D11AveragePresentCallMs:0.00} ms • " +
                    $"frame-age {snapshot.D3D11AverageFrameAgeAtPresentMs:0.00} ms • " +
                    $"activity-age {snapshot.D3D11PresenterActivityAgeMs:0.00} ms • " +
                    $"stage {presenterStage}/{(snapshot.D3D11PresentInFlight ? "IN" : "OUT")} • " +
                    $"foreground {foregroundLabel} • " +
                    $"wait-skip {snapshot.D3D11FrameLatencyBypasses} • " +
                    $"still {snapshot.D3D11PresentStillDrawing} • " +
                    $"latest-skip {snapshot.D3D11ReplacedPendingFrames} • " +
                    $"present-fail {snapshot.D3D11PresentFailures}";

                ClientDecodePlaceholder.Visibility =
                    snapshot.D3D11PresentedFrames > 0
                        ? Visibility.Collapsed
                        : Visibility.Visible;
            }

            MonitorRendererHealth(
                snapshot);
        }

        private void ResetRendererWatchdog()
        {
            _lastRendererSubmittedObserved = 0;
            _lastRendererPresentedObserved = 0;
            _rendererStallIntervals = 0;
            _rendererRecoveryAttempts = 0;
            _lastRendererRecoveryAt = 0;
            _rendererRecoveryInProgress = false;
        }

        private void MonitorRendererHealth(
            NativeNvdecSnapshot snapshot)
        {
            if (_loopbackHeadless)
            {
                _rendererStallIntervals = 0;
                return;
            }

            // Remote client rendering lives in RemoteSessionWindow. Its
            // SwapChainPanel must never be detached/rebound by this page's
            // diagnostics watchdog when the user navigates back here.
            RemoteSessionSnapshot session =
                _remoteSession.GetSnapshot();
            if (session.Connected &&
                session.Role == RemoteSessionRole.Client)
            {
                _rendererStallIntervals = 0;
                _lastRendererSubmittedObserved = snapshot.D3D11SubmittedFrames;
                _lastRendererPresentedObserved = snapshot.D3D11PresentedFrames;
                return;
            }

            if (_decoder is null ||
                !_decoder.Running ||
                !snapshot.D3D11Attached)
            {
                _rendererStallIntervals = 0;

                _lastRendererSubmittedObserved =
                    snapshot.D3D11SubmittedFrames;

                _lastRendererPresentedObserved =
                    snapshot.D3D11PresentedFrames;

                return;
            }

            ulong submittedDelta =
                snapshot.D3D11SubmittedFrames >=
                    _lastRendererSubmittedObserved
                    ? snapshot.D3D11SubmittedFrames -
                        _lastRendererSubmittedObserved
                    : 0;

            bool presentationAdvanced =
                snapshot.D3D11PresentedFrames >
                    _lastRendererPresentedObserved;

            // Only call it a stall when frames are actively arriving.
            if (submittedDelta >= 30 &&
                !presentationAdvanced)
            {
                _rendererStallIntervals++;
            }
            else if (presentationAdvanced)
            {
                _rendererStallIntervals = 0;
            }

            _lastRendererSubmittedObserved =
                snapshot.D3D11SubmittedFrames;

            _lastRendererPresentedObserved =
                snapshot.D3D11PresentedFrames;

            if (_rendererStallIntervals <
                    RendererStallIntervalsBeforeRecovery)
            {
                return;
            }

            TryRecoverRenderer();
        }

        private void TryRecoverRenderer()
        {
            if (_loopbackHeadless)
                return;

            if (_rendererRecoveryInProgress ||
                _decoder is null ||
                !_decoder.Running)
            {
                return;
            }

            long now =
                Environment.TickCount64;

            if (_lastRendererRecoveryAt != 0 &&
                now - _lastRendererRecoveryAt <
                    RendererRecoveryCooldownMs)
            {
                return;
            }

            if (_rendererRecoveryAttempts >=
                    MaxRendererRecoveryAttempts)
            {
                DecoderStatusTextBlock.Text =
                    "D3D11 parou de apresentar após 3 recuperações. " +
                    "Pare e inicie o transporte novamente para diagnóstico.";

                return;
            }

            _rendererRecoveryInProgress =
                true;

            _lastRendererRecoveryAt =
                now;

            try
            {
                _decoder.DetachRenderer();

                AttachDecoderRenderer();

                _rendererRecoveryAttempts++;
                _rendererStallIntervals = 0;

                NativeNvdecSnapshot snapshot =
                    _decoder.GetSnapshot();

                _lastRendererSubmittedObserved =
                    snapshot.D3D11SubmittedFrames;

                _lastRendererPresentedObserved =
                    snapshot.D3D11PresentedFrames;

                DecoderStatusTextBlock.Text =
                    $"D3D11 recuperado automaticamente " +
                    $"({_rendererRecoveryAttempts}/{MaxRendererRecoveryAttempts}).";
            }
            catch (Exception ex)
            {
                _rendererRecoveryAttempts++;

                DecoderStatusTextBlock.Text =
                    $"Falha na recuperação automática D3D11: {ex.Message}";
            }
            finally
            {
                _rendererRecoveryInProgress =
                    false;
            }
        }

        private async Task StopPreviewAsync()
        {
            CancellationTokenSource? cancel = _cancel;
            _cancel = null;

            cancel?.Cancel();

            Task? previewTask = _previewLoopTask;
            if (previewTask is not null && !previewTask.IsCompleted)
            {
                try
                {
                    await previewTask;
                }
                catch (OperationCanceledException)
                {
                    // Encerramento normal do readback de preview.
                }
                catch (Exception ex)
                {
                    EncoderStatusTextBlock.Text =
                        $"Preview finalizado com erro: {ex.Message}";
                }
            }

            cancel?.Dispose();

            NativeCaptureSession? capture = _capture;
            HostVideoPipelineController? host = HostTransmissionService.IsValueCreated
                ? HostTransmissionService.Instance : null;
            bool serviceOwnedCapture = host?.OwnsCapture(capture) == true;
            bool serviceOwnedTransport = host?.IsUsingTransport(_transport) == true;
            if (host is not null && (host.IsAutomaticCapture(capture) || host.IsAutomaticTransport(_transport)))
            {
                EncoderStatusTextBlock.Text =
                    "A transmissão principal está ativa. Use Parar na aba Transmissão.";
                return;
            }

            try { StopTransport(); }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("MANUAL_TRANSPORT_STOP_INCOMPLETE", ex);
                EncoderStatusTextBlock.Text = "Falha ao parar transporte: " + ex.Message;
                return; // Preserve the capture and transport for a later retry.
            }
            if (capture is not null)
            {
                try
                {
                    await HostTransmissionService.Instance.StopManualEncoderAsync(capture, disposeCapture: true);
                }
                catch (Exception ex)
                {
                    EncoderStatusTextBlock.Text =
                        $"Falha ao parar encoder: {ex.Message}";
                    StartupDiagnostics.RecordException("MANUAL_CAPTURE_STOP_INCOMPLETE", ex);
                    return; // Do not lose the reference or open another capture.
                }
            }
            if (ReferenceEquals(_capture, capture) || serviceOwnedCapture)
                _capture = null;
            _encoderRunning = false;

            _activeMonitor = null;
            _bitmap = null;

            PreviewImage.Source = null;
            PreviewPlaceholderPanel.Visibility = Visibility.Visible;

            StartPreviewButton.IsEnabled =
                MonitorComboBox.Items.Count > 0;
            StopPreviewButton.IsEnabled = false;
            MonitorComboBox.IsEnabled = true;
            StartEncoderButton.IsEnabled = false;
            StopEncoderButton.IsEnabled = false;
            ForceIdrButton.IsEnabled = false;
            StartTransportButton.IsEnabled = false;
            StopTransportButton.IsEnabled = false;

            EncoderStatusTextBlock.Text = "Encoder parado.";
            TransportStatusTextBlock.Text = "Transporte parado.";

            ResetEncoderStatsText();
            ResetTransportStatsText();
            ResetDecoderStatsText();

            _stats.Reset();
            UpdateRemoteModeUi();
        }

        private void AdvancedPipelineButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            bool show = AdvancedPipelinePanel.Visibility != Visibility.Visible;
            AdvancedPipelinePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            AdvancedPipelineButton.Content = show
                ? "Ocultar pipeline avançado"
                : "Mostrar pipeline avançado";
        }

        private void StartEncoderButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            StartEncoderFromCurrentSettings();
        }

        private bool EnsureManualVideoMutationAllowed()
        {
            if (HostTransmissionService.Instance.CanUseManualPipeline(_capture))
                return true;
            EncoderStatusTextBlock.Text =
                "Pare a transmissão principal antes de alterar o pipeline manual.";
            return false;
        }

        private bool ManualPipelineAvailable() =>
            EnsureManualVideoMutationAllowed();

        private bool StartEncoderFromCurrentSettings()
        {
            if (!ManualPipelineAvailable()) return false;
            if (_capture is null)
                return false;

            if (!TryReadEncoderUiSettings(
                    out uint fps,
                    out uint bitrate,
                    out uint preset,
                    out bool ultraLowLatency,
                    out uint labInputMode))
            {
                EncoderStatusTextBlock.Text =
                    "Configuração do encoder inválida.";
                return false;
            }

            _encoderTargetFps =
                fps;

            _encoderTargetBitrateKbps =
                bitrate;

            EncoderStartResult result =
                HostTransmissionService.Instance.StartManualEncoder(_capture,
                    new EncoderConfig
                    {
                        Fps = fps,
                        BitrateKbps = bitrate,
                        Preset = preset,
                        UltraLowLatency =
                            ultraLowLatency,
                        LabInputMode =
                            labInputMode
                    });

            EncoderStatusTextBlock.Text =
                result.Message;

            if (!result.Success)
            {
                EncoderDiagnosticsTextBlock.Text =
                    "ENCODER DIAG  •  inicialização falhou; veja o estágio/status acima.";
                return false;
            }

            _encoderRunning = true;

            _streamRuntime.UpdateCapture(
                _capture,
                encoderRunning: true);

            StartEncoderButton.IsEnabled =
                false;

            StopEncoderButton.IsEnabled =
                true;

            ForceIdrButton.IsEnabled =
                true;

            StopTransportButton.IsEnabled =
                false;

            UpdateRemoteModeUi();

            _lastEncoded = 0;
            _lastSubmitted = 0;
            _lastBytes = 0;
            _lastEncoderSampleTimestamp = 0;
            ResetMouseCadenceDiagnosticsSample();
            ResetHostStabilityProbe();
            ResetRingFenceIsolationProbe();

            ResetEncoderStatsText();
            return true;
        }

        private void ConfigureRemoteStreamingProfileUi()
        {
            // Keep only supported stream rates.
            for (int i = EncoderFpsComboBox.Items.Count - 1; i >= 0; --i)
            {
                if (EncoderFpsComboBox.Items[i] is not ComboBoxItem item ||
                    !uint.TryParse(item.Content?.ToString(), out uint value) ||
                    (value is not (30u or 60u or 80u or 90u or 120u)))
                {
                    EncoderFpsComboBox.Items.RemoveAt(i);
                }
            }

            if (!SelectComboValue(EncoderFpsComboBox, "60", useTag: false) &&
                EncoderFpsComboBox.Items.Count > 0)
            {
                EncoderFpsComboBox.SelectedIndex = 0;
            }
        }

        private bool TryReadEncoderUiSettings(
            out uint fps,
            out uint bitrate,
            out uint preset,
            out bool ultraLowLatency,
            out uint labInputMode)
        {
            fps = 60;
            bitrate = 20000;
            preset = 0;
            ultraLowLatency = true;
            labInputMode = 10;

            if (EncoderFpsComboBox.SelectedItem
                    is not ComboBoxItem fpsItem ||
                !uint.TryParse(
                    fpsItem.Content?.ToString(),
                    out fps))
            {
                return false;
            }

            if (fps is not (30u or 60u or 80u or 90u or 120u))
            {
                fps = 60u;
            }

            if (EncoderBitrateComboBox.SelectedItem
                    is not ComboBoxItem bitrateItem ||
                !uint.TryParse(
                    bitrateItem.Tag?.ToString(),
                    out bitrate))
            {
                return false;
            }

            if (EncoderPresetComboBox.SelectedItem
                    is not ComboBoxItem presetItem ||
                !uint.TryParse(
                    presetItem.Tag?.ToString(),
                    out preset))
            {
                return false;
            }

            if (EncoderTuningComboBox.SelectedItem
                    is not ComboBoxItem tuningItem)
            {
                return false;
            }

            ultraLowLatency =
                tuningItem.Tag?.ToString() == "1";

            if (EncoderLabModeComboBox.SelectedItem
                    is not ComboBoxItem labItem ||
                !uint.TryParse(
                    labItem.Tag?.ToString(),
                    out labInputMode) ||
                labInputMode < 1 ||
                labInputMode > 10)
            {
                return false;
            }

            return true;
        }

        private void LoadEncoderUiSettings()
        {
            _loadingEncoderUiSettings = true;

            try
            {
                // Core 08.8 stores semantic values instead of relying only on
                // ComboBox indexes. Reordering XAML items no longer changes a
                // previously saved 60 FPS into another setting.
                bool fpsLoaded =
                    TrySelectSavedComboValue(
                        EncoderFpsComboBox,
                        "Multiverse.Encoder.Fps",
                        useTag: false);

                bool bitrateLoaded =
                    TrySelectSavedComboValue(
                        EncoderBitrateComboBox,
                        "Multiverse.Encoder.BitrateKbps",
                        useTag: true);

                bool presetLoaded =
                    TrySelectSavedComboValue(
                        EncoderPresetComboBox,
                        "Multiverse.Encoder.Preset",
                        useTag: true);

                bool tuningLoaded =
                    TrySelectSavedComboValue(
                        EncoderTuningComboBox,
                        "Multiverse.Encoder.Tuning",
                        useTag: true);

                // Migration from the old index-based profile must map the
                // OLD item order, otherwise adding 30/45/90 FPS or new bitrate
                // choices would silently change a saved profile.
                if (!fpsLoaded)
                {
                    // Old profiles could persist 90/120/144/165/240. Those
                    // rates are no longer valid remote-stream settings; migrate
                    // them deterministically to the recommended 60 FPS profile.
                    SelectComboValue(
                        EncoderFpsComboBox,
                        "60",
                        useTag: false);
                }

                if (!bitrateLoaded)
                {
                    int legacyBitrateIndex =
                        ReadSavedComboIndex(
                            "Multiverse.Encoder.BitrateIndex",
                            1,
                            6);

                    string[] legacyBitrate =
                    {
                        "12000", "20000", "30000", "40000", "50000", "65000"
                    };

                    SelectComboValue(
                        EncoderBitrateComboBox,
                        legacyBitrate[Math.Clamp(legacyBitrateIndex, 0, legacyBitrate.Length - 1)],
                        useTag: true);
                }

                if (!presetLoaded)
                {
                    EncoderPresetComboBox.SelectedIndex =
                        ReadSavedComboIndex(
                            "Multiverse.Encoder.PresetIndex",
                            0,
                            EncoderPresetComboBox.Items.Count);
                }

                if (!tuningLoaded)
                {
                    EncoderTuningComboBox.SelectedIndex =
                        ReadSavedComboIndex(
                            "Multiverse.Encoder.TuningIndex",
                            0,
                            EncoderTuningComboBox.Items.Count);
                }

                var pipelineValues = _settings;
                bool core1116Migrated =
                    pipelineValues.TryGetRaw(
                        "Multiverse.Core11.SameDevicePipelineMigrated",
                        out object? core1116MigratedRaw) &&
                    bool.TryParse(core1116MigratedRaw?.ToString(), out bool core1116MigratedValue) &&
                    core1116MigratedValue;

                if (!core1116Migrated)
                {
                    // CORE 11.16 changes the recommended host topology. Migrate
                    // once so existing installs actually exercise same-device WGC/NVENC.
                    SelectComboValue(EncoderLabModeComboBox, "10", useTag: true);
                    pipelineValues.Set(MultiverseSettingsService.StreamPipelineModeKey, "10");
                    pipelineValues.Set("Multiverse.Core11.SameDevicePipelineMigrated", true);
                }
                else
                {
                    TrySelectSavedComboValue(
                        EncoderLabModeComboBox,
                        MultiverseSettingsService.StreamPipelineModeKey,
                        useTag: true);
                }
            }
            catch
            {
                // Defaults do XAML continuam válidos.
            }
            finally
            {
                _loadingEncoderUiSettings = false;
            }

            if (TryReadEncoderUiSettings(
                    out uint fps,
                    out uint bitrate,
                    out _,
                    out _,
                    out _))
            {
                _encoderTargetFps = fps;
                _encoderTargetBitrateKbps = bitrate;
            }
        }

        private static bool SelectComboValue(
            ComboBox comboBox,
            string expected,
            bool useTag)
        {
            for (int index = 0; index < comboBox.Items.Count; index++)
            {
                if (comboBox.Items[index] is not ComboBoxItem item)
                    continue;

                string actual = useTag
                    ? item.Tag?.ToString() ?? ""
                    : item.Content?.ToString() ?? "";

                if (string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    comboBox.SelectedIndex = index;
                    return true;
                }
            }

            return false;
        }

        private static bool TrySelectSavedComboValue(
            ComboBox comboBox,
            string key,
            bool useTag)
        {
            try
            {
                var values = MultiverseSettingsService.Instance;

                if (!values.TryGetRaw(
                        key,
                        out object? raw) ||
                    raw is null)
                {
                    return false;
                }

                string expected =
                    raw.ToString() ?? "";

                for (int index = 0;
                     index < comboBox.Items.Count;
                     index++)
                {
                    if (comboBox.Items[index]
                            is not ComboBoxItem item)
                    {
                        continue;
                    }

                    string actual =
                        useTag
                            ? item.Tag?.ToString() ?? ""
                            : item.Content?.ToString() ?? "";

                    if (string.Equals(
                            actual,
                            expected,
                            StringComparison.Ordinal))
                    {
                        comboBox.SelectedIndex =
                            index;

                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static int ReadSavedComboIndex(
            string key,
            int fallback,
            int itemCount)
        {
            try
            {
                var values = MultiverseSettingsService.Instance;

                if (values.TryGetRaw(
                        key,
                        out object? raw) &&
                    int.TryParse(
                        raw?.ToString(),
                        out int saved) &&
                    saved >= 0 &&
                    saved < itemCount)
                {
                    return saved;
                }
            }
            catch
            {
            }

            return fallback;
        }

        private void SaveEncoderUiSettings()
        {
            try
            {
                if (TryReadEncoderUiSettings(
                        out uint semanticFps,
                        out uint semanticBitrate,
                        out uint semanticPreset,
                        out bool semanticUltraLowLatency,
                        out _))
                {
                    _settings.SaveEncoderProfile(
                        semanticFps,
                        semanticBitrate,
                        semanticPreset,
                        semanticUltraLowLatency);
                }

                var values = MultiverseSettingsService.Instance;

                // Semantic values are the authoritative settings.
                if (EncoderFpsComboBox.SelectedItem
                        is ComboBoxItem fpsItem)
                {
                    values.Set("Multiverse.Encoder.Fps", fpsItem.Content?.ToString() ??
                        "60");
                }

                if (EncoderBitrateComboBox.SelectedItem
                        is ComboBoxItem bitrateItem)
                {
                    values.Set("Multiverse.Encoder.BitrateKbps", bitrateItem.Tag?.ToString() ??
                        "20000");
                }

                if (EncoderPresetComboBox.SelectedItem
                        is ComboBoxItem presetItem)
                {
                    values.Set("Multiverse.Encoder.Preset", presetItem.Tag?.ToString() ??
                        "0");
                }

                if (EncoderTuningComboBox.SelectedItem
                        is ComboBoxItem tuningItem)
                {
                    values.Set("Multiverse.Encoder.Tuning", tuningItem.Tag?.ToString() ??
                        "1");
                }

                if (EncoderLabModeComboBox.SelectedItem
                        is ComboBoxItem labItem)
                {
                    values.Set(MultiverseSettingsService.StreamPipelineModeKey, labItem.Tag?.ToString() ??
                        "10");
                }

                // Keep indexes only for backwards compatibility with 08.6.
                values.Set("Multiverse.Encoder.FpsIndex", EncoderFpsComboBox.SelectedIndex);

                values.Set("Multiverse.Encoder.BitrateIndex", EncoderBitrateComboBox.SelectedIndex);

                values.Set("Multiverse.Encoder.PresetIndex", EncoderPresetComboBox.SelectedIndex);

                values.Set("Multiverse.Encoder.TuningIndex", EncoderTuningComboBox.SelectedIndex);
            }
            catch (Exception ex)
            {
                EncoderStatusTextBlock.Text =
                    $"Configuração não pôde ser salva: {ex.Message}";
            }
        }

        private void EncoderSettings_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_loadingEncoderUiSettings)
                return;

            SaveEncoderUiSettings();

            if (TryReadEncoderUiSettings(
                    out uint fps,
                    out uint bitrate,
                    out _,
                    out _,
                    out _))
            {
                _encoderTargetFps = fps;
                _encoderTargetBitrateKbps = bitrate;
            }

            if (!_encoderRunning ||
                _capture is null)
            {
                return;
            }

            // Alterar uma opção enquanto transmite passa a ter efeito real.
            // O debounce evita reiniciar o NVENC várias vezes enquanto o
            // usuário percorre rapidamente a ComboBox.
            _encoderSettingsApplyTimer.Stop();
            _encoderSettingsApplyTimer.Start();
        }

        private async void EncoderSettingsApplyTimer_Tick(
            DispatcherQueueTimer sender,
            object args)
        {
            sender.Stop();
            await ApplyEncoderSettingsFromUiAsync();
        }

        private async Task ApplyEncoderSettingsFromUiAsync()
        {
            if (!ManualPipelineAvailable()) return;
            if (_encoderSettingsApplyInProgress ||
                !_encoderRunning ||
                _capture is null)
            {
                return;
            }

            _encoderSettingsApplyInProgress = true;

            try
            {
                string previousTransportMode =
                    _transport?.GetSnapshot().Mode ?? "";

                bool resumeRemoteHost =
                    string.Equals(
                        previousTransportMode,
                        "REMOTE_HOST",
                        StringComparison.Ordinal);

                bool resumeLoopback =
                    string.Equals(
                        previousTransportMode,
                        "LOOPBACK",
                        StringComparison.Ordinal);

                StopTransport();

                await HostTransmissionService.Instance.StopManualEncoderAsync(_capture);
                _encoderRunning = false;

                if (!StartEncoderFromCurrentSettings())
                    return;

                RemoteSessionSnapshot session =
                    _remoteSession.GetSnapshot();

                if (resumeRemoteHost &&
                    session.Connected &&
                    session.Role ==
                        RemoteSessionRole.Host)
                {
                    await StartRemoteHostTransportAsync();
                }
                else if (resumeLoopback)
                {
                    StartLoopbackTransport();
                }

                EncoderStatusTextBlock.Text +=
                    " • configuração aplicada.";
            }
            catch (Exception ex)
            {
                EncoderStatusTextBlock.Text =
                    $"Falha ao aplicar configuração: {ex.Message}";
            }
            finally
            {
                _encoderSettingsApplyInProgress = false;
            }
        }

        private async void StopEncoderButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_stopDiagnosticInProgress)
                return;

            if (!EnsureManualVideoMutationAllowed())
                return;

            NativeCaptureSession? captureSession = _capture;
            if (captureSession is null)
                return;

            _stopDiagnosticInProgress = true;
            // Preserve the baseline stop semantics immediately; the diagnostic
            // flag keeps the stats loop alive even though encoderRunning is false.
            _encoderRunning = false;

            StartEncoderButton.IsEnabled = false;
            StopEncoderButton.IsEnabled = false;
            ForceIdrButton.IsEnabled = false;
            StartTransportButton.IsEnabled = false;
            StopTransportButton.IsEnabled = false;

            SetManagedStopStage("TRANSPORT_STOP");
            EncoderDiagnosticsTextBlock.Text =
                $"STOP DIAG  •  managed TRANSPORT_STOP / 0 ms  •  aguardando StopTransport()";
            await Task.Yield();

            try
            {
                StopTransport();

                SetManagedStopStage("NATIVE_ENCODER_STOP");
                EncoderDiagnosticsTextBlock.Text =
                    $"STOP DIAG  •  managed NATIVE_ENCODER_STOP / 0 ms  •  native call iniciando...";
                await Task.Yield();

                // Diagnostic harness only: native ordering is unchanged, but the
                // blocking call runs off the UI thread so liveness/stop stages
                // remain visible if a join/driver call never returns.
                await HostTransmissionService.Instance.StopManualEncoderAsync(captureSession);

                SetManagedStopStage("COMPLETE");
                _encoderRunning = false;
                _streamRuntime.UpdateCapture(
                    _capture,
                    encoderRunning: false);

                EncoderStatusTextBlock.Text =
                    "Encoder parado.";

                ResetEncoderStatsText();
            }
            catch (Exception ex)
            {
                SetManagedStopStage("ERROR");
                EncoderStatusTextBlock.Text =
                    $"STOP DIAG falhou: {ex.Message}";
            }
            finally
            {
                _stopDiagnosticInProgress = false;

                StartEncoderButton.IsEnabled =
                    _capture is not null;

                StopEncoderButton.IsEnabled =
                    false;

                ForceIdrButton.IsEnabled =
                    false;

                StartTransportButton.IsEnabled =
                    false;

                StopTransportButton.IsEnabled =
                    false;
            }
        }

        private async void StartTransportButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            RemoteSessionSnapshot session =
                _remoteSession.GetSnapshot();

            if (
                session.Connected &&
                session.Role ==
                    RemoteSessionRole.Client)
            {
                await StartRemoteClientTransportAsync();
                return;
            }

            if (_capture is null ||
                !_encoderRunning)
            {
                return;
            }

            if (!ManualPipelineAvailable()) return;
            StopTransport();

            try
            {
                if (
                    session.Connected &&
                    session.Role ==
                        RemoteSessionRole.Host)
                {
                    await StartRemoteHostTransportAsync();
                }
                else
                {
                    StartLoopbackTransport();
                }
            }
            catch (Exception ex)
            {
                StopTransport();

                TransportStatusTextBlock.Text =
                    $"Falha ao iniciar transporte: {ex.Message}";

                UpdateRemoteModeUi();
            }
        }

        private void StartLoopbackTransport()
        {
            if (!ManualPipelineAvailable())
                throw new InvalidOperationException("A transmissão principal está usando o pipeline.");
            // Decoder creation negotiates NVIDIA or Windows H.264.

            if (_capture is null)
                return;

            EncoderStats? loopbackEncoder =
                _capture.GetEncoderStats();

            uint loopbackWidth =
                loopbackEncoder?.Width > 0
                    ? loopbackEncoder.Width
                    : _capture.SourceWidth;

            uint loopbackHeight =
                loopbackEncoder?.Height > 0
                    ? loopbackEncoder.Height
                    : _capture.SourceHeight;

            _loopbackHeadless =
                LoopbackHeadlessToggleSwitch.IsOn;

            _decoder =
                new NativeNvdecPlayer(
                    loopbackWidth,
                    loopbackHeight,
                    () => HostTransmissionService.Instance.TryRequestIdr(_capture),
                    headlessOutput: _loopbackHeadless);

            _decoder.Start();

            if (!_loopbackHeadless)
            {
                AttachDecoderRenderer();
            }
            else
            {
                ClientDecodePlaceholder.Text =
                    "CORE 11.8 HEADLESS • D3D11/Present/DWM desativados neste loopback";

                ClientDecodePlaceholder.Visibility =
                    Visibility.Visible;
            }

            _transport = HostTransmissionService.Instance.StartManualLoopback(_capture, _decoder.PushFrame);

            HostTransmissionService.Instance.TryRequestIdr(_capture);

            ResetTransportCounters();
            ResetRendererWatchdog();

            StartTransportButton.IsEnabled =
                false;

            StopTransportButton.IsEnabled =
                true;

            LoopbackHeadlessToggleSwitch.IsEnabled =
                false;

            TransportStatusTextBlock.Text =
                _loopbackHeadless
                    ? "Loopback HEADLESS ativo: H.264 → UDP → reassembly → NVDEC, sem D3D11/Present/DWM."
                    : "Loopback VISUAL ativo: H.264 → UDP → reassembly → NVDEC → D3D11.";

            DecoderStatusTextBlock.Text =
                _loopbackHeadless
                    ? "CORE 11.8 HEADLESS • medindo decode sem map/preview/presenter local."
                    : "CORE 11.8 VISUAL • NVDEC → presenter D3D11 ativo.";
        }

        private async Task StartRemoteHostTransportAsync()
        {
            if (!ManualPipelineAvailable())
                throw new InvalidOperationException("A transmissão principal está usando o pipeline.");
            if (_capture is null ||
                !_encoderRunning)
            {
                return;
            }

            IPEndPoint? remote =
                _remoteSession
                    .GetRemoteVideoEndpoint();

            if (remote is null)
            {
                throw new InvalidOperationException(
                    "A sessão não possui um endpoint UDP remoto.");
            }

            _transport = await HostTransmissionService.Instance.StartManualRemoteTransportAsync(_capture);

            ResetTransportCounters();

            // Este bootstrap precisa acontecer DEPOIS do sender ativar
            // o packet consumer. Mesmo que o cliente tenha pedido IDR nos
            // 260 ms anteriores, garantimos uma keyframe dentro do TX ativo.
            RequestLocalIdrDebounced(
                force: true);

            StartTransportButton.IsEnabled =
                false;

            StopTransportButton.IsEnabled =
                true;

            RemoteSessionSnapshot session =
                _remoteSession.GetSnapshot();

            TransportStatusTextBlock.Text =
                $"Transmitindo H.264 para {session.RemoteName} • " +
                $"{remote.Address}:{remote.Port}.";

            DecoderStatusTextBlock.Text =
                "HOST ativo • decoder local não é necessário na transmissão remota.";
        }

        private async Task StartRemoteClientTransportAsync()
        {
            RemoteSessionSnapshot session =
                _remoteSession.GetSnapshot();

            if (!session.Connected ||
                session.Role != RemoteSessionRole.Client)
            {
                UpdateRemoteModeUi();
                return;
            }

            RemoteStreamConfig? config =
                _remoteSession.GetStreamConfig();

            if (config is null)
            {
                TransportStatusTextBlock.Text =
                    "Conectado ao host. Aguardando configuração do stream.";

                UpdateRemoteModeUi();
                return;
            }

            try
            {
                // Decoder creation negotiates NVIDIA or Windows H.264.

                bool running =
                    await _streamRuntime.EnsureClientStreamingAsync();

                if (!running)
                {
                    TransportStatusTextBlock.Text =
                        "Cliente remoto ainda não recebeu uma configuração válida.";
                    return;
                }

                RestoreRuntimeState();

                _activeClientStreamWidth =
                    config.Width;
                _activeClientStreamHeight =
                    config.Height;

                ResetTransportCounters();
                ResetRendererWatchdog();

                _stats.Restart();
                _remoteClientStatsTimer.Start();

                // Cursor/video rendering now belongs to the independent
                // session window. The Devices page remains a diagnostics UI.
                _remoteCursorRenderTimer.Stop();
                RemoteCursorVisual.Visibility =
                    Visibility.Collapsed;

                StartTransportButton.IsEnabled =
                    false;

                StopTransportButton.IsEnabled =
                    true;

                TransportStatusTextBlock.Text =
                    $"Receptor remoto ativo • UDP {_remoteSession.LocalVideoPort} • " +
                    $"{config}.";

                DecoderStatusTextBlock.Text =
                    "CORE 11 cliente ativo • NVDEC/D3D11 em janela de sessão independente.";

                _streamRuntime.ShowSessionWindow();
            }
            catch (Exception ex)
            {
                _streamRuntime.StopVideoOnly();
                RestoreRuntimeState();

                TransportStatusTextBlock.Text =
                    $"Falha no cliente remoto: {ex.Message}";

                UpdateRemoteModeUi();
            }
        }

        private void ResetTransportCounters()
        {
            _lastDecoderRendered = 0;

            _lastTransportTxBytes = 0;
            _lastTransportRxBytes = 0;
            _lastTransportTxPackets = 0;
            _lastTransportRxPackets = 0;
            _lastTransportRxFrames = 0;
            _lastTransportTxFrames = 0;

            _lastLocalIdrServedAt = 0;

            ResetTransportStatsText();
        }

        private void StopTransportButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            StopTransport();
            if (_transport is null)
                TransportStatusTextBlock.Text = "Transporte parado.";
        }

        private void StopTransport()
        {
            if (HostTransmissionService.IsValueCreated &&
                HostTransmissionService.Instance.IsAutomaticTransport(_transport))
            {
                TransportStatusTextBlock.Text =
                    "Use Parar na Transmissão para encerrar o sender principal.";
                return;
            }
            _remoteClientStatsTimer.Stop();
            _remoteCursorRenderTimer.Stop();

            if (RemoteCursorVisual is not null)
            {
                RemoteCursorVisual.Visibility =
                    Visibility.Collapsed;
            }

            if (_transport is not null && HostTransmissionService.Instance.IsUsingTransport(_transport))
            {
                HostTransmissionService.Instance.StopManualTransport(_transport);
                _transport = null;
            }
            bool remoteTransport =
                _transport is not null &&
                (_transport.GetSnapshot().Mode == "REMOTE_HOST" ||
                 _transport.GetSnapshot().Mode == "REMOTE_CLIENT");

            if (remoteTransport)
            {
                _streamRuntime.StopVideoOnly(
                    disposeDecoder: true);
                _transport = null;
                _decoder = null;
            }
            else
            {
                if (_transport is not null &&
                    _decoder is not null)
                {
                    _transport.FrameReceived -=
                        _decoder.PushFrame;
                }

                _transport?.Stop();
                _transport?.Dispose();
                _transport = null;

                _decoder?.DetachRenderer();
                _decoder?.Stop();
                _decoder?.Dispose();
                _decoder = null;
            }

            _activeClientStreamWidth = 0;
            _activeClientStreamHeight = 0;
            _lastPublishedStreamWidth = 0;
            _lastPublishedStreamHeight = 0;

            if (ClientDecodePlaceholder is not null)
            {
                ClientDecodePlaceholder.Visibility =
                    Visibility.Visible;
            }

            _lastDecoderRendered = 0;
            _loopbackHeadless = false;
            ResetRendererWatchdog();

            if (DecoderFpsLabelTextBlock is not null)
                DecoderFpsLabelTextBlock.Text = "D3D11 present FPS";

            if (DecoderRenderedLabelTextBlock is not null)
                DecoderRenderedLabelTextBlock.Text = "Frames apresentados D3D11";

            if (ClientDecodePlaceholder is not null)
                ClientDecodePlaceholder.Text = "Aguardando D3D11...";

            if (DecoderStatusTextBlock is not null)
                DecoderStatusTextBlock.Text = "Decoder parado.";

            ResetDecoderStatsText();

            if (StartTransportButton is not null)
            {
                StartTransportButton.IsEnabled =
                    _encoderRunning &&
                    _capture is not null;
            }

            if (StopTransportButton is not null)
                StopTransportButton.IsEnabled = false;

            UpdateRemoteModeUi();
        }

        private void RemoteCursorRenderTimer_Tick(
            DispatcherQueueTimer sender,
            object args)
        {
            RemoteSessionSnapshot session =
                _remoteSession.GetSnapshot();

            if (!session.Connected ||
                session.Role != RemoteSessionRole.Client ||
                _transport is null ||
                !_transport.Running)
            {
                RemoteCursorVisual.Visibility =
                    Visibility.Collapsed;
                sender.Stop();
                return;
            }

            if (!session.RemoteCursorVisible ||
                ClientDecodeSwapChainPanel.ActualWidth < 2 ||
                ClientDecodeSwapChainPanel.ActualHeight < 2)
            {
                RemoteCursorVisual.Visibility =
                    Visibility.Collapsed;
                return;
            }

            double x =
                Math.Clamp(
                    session.RemoteCursorX,
                    0.0,
                    1.0) *
                ClientDecodeSwapChainPanel.ActualWidth;

            double y =
                Math.Clamp(
                    session.RemoteCursorY,
                    0.0,
                    1.0) *
                ClientDecodeSwapChainPanel.ActualHeight;

            Canvas.SetLeft(
                RemoteCursorVisual,
                x);

            Canvas.SetTop(
                RemoteCursorVisual,
                y);

            RemoteCursorVisual.Visibility =
                Visibility.Visible;
        }

        private void RemoteClientStatsTimer_Tick(
            DispatcherQueueTimer sender,
            object args)
        {
            if (_transport is null ||
                !_transport.Running)
            {
                // Runtime watchdog owns bounded recovery/rebind. Never turn a
                // single UDP receiver failure into a control-session drop here.
                RestoreRuntimeState();
                sender.Stop();
                return;
            }

            UpdateStats();
        }

        private void RemoteSession_SessionChanged(
            object? sender,
            EventArgs e)
        {
            DispatcherQueue.TryEnqueue(
                () =>
                {
                    UpdateRemoteModeUi();

                    RemoteSessionSnapshot snapshot =
                        _remoteSession.GetSnapshot();

                    if (!snapshot.Connected &&
                        _transport is not null &&
                        !string.Equals(
                            _transport.GetSnapshot().Mode,
                            "LOOPBACK",
                            StringComparison.Ordinal))
                    {
                        _streamRuntime.StopVideoOnly();
                        RestoreRuntimeState();
                        _transport = null;
                        _decoder = null;

                        TransportStatusTextBlock.Text =
                            "Sessão remota encerrada.";
                    }
                });
        }

        private void RemoteSession_StreamConfigChanged(
            object? sender,
            EventArgs e)
        {
            DispatcherQueue.TryEnqueue(
                () =>
                {
                    UpdateRemoteModeUi();

                    if (
                        _remoteSession.GetSnapshot().Role ==
                            RemoteSessionRole.Client)
                    {
                        _ =
                            StartRemoteClientTransportAsync();
                    }
                });
        }

        private void RemoteSession_RemoteIdrRequested()
        {
            // O control plane roda fora da UI no Core 08.8. Mantemos a
            // chamada ao capture no dispatcher para preservar o mesmo
            // contexto que os demais comandos da DevicesPage.
            DispatcherQueue.TryEnqueue(
                () =>
                {
                    if (
                        _transport?.Running != true ||
                        !string.Equals(
                            _transport.GetSnapshot().Mode,
                            "REMOTE_HOST",
                            StringComparison.Ordinal))
                    {
                        // Durante o bootstrap o host ainda nao ativou o
                        // packet consumer. O StartRemoteHostTransportAsync
                        // envia uma IDR garantida assim que o TX estiver vivo.
                        return;
                    }

                    RequestLocalIdrDebounced(
                        force: false);
                });
        }

        private void RequestLocalIdrDebounced(
            bool force)
        {
            NativeCaptureSession? capture =
                _capture;

            if (capture is null)
                return;

            long now =
                Environment.TickCount64;

            if (
                !force &&
                _lastLocalIdrServedAt != 0 &&
                now - _lastLocalIdrServedAt <
                    LocalIdrCooldownMs)
            {
                return;
            }

            _lastLocalIdrServedAt =
                now;

            HostTransmissionService.Instance.TryRequestIdr(capture);
        }

        private void RefreshAudioProcessList()
        {
            if (AudioProcessComboBox is null)
                return;

            RemoteAudioSnapshot snapshot = _remoteAudio.GetSnapshot();
            var items = _remoteAudio.EnumerateAudioProcesses();

            _refreshingAudioProcessList = true;
            try
            {
                AudioProcessComboBox.ItemsSource = items;
                AudioProcessComboBox.SelectedItem = null;

                if (snapshot.SelectedProcessId is int selectedPid)
                {
                    foreach (AudioProcessOption item in items)
                    {
                        if (item.ProcessId == selectedPid)
                        {
                            AudioProcessComboBox.SelectedItem = item;
                            break;
                        }
                    }
                }
            }
            finally
            {
                _refreshingAudioProcessList = false;
            }
        }

        private void RefreshAudioProcessesButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            RefreshAudioProcessList();
            UpdateAudioUi();
        }

        private void AudioProcessComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_refreshingAudioProcessList)
                return;

            AudioProcessOption? selected =
                AudioProcessComboBox.SelectedItem as AudioProcessOption;

            _remoteAudio.SelectProcess(
                selected?.ProcessId,
                selected?.DisplayName);
            UpdateAudioUi();
        }

        private void RefreshAudioOutputDeviceList()
        {
            if (AudioOutputDeviceComboBox is null)
                return;

            RemoteAudioSnapshot snapshot = _remoteAudio.GetSnapshot();
            var items = _remoteAudio.EnumerateOutputDevices();

            _refreshingAudioOutputDeviceList = true;
            try
            {
                AudioOutputDeviceComboBox.ItemsSource = items;
                AudioOutputDeviceComboBox.SelectedItem = null;

                foreach (AudioOutputDeviceOption item in items)
                {
                    if (string.Equals(
                        item.DeviceId,
                        snapshot.SelectedOutputDeviceId,
                        StringComparison.Ordinal))
                    {
                        AudioOutputDeviceComboBox.SelectedItem = item;
                        break;
                    }
                }

                if (AudioOutputDeviceComboBox.SelectedItem is null && items.Count > 0)
                    AudioOutputDeviceComboBox.SelectedItem = items[0];
            }
            finally
            {
                _refreshingAudioOutputDeviceList = false;
            }
        }

        private void RefreshAudioOutputDevicesButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            RefreshAudioOutputDeviceList();
            UpdateAudioUi();
        }

        private void AudioOutputDeviceComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_refreshingAudioOutputDeviceList)
                return;

            AudioOutputDeviceOption? selected =
                AudioOutputDeviceComboBox.SelectedItem as AudioOutputDeviceOption;
            if (selected is null)
                return;

            _remoteAudio.SelectClientOutputDevice(
                selected.DeviceId,
                selected.DisplayName);
            UpdateAudioUi();
        }

        private void RemoteAudioVolumeSlider_ValueChanged(
            object sender,
            Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loadingAudioClientUi)
                return;

            int volumePercent = (int)Math.Round(e.NewValue);
            _remoteAudio.SetClientVolumePercent(volumePercent);
            if (RemoteAudioVolumeValueTextBlock is not null)
                RemoteAudioVolumeValueTextBlock.Text = $"{volumePercent}%";
        }

        private async void ToggleRemoteAudioButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            RemoteAudioSnapshot snapshot = _remoteAudio.GetSnapshot();
            if (snapshot.HostCaptureRunning)
            {
                _remoteAudio.StopHostCaptureOnly();
                UpdateAudioUi();
                return;
            }

            ToggleRemoteAudioButton.IsEnabled = false;
            try
            {
                await _remoteAudio.StartSelectedAsync();
            }
            finally
            {
                UpdateAudioUi();
            }
        }

        private void RemoteAudio_AudioChanged(
            object? sender,
            EventArgs e)
        {
            DispatcherQueue.TryEnqueue(UpdateAudioUi);
        }

        private void UpdateAudioUi()
        {
            if (RemoteAudioStatusTextBlock is null ||
                ToggleRemoteAudioButton is null ||
                AudioProcessComboBox is null ||
                RefreshAudioProcessesButton is null ||
                AudioOutputDeviceComboBox is null ||
                RefreshAudioOutputDevicesButton is null ||
                RemoteAudioVolumeSlider is null ||
                RemoteAudioVolumeValueTextBlock is null)
            {
                return;
            }

            RemoteSessionSnapshot session = _remoteSession.GetSnapshot();
            RemoteAudioSnapshot audio = _remoteAudio.GetSnapshot();

            bool client =
                session.Connected &&
                session.Role == RemoteSessionRole.Client;
            bool hostConnected =
                session.Connected &&
                session.Role == RemoteSessionRole.Host;

            AudioProcessComboBox.IsEnabled = !client && !audio.HostCaptureRunning;
            RefreshAudioProcessesButton.IsEnabled = !client && !audio.HostCaptureRunning;
            AudioOutputDeviceComboBox.IsEnabled = client;
            RefreshAudioOutputDevicesButton.IsEnabled = client;
            RemoteAudioVolumeSlider.IsEnabled = client;

            _loadingAudioClientUi = true;
            try
            {
                if (Math.Abs(RemoteAudioVolumeSlider.Value - audio.ClientVolumePercent) >= 0.5)
                    RemoteAudioVolumeSlider.Value = audio.ClientVolumePercent;
                RemoteAudioVolumeValueTextBlock.Text = $"{audio.ClientVolumePercent}%";
            }
            finally
            {
                _loadingAudioClientUi = false;
            }

            ToggleRemoteAudioButton.IsEnabled =
                hostConnected &&
                (audio.HostCaptureRunning || audio.SelectedProcessId is not null);

            ToggleRemoteAudioButton.Content =
                audio.HostCaptureRunning
                    ? "Parar áudio remoto"
                    : "Transmitir áudio selecionado";

            if (client)
            {
                RemoteAudioStatusTextBlock.Text =
                    audio.ClientPlaybackRunning
                        ? $"CLIENTE • RX {audio.ReceivedPackets} • buffer {audio.BufferedMs:0} ms • saída {audio.SelectedOutputDeviceName} • {audio.ClientVolumePercent}%"
                        : $"CLIENTE • {audio.StatusText}";
            }
            else if (audio.HostCaptureRunning)
            {
                RemoteAudioStatusTextBlock.Text =
                    $"HOST • {audio.StatusText} • TX {audio.SentPackets}";
            }
            else if (!string.IsNullOrWhiteSpace(audio.LastError))
            {
                RemoteAudioStatusTextBlock.Text = audio.LastError;
            }
            else if (audio.SelectedProcessId is not null)
            {
                RemoteAudioStatusTextBlock.Text =
                    $"Selecionado • {audio.SelectedProcessName}";
            }
            else
            {
                RemoteAudioStatusTextBlock.Text =
                    "Selecione o aplicativo cujo som será transmitido.";
            }

            bool healthy =
                audio.HostCaptureRunning ||
                (client && audio.ClientPlaybackRunning);
            RemoteAudioStatusTextBlock.Foreground =
                new SolidColorBrush(
                    healthy
                        ? ColorHelper.FromArgb(255, 239, 43, 86)
                        : ColorHelper.FromArgb(255, 138, 138, 138));
        }

        private void UpdateGamepadUi()
        {
            if (GamepadStatusTextBlock is null)
                return;

            RemoteSessionSnapshot session =
                _remoteSession.GetSnapshot();
            RemoteGamepadServiceSnapshot gamepad =
                _remoteGamepad.GetSnapshot();

            if (!session.Connected)
            {
                GamepadStatusTextBlock.Text =
                    "Aguardando sessão remota • XInput/DualSense automático no cliente.";
                GamepadStatusTextBlock.Foreground =
                    new SolidColorBrush(
                        ColorHelper.FromArgb(
                            255, 138, 138, 138));
                return;
            }

            if (session.Role == RemoteSessionRole.Client)
            {
                string localSource = !string.IsNullOrWhiteSpace(gamepad.LocalControllerName)
                    ? gamepad.LocalControllerName
                    : (gamepad.LocalControllerIndex >= 0
                        ? $"XInput {gamepad.LocalControllerIndex + 1}"
                        : "gamepad");
                GamepadStatusTextBlock.Text =
                    gamepad.LocalControllerConnected
                        ? $"CLIENTE • {localSource} • TX {gamepad.TransmittedPackets} • polling 250 Hz"
                        : "CLIENTE • nenhum XInput ou DualSense/HID compatível detectado";

                GamepadStatusTextBlock.Foreground =
                    new SolidColorBrush(
                        gamepad.LocalControllerConnected
                            ? ColorHelper.FromArgb(255, 239, 43, 86)
                            : ColorHelper.FromArgb(255, 160, 160, 160));
                return;
            }

            string age =
                session.RemoteGamepadPackets == 0
                    ? "--"
                    : $"{session.RemoteGamepadAgeMs:0} ms";

            GamepadStatusTextBlock.Text =
                gamepad.VirtualControllerReady
                    ? $"HOST • Xbox 360 virtual ATIVO • RX {gamepad.ReceivedPackets} • último {age}" +
                      (gamepad.StaleNeutralActive ? " • SAFE-NEUTRAL" : "")
                    : $"HOST • {gamepad.Status}" +
                      (string.IsNullOrWhiteSpace(gamepad.LastError)
                          ? ""
                          : $" • {gamepad.LastError}");

            GamepadStatusTextBlock.Foreground =
                new SolidColorBrush(
                    gamepad.VirtualControllerReady &&
                    !gamepad.StaleNeutralActive
                        ? ColorHelper.FromArgb(255, 239, 43, 86)
                        : ColorHelper.FromArgb(255, 220, 190, 90));
        }

        private void UpdateRemoteModeUi()
        {
            if (RemoteSessionTextBlock is null ||
                StartTransportButton is null)
            {
                return;
            }

            RemoteSessionSnapshot snapshot =
                _remoteSession.GetSnapshot();

            UpdateGamepadUi();
            UpdateAudioUi();

            if (LoopbackHeadlessToggleSwitch is not null)
            {
                LoopbackHeadlessToggleSwitch.Visibility =
                    snapshot.Connected
                        ? Visibility.Collapsed
                        : Visibility.Visible;

                LoopbackHeadlessToggleSwitch.IsEnabled =
                    !snapshot.Connected &&
                    _transport is null;
            }

            if (!snapshot.Connected)
            {
                RemoteSessionTextBlock.Text =
                    "Sem sessão remota • loopback disponível.";

                StartTransportButton.Content =
                    "Testar transporte local";

                StartTransportButton.IsEnabled =
                    _encoderRunning &&
                    _capture is not null &&
                    _transport is null;

                return;
            }

            if (snapshot.Role ==
                RemoteSessionRole.Host)
            {
                RemoteSessionTextBlock.Text =
                    $"HOST • {snapshot.RemoteName} • " +
                    $"{snapshot.RoundTripMs:0.0} ms RTT • " +
                    $"{snapshot.RemoteAddress}";

                StartTransportButton.Content =
                    "Iniciar transmissão remota";

                StartTransportButton.IsEnabled =
                    _encoderRunning &&
                    _capture is not null &&
                    _transport is null;
            }
            else
            {
                RemoteStreamConfig? config =
                    snapshot.StreamConfig;

                RemoteSessionTextBlock.Text =
                    config is null
                        ? $"CLIENTE • {snapshot.RemoteName} • aguardando stream"
                        : $"CLIENTE • {snapshot.RemoteName} • {config} • " +
                          $"{snapshot.RoundTripMs:0.0} ms RTT";

                StartTransportButton.Content =
                    _transport is not null &&
                    _transport.Running
                        ? "Receptor ativo"
                        : "Iniciar receptor";

                StartTransportButton.IsEnabled =
                    config is not null &&
                    _transport is null;
            }
        }

        private (
            uint Width,
            uint Height)
            GetDecodePanelPixelSize()
        {
            double scale =
                XamlRoot?.RasterizationScale ??
                1.0;

            double logicalWidth =
                ClientDecodeSwapChainPanel.ActualWidth;

            double logicalHeight =
                ClientDecodeSwapChainPanel.ActualHeight;

            if (logicalWidth < 2 ||
                logicalHeight < 2)
            {
                logicalWidth = 960;
                logicalHeight = 540;
                scale = 1.0;
            }

            uint width =
                Math.Max(
                    2u,
                    (uint)Math.Round(
                        logicalWidth *
                        scale));

            uint height =
                Math.Max(
                    2u,
                    (uint)Math.Round(
                        logicalHeight *
                        scale));

            // Keep the swap-chain dimensions even. It avoids fractional
            // chroma/layout edge cases and makes resize behavior deterministic.
            width =
                Math.Max(
                    2u,
                    width & ~1u);

            height =
                Math.Max(
                    2u,
                    height & ~1u);

            return (
                width,
                height);
        }

        private void AttachDecoderRenderer()
        {
            if (_loopbackHeadless ||
                _decoder is null)
            {
                return;
            }

            UpdateClientDecodeAspectRatio();

            var size =
                GetDecodePanelPixelSize();

            // CsWinRT: ThisPtr is the underlying WinRT IUnknown without
            // creating another RCW. Native code only QueryInterfaces it during
            // this UI-thread call and stores its own COM reference.
            IntPtr panelUnknown =
                ((IWinRTObject)
                    ClientDecodeSwapChainPanel)
                    .NativeObject
                    .ThisPtr;

            int result =
                _decoder.AttachRenderer(
                    panelUnknown,
                    size.Width,
                    size.Height);

            if (result < 0)
            {
                throw new InvalidOperationException(
                    $"Falha ao conectar SwapChainPanel ao D3D11: 0x{result:X8}");
            }

            ClientDecodePlaceholder.Visibility =
                Visibility.Visible;
        }

        private void ClientDecodeFrame_SizeChanged(
            object sender,
            SizeChangedEventArgs e)
        {
            UpdateClientDecodeAspectRatio();
        }

        private void UpdateClientDecodeAspectRatio()
        {
            double width =
                ClientDecodeFrame.ActualWidth;

            if (width < 2)
                return;

            // Keep the actual video viewport 16:9 so the D3D11 renderer no
            // longer has to pillarbox inside a very short/wide container.
            double desiredHeight =
                Math.Clamp(
                    width * 9.0 / 16.0,
                    260.0,
                    760.0);

            if (double.IsNaN(
                    ClientDecodeFrame.Height) ||
                Math.Abs(
                    ClientDecodeFrame.Height -
                    desiredHeight) >
                    0.5)
            {
                ClientDecodeFrame.Height =
                    desiredHeight;
            }
        }

        private void ClientDecodeSwapChainPanel_SizeChanged(
            object sender,
            SizeChangedEventArgs e)
        {
            if (_loopbackHeadless)
                return;

            if (_decoder is null ||
                !_decoder.Running)
            {
                return;
            }

            // ResizeBuffers is not cheap. During interactive window resizing,
            // collapse dozens of SizeChanged events into one final resize.
            _decoderResizeTimer.Stop();
            _decoderResizeTimer.Start();
        }

        private void DecoderResizeTimer_Tick(
            DispatcherQueueTimer sender,
            object args)
        {
            sender.Stop();

            if (_loopbackHeadless)
                return;

            if (_decoder is null ||
                !_decoder.Running)
            {
                return;
            }

            var size =
                GetDecodePanelPixelSize();

            int result =
                _decoder.ResizeRenderer(
                    size.Width,
                    size.Height);

            if (result < 0)
            {
                DecoderStatusTextBlock.Text =
                    $"Falha ao redimensionar D3D11: 0x{result:X8}. " +
                    "Tentando recuperar o renderer.";

                _rendererStallIntervals =
                    RendererStallIntervalsBeforeRecovery;

                TryRecoverRenderer();
            }
        }

        private void ForceIdrButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!EnsureManualVideoMutationAllowed())
                return;
            if (_capture is null)
                return;

            int hr =
                HostTransmissionService.Instance.TryRequestIdr(_capture);

            EncoderStatusTextBlock.Text =
                hr >= 0
                    ? "IDR + SPS/PPS solicitado para o próximo frame."
                    : NativeCoreService
                        .DescribeHResult(hr);
        }

        private void ResetHostStabilityProbe()
        {
            _hostStabilityState =
                HostStabilityProbeState.Warming;
            _hostPressureStreak = 0;
            _hostHealthyStreak = 0;
            _hostStabilityTransitions = 0;
            _hostStabilityValidSamples = 0;
            _hostStabilityProbeStartedAt =
                Stopwatch.GetTimestamp();
            _hostWorstOutputFps =
                double.PositiveInfinity;
            _lastPressureSnapshot =
                "nenhuma";
            _lastRecoverySnapshot =
                "nenhuma";
            _worstStabilitySnapshot =
                "nenhuma";

            if (EncoderStabilityProbeTextBlock is not null)
            {
                EncoderStabilityProbeTextBlock.Text =
                    "CORE 11.17g STABILITY SNAPSHOT  •  aquecendo amostras; A/B Flush1(COPY) ativo";
            }
        }

        private double HostStabilityProbeAgeSeconds()
        {
            if (_hostStabilityProbeStartedAt == 0)
                return 0.0;

            return Stopwatch.GetElapsedTime(
                _hostStabilityProbeStartedAt,
                Stopwatch.GetTimestamp()).TotalSeconds;
        }

        private static string AdaptivePressureStateName(uint state) =>
            state switch
            {
                1 => "PRESSURE",
                2 => "RECOVERING",
                _ => "NORMAL"
            };

        private static string ClassifyHostPressureStage(
            bool synthetic,
            double pressureFloor,
            double cadenceWgcArrivalFps,
            double cadenceWgcDeliveryFps,
            double cadencePublishFps,
            double cadenceConsumeFps,
            double submitFps,
            double outputFps,
            double copyGpuNotReadyAtSubmitFps,
            uint copyGpuPending,
            double copyGpuOldestPendingMs,
            EncoderStats encoder)
        {
            if (synthetic)
                return "NVENC-PROGRESS";

            // "WGC DELIVERY" means the capture backend itself stopped handing
            // us enough source frames. It is intentionally not described as the
            // game's render FPS; WGC delivery and game FPS are different facts.
            if (cadenceWgcArrivalFps < pressureFloor)
                return "WGC DELIVERY";

            // Arrivals are healthy but our latest-frame worker cannot move them
            // downstream quickly enough: this specifically catches GPU/D3D work
            // after FrameArrived without blaming WGC itself.
            if (cadenceWgcDeliveryFps < pressureFloor)
                return "WGC→WORKER";

            // CORE 11.17c: classify COPY_GPU_STARVATION only when the EVENT
            // query proves pending GPU copy work while downstream cadence is bad.
            bool downstreamPressure =
                cadenceConsumeFps < pressureFloor ||
                submitFps < pressureFloor ||
                outputFps < pressureFloor;
            bool copyGpuEvidence =
                copyGpuNotReadyAtSubmitFps >= 1.0 ||
                copyGpuPending >= 2 ||
                copyGpuOldestPendingMs >= 16.0;
            if (downstreamPressure && copyGpuEvidence)
                return "COPY_GPU_STARVATION";

            if (cadencePublishFps < pressureFloor)
                return "CAPTURE→RING";

            if (cadenceConsumeFps < pressureFloor)
                return "RING→WORKER";

            if (submitFps < pressureFloor)
                return "WORKER→SUBMIT";

            if (outputFps < pressureFloor ||
                encoder.PendingOutputs >= 3 ||
                encoder.OldestInFlightMs >= 45.0)
            {
                return "NVENC_COMPLETION_STARVATION";
            }

            return "TRANSIENT/MIXED";
        }

        private string BuildHostStabilitySnapshot(
            string stage,
            double cadenceWgcArrivalFps,
            double cadenceWgcDeliveryFps,
            double cadencePublishFps,
            double cadenceConsumeFps,
            double submitFps,
            double outputFps,
            double cadenceBusyDropFps,
            double remoteClockLateFps,
            double copyGpuNotReadyAtSubmitFps,
            uint copyGpuPending,
            double copyGpuOldestPendingMs,
            double copyGpuLastLatencyMs,
            double copyGpuAverageLatencyMs,
            uint adaptiveState,
            uint adaptiveTargetFps,
            uint adaptiveTransitions,
            EncoderStats encoder)
        {
            return
                $"+{HostStabilityProbeAgeSeconds():0.0}s • {stage} • out {outputFps:0.0} • " +
                $"WGC in/del/ring/unique {cadenceWgcArrivalFps:0.0}/{cadenceWgcDeliveryFps:0.0}/{cadencePublishFps:0.0}/{cadenceConsumeFps:0.0} • " +
                $"submit {submitFps:0.0} • NVENC {encoder.LastOutputLatencyMs:0.00}/{encoder.AverageOutputLatencyMs:0.00} ms last/avg • " +
                $"pending {encoder.PendingOutputs}/{encoder.InputSlots} • oldest {encoder.OldestInFlightMs:0.0} ms • " +
                $"busy {cadenceBusyDropFps:0.0}/s • deadline-late {remoteClockLateFps:0.0}/s • " +
                $"COPY not-ready {copyGpuNotReadyAtSubmitFps:0.0}/s pending {copyGpuPending} oldest {copyGpuOldestPendingMs:0.0} ms lat {copyGpuLastLatencyMs:0.0}/{copyGpuAverageLatencyMs:0.0} • " +
                $"OBSERVER {AdaptivePressureStateName(adaptiveState)}@{adaptiveTargetFps} t{adaptiveTransitions} • " +
                $"PROC {encoder.ProcessGpuPriorityEffective}";
        }

        private void UpdateHostStabilityProbe(
            bool sampleValid,
            string cadenceBackend,
            uint cadenceTargetFps,
            double cadenceWgcArrivalFps,
            double cadenceWgcDeliveryFps,
            double cadencePublishFps,
            double cadenceConsumeFps,
            double submitFps,
            double outputFps,
            double cadenceBusyDropFps,
            double remoteClockLateFps,
            double copyGpuNotReadyAtSubmitFps,
            uint copyGpuPending,
            double copyGpuOldestPendingMs,
            double copyGpuLastLatencyMs,
            double copyGpuAverageLatencyMs,
            uint adaptiveState,
            uint adaptiveTargetFps,
            uint adaptiveTransitions,
            EncoderStats encoder)
        {
            bool synthetic =
                encoder.RequestedInputMode == 3;

            uint effectiveTargetFps =
                cadenceTargetFps != 0
                    ? cadenceTargetFps
                    : encoder.TargetFps;

            if (!sampleValid || effectiveTargetFps == 0)
            {
                EncoderStabilityProbeTextBlock.Text =
                    "CORE 11.17g STABILITY SNAPSHOT  •  aquecendo amostras...";
                return;
            }

            _hostStabilityValidSamples++;

            double target =
                effectiveTargetFps;
            double pressureFloor =
                target * 0.90;
            double healthyFloor =
                target * 0.96;

            bool sourceOrRingPressure =
                !synthetic &&
                (cadenceWgcArrivalFps < pressureFloor ||
                 cadenceWgcDeliveryFps < pressureFloor ||
                 cadencePublishFps < pressureFloor ||
                 cadenceConsumeFps < pressureFloor);

            bool encoderPressure =
                outputFps < pressureFloor &&
                (submitFps < pressureFloor ||
                 encoder.PendingOutputs >= 3 ||
                 encoder.OldestInFlightMs >= 45.0 ||
                 encoder.LastOutputLatencyMs >= 20.0 ||
                 remoteClockLateFps >= 1.0);

            bool pressureSample =
                sourceOrRingPressure || encoderPressure;

            bool healthySample =
                outputFps >= healthyFloor &&
                (synthetic ||
                 (cadenceWgcDeliveryFps >= healthyFloor &&
                  cadencePublishFps >= healthyFloor &&
                  cadenceConsumeFps >= healthyFloor)) &&
                encoder.OldestInFlightMs < 45.0;

            if (pressureSample)
            {
                _hostPressureStreak++;
                _hostHealthyStreak = 0;
            }
            else if (healthySample)
            {
                _hostHealthyStreak++;
                _hostPressureStreak = 0;
            }
            else
            {
                _hostPressureStreak = 0;
                _hostHealthyStreak = 0;
            }

            string stage =
                ClassifyHostPressureStage(
                    synthetic,
                    pressureFloor,
                    cadenceWgcArrivalFps,
                    cadenceWgcDeliveryFps,
                    cadencePublishFps,
                    cadenceConsumeFps,
                    submitFps,
                    outputFps,
                    copyGpuNotReadyAtSubmitFps,
                    copyGpuPending,
                    copyGpuOldestPendingMs,
                    encoder);

            string snapshot =
                BuildHostStabilitySnapshot(
                    stage,
                    cadenceWgcArrivalFps,
                    cadenceWgcDeliveryFps,
                    cadencePublishFps,
                    cadenceConsumeFps,
                    submitFps,
                    outputFps,
                    cadenceBusyDropFps,
                    remoteClockLateFps,
                    copyGpuNotReadyAtSubmitFps,
                    copyGpuPending,
                    copyGpuOldestPendingMs,
                    copyGpuLastLatencyMs,
                    copyGpuAverageLatencyMs,
                    adaptiveState,
                    adaptiveTargetFps,
                    adaptiveTransitions,
                    encoder);

            if (_hostStabilityValidSamples >= 3 &&
                outputFps > 0.1 &&
                outputFps < _hostWorstOutputFps)
            {
                _hostWorstOutputFps = outputFps;
                _worstStabilitySnapshot = snapshot;
            }

            if (_hostPressureStreak >= 2 &&
                _hostStabilityState != HostStabilityProbeState.Pressure)
            {
                _hostStabilityState =
                    HostStabilityProbeState.Pressure;
                _hostStabilityTransitions++;
                _lastPressureSnapshot = snapshot;
            }
            else if (_hostStabilityState == HostStabilityProbeState.Pressure &&
                     _hostHealthyStreak > 0)
            {
                _hostStabilityState =
                    HostStabilityProbeState.Recovering;
            }

            if (_hostHealthyStreak >= 3 &&
                _hostStabilityState != HostStabilityProbeState.Healthy)
            {
                HostStabilityProbeState previousState =
                    _hostStabilityState;

                _hostStabilityState =
                    HostStabilityProbeState.Healthy;

                if (previousState == HostStabilityProbeState.Pressure ||
                    previousState == HostStabilityProbeState.Recovering)
                {
                    _hostStabilityTransitions++;
                    _lastRecoverySnapshot = snapshot;
                }
            }

            string stateLabel =
                _hostStabilityState switch
                {
                    HostStabilityProbeState.Healthy => "OK",
                    HostStabilityProbeState.Pressure => "PRESSURE",
                    HostStabilityProbeState.Recovering => "RECOVERING",
                    _ => "WARMING"
                };

            EncoderStabilityProbeTextBlock.Text =
                $"CORE 11.17g STABILITY SNAPSHOT  •  {stateLabel} [{stage}]  •  transições {_hostStabilityTransitions}  •  " +
                $"streak P/H {_hostPressureStreak}/{_hostHealthyStreak}  •  backend {cadenceBackend}  •  " +
                $"native-observer {AdaptivePressureStateName(adaptiveState)}@{adaptiveTargetFps}\n" +
                $"AGORA  •  {snapshot}\n" +
                $"ÚLTIMA PRESSÃO  •  {_lastPressureSnapshot}\n" +
                $"ÚLTIMA RECUPERAÇÃO  •  {_lastRecoverySnapshot}\n" +
                $"PIOR AMOSTRA  •  {_worstStabilitySnapshot}";
        }

        private void ResetRingFenceIsolationProbe()
        {
            _lastProbeRingAcquireCalls = 0;
            _lastProbeRingNoReady = 0;
            _lastProbeRingSelected = 0;
            _lastProbeCaptureFenceReady = 0;
            _lastProbeCaptureFencePending = 0;
            _lastProbeNoReusableSlots = 0;
            _ringFenceProbeInitialized = false;

            if (EncoderRingFenceProbeTextBlock is not null)
            {
                EncoderRingFenceProbeTextBlock.Text =
                    "CORE 11.12 RING/FENCE ISOLATION PROBE  •  aguardando amostra";
            }
        }

        private void UpdateRingFenceIsolationProbe(
            CapturePipelineStats? capture,
            EncoderStats encoder,
            double seconds)
        {
            if (encoder.RequestedInputMode == 10)
            {
                EncoderRingFenceProbeTextBlock.Text =
                    "CORE 11.17g FIXED-60 + WGC-14MS-RESERVE + LATEST-WORKER ATIVO  •  Flush1(COPY) assíncrono após GPU EVENT • DXGI duplication ocioso • sem captura dupla • sem shared-ring cross-device/fences\n" +
                    $"LOCAL RING  •  ready {capture?.RingReadyFrames ?? 0}/4  •  ring-pub {capture?.PublishedToRing ?? 0}  •  " +
                    $"COPY GPU pending {capture?.CopyGpuProbePending ?? 0}  •  ready-at-submit {capture?.CopyGpuReadyAtSubmit ?? 0} / not-ready {capture?.CopyGpuNotReadyAtSubmit ?? 0}  •  " +
                    $"lat {(capture?.CopyGpuLastLatencyMs ?? 0):0.00}/{(capture?.CopyGpuAverageLatencyMs ?? 0):0.00}/{(capture?.CopyGpuMaxLatencyMs ?? 0):0.00} ms last/avg/max";
                return;
            }

            if (encoder.RequestedInputMode == 9)
            {
                EncoderRingFenceProbeTextBlock.Text =
                    "CORE 11.15c FAIR GPU + NATIVE HOST FALLBACK  •  zero scaling/VP no host; CopyResource nativo no EncodeDevice; cliente escala\n" +
                    $"RING/FENCE  •  ready {capture?.RingCaptureFenceReady ?? 0}  •  pending {capture?.RingCaptureFencePending ?? 0}  •  " +
                    $"release-pending {capture?.RingReleaseFencePendingSlots ?? 0}/3  •  VP pending {encoder.VideoProcessorFencePending}  •  preprocess-copy pending {encoder.PreprocessCopyFencePending}";
                return;
            }

            if (encoder.RequestedInputMode == 8)
            {
                EncoderRingFenceProbeTextBlock.Text =
                    "CORE 11.14 ENCODE-SIDE RGB SCALER FALLBACK  •  capture fence = dependência GPU→GPU; CPU gate OFF; VideoProcessor OFF\n" +
                    $"RING/FENCE  •  ready {capture?.RingCaptureFenceReady ?? 0}  •  pending {capture?.RingCaptureFencePending ?? 0}  •  " +
                    $"release-pending {capture?.RingReleaseFencePendingSlots ?? 0}/3  •  VP pending {encoder.VideoProcessorFencePending}  •  preprocess-copy pending {encoder.PreprocessCopyFencePending}";
                return;
            }

            if (encoder.RequestedInputMode == 7)
            {
                EncoderRingFenceProbeTextBlock.Text =
                    "CORE 11.13 DIRECT-RING RGB FALLBACK  •  capture fence CPU-ready + NVENC external ARGB\n" +
                    $"RING/FENCE  •  ready {capture?.RingCaptureFenceReady ?? 0}  •  pending {capture?.RingCaptureFencePending ?? 0}  •  " +
                    $"release-pending {capture?.RingReleaseFencePendingSlots ?? 0}/3  •  VP pending {encoder.VideoProcessorFencePending}  •  preprocess-copy pending {encoder.PreprocessCopyFencePending}";
                return;
            }

            if (capture is null ||
                seconds <= 0.05 ||
                encoder.RequestedInputMode != 6)
            {
                EncoderRingFenceProbeTextBlock.Text =
                    encoder.RequestedInputMode == 6
                        ? "CORE 11.12 RING/FENCE ISOLATION PROBE  •  aquecendo amostra"
                        : "CORE 11.12 RING/FENCE ISOLATION PROBE  •  selecione CORE 11.12 WGC→NV12";
                return;
            }

            if (!_ringFenceProbeInitialized)
            {
                _lastProbeRingAcquireCalls = capture.RingAcquireCalls;
                _lastProbeRingNoReady = capture.RingAcquireNoReadyFrames;
                _lastProbeRingSelected = capture.RingAcquireReadySelections;
                _lastProbeCaptureFenceReady = capture.RingCaptureFenceReady;
                _lastProbeCaptureFencePending = capture.RingCaptureFencePending;
                _lastProbeNoReusableSlots = capture.RingPublishNoReusableSlots;
                _ringFenceProbeInitialized = true;
                EncoderRingFenceProbeTextBlock.Text =
                    "CORE 11.12 RING/FENCE ISOLATION PROBE  •  baseline capturada; aguarde ~1 s";
                return;
            }

            static ulong Delta(ulong current, ulong previous) =>
                current >= previous ? current - previous : current;

            double acquireRate = Delta(capture.RingAcquireCalls, _lastProbeRingAcquireCalls) / seconds;
            double noReadyRate = Delta(capture.RingAcquireNoReadyFrames, _lastProbeRingNoReady) / seconds;
            double selectedRate = Delta(capture.RingAcquireReadySelections, _lastProbeRingSelected) / seconds;
            double fenceReadyRate = Delta(capture.RingCaptureFenceReady, _lastProbeCaptureFenceReady) / seconds;
            double fencePendingRate = Delta(capture.RingCaptureFencePending, _lastProbeCaptureFencePending) / seconds;
            double noReusableRate = Delta(capture.RingPublishNoReusableSlots, _lastProbeNoReusableSlots) / seconds;

            _lastProbeRingAcquireCalls = capture.RingAcquireCalls;
            _lastProbeRingNoReady = capture.RingAcquireNoReadyFrames;
            _lastProbeRingSelected = capture.RingAcquireReadySelections;
            _lastProbeCaptureFenceReady = capture.RingCaptureFenceReady;
            _lastProbeCaptureFencePending = capture.RingCaptureFencePending;
            _lastProbeNoReusableSlots = capture.RingPublishNoReusableSlots;

            string verdict;
            if (capture.RingReleaseFencePendingSlots >= 2 || noReusableRate >= 2.0)
            {
                verdict = "RELEASE_FENCE_SLOTS";
            }
            else if (fencePendingRate >= Math.Max(3.0, selectedRate * 0.25) ||
                     capture.RingCaptureFenceLag >= 2)
            {
                verdict = "CAPTURE_FENCE_PENDING";
            }
            else if (encoder.VideoProcessorFencePending >= 2 &&
                     encoder.VideoProcessorFenceOldestAgeMs >= 12.0)
            {
                verdict = "VIDEO_PROCESSOR_PENDING";
            }
            else if (encoder.PreprocessCopyFencePending >= 2 &&
                     encoder.PreprocessCopyFenceOldestAgeMs >= 12.0)
            {
                verdict = "PREPROCESS_COPY_PENDING";
            }
            else if (noReadyRate >= 3.0)
            {
                verdict = "NO_NEW_SEQUENCE";
            }
            else
            {
                verdict = "NO_DOMINANT_STALL";
            }

            EncoderRingFenceProbeTextBlock.Text =
                $"CORE 11.12 RING/FENCE ISOLATION PROBE  •  VERDICT {verdict}\n" +
                $"RING ACQUIRE  •  calls {acquireRate:0.0}/s  •  selected {selectedRate:0.0}/s  •  NO_NEW_SEQUENCE {noReadyRate:0.0}/s  •  " +
                $"wait {capture.RingAcquireWaitMs:0.00}/{capture.RingAverageAcquireWaitMs:0.00} ms  •  lease-age {capture.RingLeaseAgeMs:0.00}/{capture.RingAverageLeaseAgeMs:0.00} ms\n" +
                $"FENCE/COPY PROBE  •  ready {fenceReadyRate:0.0}/s  •  CAPTURE_FENCE_PENDING {fencePendingRate:0.0}/s  •  " +
                $"lag {capture.RingCaptureFenceLag}/{capture.RingMaxCaptureFenceLag}  •  RELEASE_FENCE_SLOTS {capture.RingReleaseFencePendingSlots}/3  •  no-reusable {noReusableRate:0.0}/s\n" +
                $"ENCODE GPU  •  PREPROCESS_COPY_PENDING {encoder.PreprocessCopyFencePending} (max {encoder.PreprocessCopyFenceMaxPending}, oldest {encoder.PreprocessCopyFenceOldestAgeMs:0.0} ms)  •  " +
                $"VIDEO_PROCESSOR_PENDING {encoder.VideoProcessorFencePending} (max {encoder.VideoProcessorFenceMaxPending}, oldest {encoder.VideoProcessorFenceOldestAgeMs:0.0} ms)\n" +
                $"COUNTERS  •  ring calls/selected/no-new {capture.RingAcquireCalls}/{capture.RingAcquireReadySelections}/{capture.RingAcquireNoReadyFrames}  •  " +
                $"capture fence ready/pending {capture.RingCaptureFenceReady}/{capture.RingCaptureFencePending}  •  copy signal/done {encoder.PreprocessCopyFenceSignals}/{encoder.PreprocessCopyFenceCompleted}  •  " +
                $"VP signal/done {encoder.VideoProcessorFenceSignals}/{encoder.VideoProcessorFenceCompleted}";
        }

        private void ResetEncoderStatsText()
        {
            EncoderFpsTextBlock.Text =
                "-- FPS";

            EncoderOutputBitrateTextBlock.Text =
                "-- Mbps";

            EncoderLatencyTextBlock.Text =
                "-- ms";

            EncoderSubmitTextBlock.Text =
                "-- ms";

            EncoderQueueTextBlock.Text =
                "0/4 • 0";

            EncoderDiagnosticsTextBlock.Text =
                "ENCODER DIAG  •  aguardando encoder";

            if (!_encoderRunning)
            {
                EncoderStabilityProbeTextBlock.Text =
                    "CORE 11.17g STABILITY SNAPSHOT  •  aguardando encoder";
                EncoderRingFenceProbeTextBlock.Text =
                    "CORE 11.12 RING/FENCE ISOLATION PROBE  •  aguardando encoder";
            }
        }

        private void ResetDecoderStatsText()
        {
            DecoderFpsLabelTextBlock.Text =
                "D3D11 present FPS";

            DecoderRenderedLabelTextBlock.Text =
                "Frames apresentados D3D11";

            DecoderFpsTextBlock.Text =
                "-- FPS";

            DecoderLagTextBlock.Text =
                "-- ms";

            DecoderDropTextBlock.Text =
                "0";

            DecoderSubmittedTextBlock.Text =
                "0";

            DecoderRenderedTextBlock.Text =
                "0";

            DecoderPresentWaitTextBlock.Text =
                "-- ms";

            DecoderLatestSkipTextBlock.Text =
                "0";

            DecoderPresentErrorTextBlock.Text =
                "0";

            _lastDecoderRendered = 0;

            if (ClientDecodePlaceholder is not null)
            {
                ClientDecodePlaceholder.Text =
                    "Aguardando D3D11...";

                ClientDecodePlaceholder.Visibility =
                    Visibility.Visible;
            }
        }

        private void ResetTransportStatsText()
        {
            TransportTxTextBlock.Text =
                "-- Mbps";

            TransportRxTextBlock.Text =
                "-- Mbps";

            TransportFrameRateTextBlock.Text =
                "-- FPS";

            TransportPacketRateTextBlock.Text =
                "-- pkt/s";

            TransportLatencyTextBlock.Text =
                "-- ms";

            TransportDropTextBlock.Text =
                "0 / 0";

            TransportPendingTextBlock.Text =
                "0";

            _lastTransportTxBytes = 0;
            _lastTransportRxBytes = 0;
            _lastTransportTxPackets = 0;
            _lastTransportRxPackets = 0;
            _lastTransportRxFrames = 0;
        }

        private static Border Card(
            string title,
            string detail)
        {
            var border =
                new Border
                {
                    Background =
                        Brush(11, 11, 11),

                    BorderBrush =
                        Brush(32, 32, 32),

                    BorderThickness =
                        new Thickness(1),

                    CornerRadius =
                        new CornerRadius(11),

                    Padding =
                        new Thickness(17)
                };

            var titleText =
                new TextBlock
                {
                    Text = title,

                    Foreground =
                        Brush(235, 235, 235),

                    FontWeight =
                        Microsoft.UI.Text
                            .FontWeights.SemiBold
                };

            var detailText =
                new TextBlock
                {
                    Text = detail,

                    Foreground =
                        Brush(130, 130, 130),

                    FontSize = 10,

                    Margin =
                        new Thickness(
                            0,
                            5,
                            0,
                            0)
                };

            var stack =
                new StackPanel();

            stack.Children.Add(
                titleText);

            stack.Children.Add(
                detailText);

            border.Child =
                stack;

            return border;
        }

        private static SolidColorBrush Brush(
            byte red,
            byte green,
            byte blue)
        {
            return new SolidColorBrush(
                ColorHelper.FromArgb(
                    255,
                    red,
                    green,
                    blue));
        }
    }
}
