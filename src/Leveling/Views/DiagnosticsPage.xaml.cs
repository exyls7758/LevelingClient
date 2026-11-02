using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Multiverse.Services;
using System;
using System.Diagnostics;

namespace Multiverse.Views
{
    public sealed partial class DiagnosticsPage : Page
    {
        private readonly RemoteSessionService _session = RemoteSessionService.Instance;
        private readonly RemoteStreamingRuntime _runtime = RemoteStreamingRuntime.Instance;
        private readonly RemoteAudioService _audio = RemoteAudioService.Instance;
        private readonly RemoteGamepadService _gamepad = RemoteGamepadService.Instance;
        private readonly Process _currentProcess = Process.GetCurrentProcess();
        private readonly DispatcherTimer _timer = new();
        private string _diagnosticsLevel = "Basic";

        public DiagnosticsPage()
        {
            InitializeComponent();
            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += (_, _) => SafeUpdateDiagnostics();
            Loaded += (_, _) =>
            {
                FriendRequestPromptCoordinator.Instance.Attach(this);
                ApplyDiagnosticsLevel();

                if (_diagnosticsLevel != "Off")
                {
                    SafeUpdateDiagnostics();
                    _timer.Start();
                }
            };
            Unloaded += (_, _) =>
            {
                _timer.Stop();
                FriendRequestPromptCoordinator.Instance.Detach(this);
            };
        }

