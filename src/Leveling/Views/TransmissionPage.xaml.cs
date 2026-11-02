using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Multiverse.Services;
using System;
using System.Linq;
using System.Threading;

namespace Multiverse.Views
{
    public sealed partial class TransmissionPage : Page
    {
        private readonly DispatcherTimer _telemetryTimer = new();
        private readonly HostVideoPipelineController _host = HostTransmissionService.Instance;
        private readonly MultiverseSettingsService _settings = MultiverseSettingsService.Instance;
        private CancellationTokenSource? _pageCancellation;
        private long _presented, _sampleTick, _stream;
        private int _sessionGeneration;
        private bool _profileReady;

        public TransmissionPage()
        {
            InitializeComponent();
            _telemetryTimer.Interval = TimeSpan.FromMilliseconds(500);
            _telemetryTimer.Tick += (_, _) => UpdateRuntimeTelemetry();
            Loaded += TransmissionPage_Loaded;
            Unloaded += TransmissionPage_Unloaded;
        }

        private void TransmissionPage_Loaded(object sender, RoutedEventArgs e)
        {
            _pageCancellation = new();
            var saved = _settings.GetStreamProfile();
            uint expectedBitrate = saved.MaxHeight == 1440 ? 35000u : saved.MaxHeight == 720 ? 10000u : 20000u;
            QualityPresetCombo.SelectedIndex = saved.Fps == 60 && saved.BitrateKbps == expectedBitrate
                ? saved.MaxHeight == 1440 ? 2 : saved.MaxHeight == 720 ? 0 : 1 : 3;
            SelectValue(ResolutionCombo, $"{saved.MaxHeight}p");
            SelectValue(FpsCombo, saved.Fps.ToString());
            SelectValue(BitrateCombo, saved.BitrateKbps.ToString(), $"{saved.BitrateKbps / 1000.0:0.#} Mbps");
            SelectValue(NvencCombo, saved.Preset.ToString());
            LowLatencyToggle.IsOn = saved.UltraLowLatency;
            _profileReady = true;
            FriendRequestPromptCoordinator.Instance.Attach(this);
            _host.StateChanged += Host_StateChanged;
            SafeUpdate();
            UpdateRuntimeTelemetry();
            _telemetryTimer.Start();
        }

        private void TransmissionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _pageCancellation?.Cancel();
            _pageCancellation?.Dispose();
            _pageCancellation = null;
            _telemetryTimer.Stop();
            _host.StateChanged -= Host_StateChanged;
            FriendRequestPromptCoordinator.Instance.Detach(this);
        }

        private void Host_StateChanged(object? sender, EventArgs e) =>
            DispatcherQueue.TryEnqueue(SafeUpdate);

        private void SafeUpdate()
        {
            try
            {
                HostTransmissionSnapshot state = _host.GetSnapshot();
                MultiverseStreamProfile profile = _settings.GetStreamProfile();
                TransmissionStatusText.Text = state.StatusText;
                TransmissionDetailText.Text = string.IsNullOrWhiteSpace(state.LastError)
                    ? state.RemoteTransportRunning
                        ? "Vídeo sendo enviado. Acompanhe abaixo a apresentação confirmada pelo cliente."
                        : state.Armed
                            ? "Encoder pronto; o transporte inicia automaticamente quando o cliente conecta."
                            : "Escolha um perfil e inicie quando estiver pronto."
                    : state.LastError;
                ProfileText.Text = $"Salvo: até {profile.MaxHeight}p • {profile.Fps} FPS • {profile.BitrateKbps / 1000.0:0.#} Mbps";
                ApplyProfileButton.IsEnabled = !state.CanStop;
                QualityPresetCombo.IsEnabled = !state.CanStop;
                ResolutionCombo.IsEnabled = FpsCombo.IsEnabled = BitrateCombo.IsEnabled = NvencCombo.IsEnabled = LowLatencyToggle.IsEnabled = !state.CanStop;
                MonitorText.Text = string.IsNullOrWhiteSpace(state.MonitorDeviceName)
                    ? string.IsNullOrWhiteSpace(profile.MonitorDeviceName)
                        ? "Monitor: automático (principal compatível)"
                        : $"Monitor salvo: {profile.MonitorDeviceName}"
                    : $"Monitor ativo: {state.MonitorDeviceName}";
                StartButton.IsEnabled = !state.CanStop;
                StopButton.IsEnabled = state.CanStop;
            }
            catch (Exception ex)
            {
                TransmissionStatusText.Text = "Falha somente na tela de transmissão.";
                TransmissionDetailText.Text = ex.Message;
            }
        }

        private void UpdateRuntimeTelemetry()
        {
            var session = RemoteSessionService.Instance.GetSnapshot();
            var telemetry = RemotePipelineTelemetry.Instance.GetSnapshot();
            if (TechnicalDetails.IsExpanded) RuntimeTelemetryTextBlock.Text = telemetry.Text;
            long now = Environment.TickCount64;
            bool fresh = session.Connected && session.RemoteTelemetryAgeMs >= 0 && session.RemoteTelemetryAgeMs < 2000;
            bool same = _sessionGeneration == session.SessionGeneration && _stream == session.StreamConfig?.Generation;
            double? fps = session.Role == RemoteSessionRole.Client ? telemetry.PresentFps :
                fresh && same && _sampleTick > 0 && now > _sampleTick && session.RemotePresentedFrames >= _presented
                ? (session.RemotePresentedFrames - _presented) * 1000.0 / (now - _sampleTick) : null;
            PresentMetricText.Text = session.Connected && fps.HasValue ? $"{fps:0} fps" : "—";
            LatencyMetricText.Text = session.Connected && session.RoundTripMs > 0 ? $"{session.RoundTripMs:0} ms" : "—";
            double? mbps = session.Role == RemoteSessionRole.Client ? telemetry.ReceiveMbps : fresh ? session.RemoteRxMbps : null;
            ReceiveMetricText.Text = session.Connected && mbps.HasValue ? $"{mbps:0.0}" : "—";
            double? loss = session.Role == RemoteSessionRole.Client ? telemetry.PacketLossPercent : fresh ? session.RemotePacketLossPercent : null;
            LossMetricText.Text = session.Connected && loss.HasValue ? $"{loss:0.0} %" : "—";
            StabilityMetricText.Text = !session.Connected ? "—" :
                session.Role == RemoteSessionRole.Host ? fresh ? "Feedback ativo" : "Sem feedback" :
                telemetry.PresenterAttached && fps > 0 ? "Apresentando" : "Aguardando vídeo";
            _sampleTick = now; _presented = session.RemotePresentedFrames;
            _sessionGeneration = session.SessionGeneration; _stream = session.StreamConfig?.Generation ?? 0;
        }

        private void ApplyProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_host.GetSnapshot().CanStop) return;
            string resolution = (ResolutionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "1080p";
            uint bitrate = uint.Parse((string)((ComboBoxItem)BitrateCombo.SelectedItem).Tag);
            _settings.SaveStreamResolution(resolution);
            _settings.SaveStreamProfile(uint.Parse((string)((ComboBoxItem)FpsCombo.SelectedItem).Tag), bitrate, uint.Parse((string)((ComboBoxItem)NvencCombo.SelectedItem).Tag), LowLatencyToggle.IsOn);
            ProfileHintText.Text = "Perfil salvo para a próxima transmissão. A qualidade depende também da GPU, da rede e do computador que recebe.";
            SafeUpdate();
        }

        private static void SelectValue(ComboBox combo, string value, string? label = null)
        {
            var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value);
            if (item is null) { item = new ComboBoxItem { Content = label ?? value, Tag = value }; combo.Items.Add(item); }
            combo.SelectedItem = item;
        }

        private void QualityPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_profileReady || _host.GetSnapshot().CanStop) return;
            string resolution = (QualityPresetCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "1080p";
            if (resolution == "custom") return;
            SelectValue(ResolutionCombo, resolution);
            SelectValue(FpsCombo, "60");
            SelectValue(BitrateCombo, resolution == "1440p" ? "35000" : resolution == "720p" ? "10000" : "20000");
            ProfileHintText.Text = "Prévisualização do perfil. Clique em Salvar perfil para aplicar.";
        }

        private async void CheckNetworkButton_Click(object sender, RoutedEventArgs e)
        {
            var lifetime = _pageCancellation;
            if (lifetime is null) return;
            CheckNetworkButton.IsEnabled = false;
            RouteTitleText.Text = "Verificando a rede…";
            try
            {
                var result = await TailscalePeerDiscoveryService.Instance.RefreshAsync(lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                var session = RemoteSessionService.Instance.GetSnapshot();
                var endpoint = RemoteSessionService.Instance.GetRemoteVideoEndpoint();
                var peer = result.Peers.FirstOrDefault(p => p.TailscaleIp == endpoint?.Address.ToString());
                RouteTitleText.Text = peer?.RouteText ?? (result.Available ? "Rede disponível" : "Conexão precisa de atenção");
                RouteDetailText.Text = result.Status;
                NetworkAdviceText.Text = peer is not null
                    ? $"{peer.DisplayName} • rota observada agora. Ela pode mudar durante a sessão."
                    : session.Connected ? "A sessão está conectada, mas a rota direta/relay não foi confirmada por esta consulta."
                    : "Conecte um PC autorizado em Amigos. Depois verifique novamente para identificar a rota da sessão.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) RouteDetailText.Text = ex.Message; }
            finally { if (!lifetime.IsCancellationRequested) CheckNetworkButton.IsEnabled = true; }
        }

        private void OpenAdvancedDiagnosticsButton_Click(object sender, RoutedEventArgs e) =>
            Frame.Navigate(typeof(DiagnosticsPage));

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StartButton.IsEnabled = false;
                await System.Threading.Tasks.Task.Run(() => _host.StartAsync());
            }
            catch (Exception ex)
            {
                TransmissionDetailText.Text = ex.Message;
            }
            finally
            {
                SafeUpdate();
            }
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StopButton.IsEnabled = false;
                await System.Threading.Tasks.Task.Run(() => _host.StopAsync());
            }
            catch (Exception ex)
            {
                TransmissionDetailText.Text = ex.Message;
            }
            finally
            {
                SafeUpdate();
            }
        }
    }
}