        private void OpenStartupLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = StartupDiagnostics.LogPath;
                string? directory = System.IO.Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory))
                    throw new System.IO.DirectoryNotFoundException("A pasta de registros ainda não está disponível.");
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + directory + "\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StartupStatusText.Text = "Não foi possível abrir a pasta: " + ex.Message;
            }
        }

        private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            if (!ExportDiagnosticsButton.IsEnabled) return;
            ExportDiagnosticsButton.IsEnabled = false;
            ExportStatusText.Text = "Preparando o arquivo de diagnóstico...";
            try
            {
                string path = await VideoIncidentRecorder.Instance.ExportAsync();
                ExportStatusText.Text = "Diagnóstico salvo: " + path;
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
                    {
                        UseShellExecute = true
                    });
                }
                catch { }
            }
            catch (Exception ex)
            {
                ExportStatusText.Text = "Não foi possível exportar: " + ex.Message;
            }
            finally { ExportDiagnosticsButton.IsEnabled = true; }
        }

        private void ApplyDiagnosticsLevel()
        {
            MultiverseAppPreferences preferences = MultiverseSettingsService.Instance.GetAppPreferences();
            _diagnosticsLevel = preferences.DiagnosticsLevel switch
            {
                "Off" => "Off",
                "Advanced" => "Advanced",
                _ => "Basic"
            };

            if (_diagnosticsLevel == "Off")
            {
                BasicDiagnosticsGrid.Visibility = Visibility.Collapsed;
                AdvancedDiagnosticsGrid.Visibility = Visibility.Collapsed;
                PeripheralDiagnosticsGrid.Visibility = Visibility.Collapsed;
                DiagnosticsModeText.Text = "Nível: desativado — nenhuma telemetria periódica é coletada nesta página.";
                return;
            }

            BasicDiagnosticsGrid.Visibility = Visibility.Visible;
            PeripheralDiagnosticsGrid.Visibility = Visibility.Visible;
            AdvancedDiagnosticsGrid.Visibility = _diagnosticsLevel == "Advanced"
                ? Visibility.Visible
                : Visibility.Collapsed;
            DiagnosticsModeText.Text = _diagnosticsLevel == "Advanced"
                ? "Nível: avançado — sessão, transporte, WGC, NVENC, áudio e controle."
                : "Nível: básico — sessão, transporte, recuperação de microtravadas, áudio e controle; detalhes completos WGC/NVENC ficam ocultos.";
        }

        private void SafeUpdateDiagnostics()
        {
            if (_diagnosticsLevel == "Off")
                return;

            try
            {
                UpdateDiagnostics();
            }
            catch (Exception ex)
            {
                _timer.Stop();
                DiagnosticsModeText.Text = $"Diagnóstico pausado: {ex.Message}";
            }
        }

        private void UpdateDiagnostics()
        {
            StartupStatusText.Text = BuildInfo.DisplayVersion + " • etapa: " + StartupDiagnostics.CurrentStage +
                "\nArmazenamento: " + MultiverseSettingsService.Instance.StorageDescription +
                "\nÚltima falha de gravação: " +
                (string.IsNullOrWhiteSpace(MultiverseSettingsService.Instance.LastPersistenceError) ?
                    "nenhuma" : MultiverseSettingsService.Instance.LastPersistenceError) +
                "\nRegistro: " + StartupDiagnostics.LogPath +
                (string.IsNullOrWhiteSpace(StartupDiagnostics.LastError) ? "" :
                    "\nErro do registrador: " + StartupDiagnostics.LastError);
            PipelineHealthText.Text = RemotePipelineTelemetry.Instance.GetDisplayText();
            RemoteSessionSnapshot session = _session.GetSnapshot();
            RemoteStreamingState runtime = _runtime.GetState();
            RemoteAudioSnapshot audio = _audio.GetSnapshot();
            RemoteGamepadServiceSnapshot pad = _gamepad.GetSnapshot();

            _currentProcess.Refresh();
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            double workingSetMb = _currentProcess.WorkingSet64 / (1024.0 * 1024.0);
            double privateMb = _currentProcess.PrivateMemorySize64 / (1024.0 * 1024.0);
            double managedHeapMb = gc.HeapSizeBytes / (1024.0 * 1024.0);
            double managedLiveMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
            ProcessResourceDiagText.Text =
                $"RAM física {workingSetMb:0} MB • privada/commit {privateMb:0} MB\n" +
                $"heap .NET {managedHeapMb:0} MB • objetos vivos {managedLiveMb:0} MB\n" +
                $"handles {_currentProcess.HandleCount} • threads {_currentProcess.Threads.Count}";

            var flight = VideoIncidentRecorder.Instance;
            RecorderStatusText.Text = "Último incidente: " +
                (string.IsNullOrWhiteSpace(flight.LastReason) ? "nenhum" : flight.LastReason) +
                " • registros descartados: " + flight.DroppedEntries +
                (string.IsNullOrWhiteSpace(flight.LastWriteError) ? "" : " • gravação: " + flight.LastWriteError);

            SessionDiagText.Text =
                $"{session.StatusText}\nrole {session.Role} • peer {session.RemoteName}\n" +
                $"RTT {session.RoundTripMs:0.0} ms • base {session.BaseRoundTripMs:0.0} • fila {session.QueueDelayMs:0.0} • jitter {session.JitterMs:0.0}\n" +
                $"perda remota {session.RemotePacketLossPercent:0.00}% • FPS remoto {session.RemoteVideoFps:0.0}\n" +
                $"ÚLTIMA FALHA DE VÍDEO • {(string.IsNullOrWhiteSpace(session.VideoStartupError) ? "sem falha registrada" : session.VideoStartupError)}";

            TransportSnapshot? transportSnapshot = runtime.Transport?.GetSnapshot();
            if (transportSnapshot is not null)
            {
                TransportSnapshot t = transportSnapshot;
                TransportDiagText.Text =
                    $"{t.Mode} • running {t.Running} • {t.RemoteEndpoint}\n" +
                    $"TX frames {t.SentFrames} • RX frames {t.ReceivedFrames} • pendentes {t.PendingFrames}\n" +
                    $"send {t.ActualSendMbps:0.0} Mbps • pacing {t.CurrentPacingMbps:0.0}/{t.TargetPacingMbps:0.0} • backlog {t.PacerBacklogMs:0.0} ms\n" +
                    $"gaps {t.SequenceGaps} • incompletos {t.IncompleteFramesDropped} • FEC {t.FecRecoveredFragments} • drops decode {t.DecodeDispatchDrops}";
            }
            else
            {
                TransportDiagText.Text = "Transporte de vídeo inativo.";
            }

            EncoderStats? microbreakEncoder = runtime.Capture?.GetEncoderStatsDiagnostic();
            NativeNvdecSnapshot? microbreakDecoder = runtime.Decoder?.GetSnapshot();
            long framesDroppedBeforeSend = transportSnapshot?.FramesDroppedBeforeSend ?? 0;
            long fecRecovered = transportSnapshot?.FecRecoveredFragments ?? 0;
            long sequenceGaps = transportSnapshot?.SequenceGaps ?? 0;
            long incompleteFrames = transportSnapshot?.IncompleteFramesDropped ?? 0;
            long decodeDispatchDrops = transportSnapshot?.DecodeDispatchDrops ?? 0;

            MicrobreakDiagText.Text =
                $"REDE/RX  gaps {sequenceGaps} • incompletos {incompleteFrames} • FEC recuperados {fecRecovered}\n" +
                $"HOST/TX  drops antes envio {framesDroppedBeforeSend} • QueueResets {microbreakEncoder?.QueueResets ?? 0} • pacing drops {microbreakEncoder?.PacingDrops ?? 0}\n" +
                $"DECODER  dispatch drops {decodeDispatchDrops} • stale {microbreakDecoder?.StaleFramesDropped ?? 0} • waiting-keyframe {microbreakDecoder?.WaitingForKeyFrameDrops ?? 0} • IDR recuperação {microbreakDecoder?.RecoveryIdrRequests ?? 0}\n" +
                $"PRESENT  drops {microbreakDecoder?.D3D11DroppedPresents ?? 0} • timeouts {microbreakDecoder?.D3D11PresentWaitTimeouts ?? 0} • still-drawing {microbreakDecoder?.D3D11PresentStillDrawing ?? 0} • failures {microbreakDecoder?.D3D11PresentFailures ?? 0}";

            if (_diagnosticsLevel == "Advanced")
            {
                NativeCaptureSession? capture = runtime.Capture;
                CapturePipelineStats? c = capture?.GetCapturePipelineStatsDiagnostic();
                if (c is not null)
                {
                    CaptureDiagText.Text =
                        $"WGC running {c.BackendRunning} • target {c.AdaptiveTargetFps} • ingress cap {c.IngressPublishCapFps}\n" +
                        $"arrived {c.BackendFramesArrived} • delivered {c.BackendFramesDelivered} • replaced {c.BackendPendingFramesReplaced}\n" +
                        $"ring ocupado {c.CaptureRingOccupiedFrames} • copy avg {c.CopyGpuAverageLatencyMs:0.00} ms • max {c.CopyGpuMaxLatencyMs:0.00} ms\n" +
                        $"pressure {c.AdaptivePressureState} • healthy streak {c.AdaptiveHealthyStreak}";
                }
                else
                {
                    CaptureDiagText.Text = "Captura nativa inativa neste PC/role.";
                }

                EncoderStats? e = capture?.GetEncoderStatsDiagnostic();
                if (e is not null)
                {
                    EncoderDiagText.Text =
                        $"running {e.Running} • {e.Width}×{e.Height} @ {e.TargetFps} • {e.BitrateKbps / 1000.0:0.#} Mbps\n" +
                        $"encoded {e.EncodedFrames} • submitted {e.SubmittedFrames} • in-flight {e.InFlightFrames}/{e.InputSlots}\n" +
                        $"encode avg {e.AverageEncodeMs:0.00} ms • submit avg {e.AverageSubmitMs:0.00} ms • output {e.AverageOutputLatencyMs:0.00} ms\n" +
                        $"no-slot {e.NoFreeSlotDrops} • busy {e.EncoderBusyRetries} • oldest {e.OldestInFlightMs:0.0} ms";
                }
                else
                {
                    EncoderDiagText.Text = "NVENC inativo neste PC/role.";
                }
            }

            AudioDiagText.Text =
                $"{audio.StatusText}\nHOST {audio.HostCaptureRunning} • CLIENT {audio.ClientPlaybackRunning}\n" +
                $"voz: {audio.VoicePrivacyStatus} • bloqueadas {audio.VoicePrivacyBlockedSources}\n" +
                $"processo {audio.SelectedProcessName} • saída {audio.SelectedOutputDeviceName} • volume {audio.ClientVolumePercent}%\n" +
                $"TX {audio.SentPackets} • RX {audio.ReceivedPackets} • drop {audio.DroppedPackets} • buffer {audio.BufferedMs:0} ms";

            string localController = !pad.LocalControllerConnected
                ? "nenhum"
                : (!string.IsNullOrWhiteSpace(pad.LocalControllerName)
                    ? pad.LocalControllerName
                    : (pad.LocalControllerIndex >= 0 ? $"XInput {pad.LocalControllerIndex + 1}" : "gamepad"));
            GamepadDiagText.Text =
                $"{pad.Status}\nlocal {pad.LocalControllerConnected} • {localController} • virtual {pad.VirtualControllerReady}\n" +
                $"TX {pad.TransmittedPackets} • RX {pad.ReceivedPackets} • age {pad.RemotePacketAgeMs:0} ms\n" +
                $"remote {pad.RemoteControllerConnected} • safe-neutral {pad.StaleNeutralActive}";
        }
    }
}
