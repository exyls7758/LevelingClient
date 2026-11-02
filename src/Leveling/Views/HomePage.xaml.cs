using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Multiverse.Services;
using Multiverse.Models;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace Multiverse.Views
{
    public sealed partial class HomePage : Page
    {
        private readonly RemoteSessionService _session = RemoteSessionService.Instance;
        private RemoteStreamingRuntime StreamRuntime => RemoteStreamingRuntime.Instance;
        private readonly HostVideoPipelineController _host = HostTransmissionService.Instance;
        private readonly MultiverseSettingsService _settings = MultiverseSettingsService.Instance;
        private readonly FriendConnectionService _friends = FriendConnectionService.Instance;
        private readonly FriendPresenceService _presence = FriendPresenceService.Instance;
        private readonly DispatcherTimer _uiTimer = new();
        private bool _pageLoaded;
        private bool _connectBusy;
        private bool _viewReady;
        private bool _loadingQuickQuality;

        public HomePage()
        {
            InitializeComponent();
            _viewReady = true;
            _session.Start();
            _uiTimer.Interval = TimeSpan.FromMilliseconds(500);
            _uiTimer.Tick += UiTimer_Tick;
            Loaded += HomePage_Loaded;
            Unloaded += HomePage_Unloaded;
            SafeUpdateSessionUi();
        }

        private void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            _pageLoaded = true;
            LoadQuickQuality();
            SetComputerLayout(false);
            _session.SessionChanged += Session_SessionChanged;
            _session.StreamConfigChanged += Session_StreamConfigChanged;
            _host.StateChanged += Host_StateChanged;
            _presence.PresenceChanged += Presence_PresenceChanged;
            _presence.AddConsumer(FriendPresenceRefreshMode.Home);
            FriendRequestPromptCoordinator.Instance.Attach(this);
            _ = RefreshHomePresenceAsync();
            SafeUpdateSessionUi();
        }

        private void HomePage_Unloaded(object sender, RoutedEventArgs e)
        {
            _pageLoaded = false;
            _uiTimer.Stop();
            _session.SessionChanged -= Session_SessionChanged;
            _session.StreamConfigChanged -= Session_StreamConfigChanged;
            _host.StateChanged -= Host_StateChanged;
            _presence.PresenceChanged -= Presence_PresenceChanged;
            _presence.RemoveConsumer(FriendPresenceRefreshMode.Home);
            FriendRequestPromptCoordinator.Instance.Detach(this);
        }

        private void Presence_PresenceChanged(object? sender, EventArgs e) =>
            DispatcherQueue.TryEnqueue(RenderHomeFriends);

        private async Task RefreshHomePresenceAsync()
        {
            try
            {
                await _presence.RefreshAsync();
                RenderHomeFriends();
            }
            catch
            {
                RenderHomeFriends();
            }
        }

        private void RenderHomeFriends()
        {
            FriendPresenceSnapshot snapshot = _presence.GetSnapshot();
            if (!_viewReady) return;
            string query = DeviceSearchBox.Text.Trim();
            int filter = DeviceFilterCombo.SelectedIndex;
            MultiverseFriend[] online = snapshot.Friends
                .Where(friend => !friend.Blocked && friend.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    && (filter == 0 || (filter == 1 ? friend.Online : !friend.Online)))
                .OrderByDescending(friend => friend.Online)
                .ThenBy(friend => friend.DisplayName)
                .ToArray();
            HomeOnlineFriendsList.ItemsSource = online;
            EmptyComputersCard.Visibility = online.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            HomeNoOnlineFriendsText.Text = query.Length > 0 || filter != 0
                ? "Nenhum computador corresponde ao filtro."
                : "Seus outros computadores aparecem aqui.";
            var local = _session.GetSnapshot();
            LocalRowNameText.Text = local.LocalName;
            LocalRowStatusText.Text = local.Listening ? "Online • Este computador" : "Indisponível • Este computador";
            
            var savedQuality = _settings.GetStreamProfile();
            HomeSavedQualityText.Text = $"Até {savedQuality.MaxHeight}p • {savedQuality.Fps} FPS • {savedQuality.BitrateKbps / 1000.0:0.#} Mbps";
            LocalComputerCard.Visibility = local.LocalName.Contains(query, StringComparison.OrdinalIgnoreCase)
                && (filter == 0 || (filter == 1 ? local.Listening : !local.Listening))
                ? Visibility.Visible : Visibility.Collapsed;
            HomeComputerCountText.Text = $"Seus computadores ({online.Length + (LocalComputerCard.Visibility == Visibility.Visible ? 1 : 0)})";
        }

        private void DeviceSearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (_viewReady) RenderHomeFriends(); }
        private void DeviceFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_viewReady) RenderHomeFriends(); }
        private void LoadQuickQuality()
        {
            _loadingQuickQuality = true;
            try
            {
                var profile = _settings.GetStreamProfile();
                SelectQuickValue(HomeResolutionCombo, $"{profile.MaxHeight}p");
                SelectQuickValue(HomeFpsCombo, profile.Fps.ToString());
                SelectQuickValue(HomeBitrateCombo, profile.BitrateKbps.ToString(), $"{profile.BitrateKbps / 1000.0:0.#} Mbps");
            }
            finally { _loadingQuickQuality = false; }
        }
        private static void SelectQuickValue(ComboBox combo, string value, string? label = null)
        {
            var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value);
            if (item is null) { item = new ComboBoxItem { Content = label ?? value, Tag = value }; combo.Items.Add(item); }
            combo.SelectedItem = item;
        }
        private void HomeQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_pageLoaded || _loadingQuickQuality) return;
            if (_host.GetSnapshot().CanStop) { LoadQuickQuality(); return; }
            if (HomeResolutionCombo.SelectedItem is not ComboBoxItem resolution ||
                HomeFpsCombo.SelectedItem is not ComboBoxItem fps || HomeBitrateCombo.SelectedItem is not ComboBoxItem bitrate) return;
            var profile = _settings.GetStreamProfile();
            _settings.SaveStreamResolution((string)resolution.Tag);
            _settings.SaveStreamProfile(uint.Parse((string)fps.Tag), uint.Parse((string)bitrate.Tag), profile.Preset, profile.UltraLowLatency);
            RenderHomeFriends();
            SafeUpdateSessionUi();
        }

        private void ListViewButton_Click(object sender, RoutedEventArgs e) => SetComputerLayout(false);
        private void GridViewButton_Click(object sender, RoutedEventArgs e) => SetComputerLayout(true);
        private void SetComputerLayout(bool grid)
        {
            HomeOnlineFriendsList.ItemsPanel = (ItemsPanelTemplate)Resources[grid ? "ComputerGridPanel" : "ComputerRowsPanel"];
            HomeOnlineFriendsList.ItemTemplate = (DataTemplate)Resources[grid ? "ComputerGridTemplate" : "ComputerRowTemplate"];
            ListViewButton.IsEnabled = grid;
            GridViewButton.IsEnabled = !grid;
        }

        private void AddComputerButton_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(FriendsPage));
        private void OpenTransmissionButton_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(TransmissionPage));

        private async void HomeFriendConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_connectBusy || sender is not Button button || button.DataContext is not MultiverseFriend friend)
                return;

            _connectBusy = true;
            button.IsEnabled = false;
            try
            {
                RemoteConnectResult result = await _friends.ConnectAsync(friend, friend.LastKnownIp);
                string message = result.Message;
                if (!result.Success && message.Contains("Entrada direta", StringComparison.OrdinalIgnoreCase))
                    message = "Entrada direta não liberada por este amigo.";
                await ShowConnectionFeedbackAsync(message);
                if (!result.Success)
                    return;

                if (await StreamRuntime.EnsureClientStreamingAsync())
                {
                    MultiverseAppPreferences preferences = _settings.GetAppPreferences();
                    if (preferences.AutoOpenClientWindow)
                        StreamRuntime.ShowSessionWindow();
                }
            }
            catch (Exception ex)
            {
                await ShowConnectionFeedbackAsync($"Não foi possível conectar ao amigo: {ex.Message}");
            }
            finally
            {
                _connectBusy = false;
                button.IsEnabled = true;
                SafeUpdateSessionUi();
            }
        }

        private void Session_SessionChanged(object? sender, EventArgs e) =>
            DispatcherQueue.TryEnqueue(SafeUpdateSessionUi);

        private void Host_StateChanged(object? sender, EventArgs e) =>
            DispatcherQueue.TryEnqueue(SafeUpdateSessionUi);

        private void Session_StreamConfigChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    RemoteSessionSnapshot snapshot = _session.GetSnapshot();
                    if (!snapshot.Connected || snapshot.Role != RemoteSessionRole.Client)
                        return;

                    if (await StreamRuntime.EnsureClientStreamingAsync())
                    {
                        MultiverseAppPreferences preferences = _settings.GetAppPreferences();
                        if (preferences.AutoOpenClientWindow)
                            StreamRuntime.ShowSessionWindow();
                    }
                }
                catch
                {
                    // A client-window failure must not terminate the session or app.
                }
            });
        }

        private void UiTimer_Tick(object? sender, object e) => SafeUpdateSessionUi();

        private void SafeUpdateSessionUi()
        {
            try
            {
                UpdateSessionUi();
            }
            catch (Exception ex)
            {
                TopStatusText.Text = "UI recuperada";
                ConnectionStateTextBlock.Text = $"A sessão continua ativa. {ex.Message}";
            }
        }


        private void UpdateUiPolling(RemoteSessionSnapshot snapshot, HostTransmissionSnapshot host)
        {
            if (!_pageLoaded)
                return;

            bool needsLiveTelemetry = snapshot.Connected || host.Armed;
            if (needsLiveTelemetry)
            {
                if (!_uiTimer.IsEnabled)
                    _uiTimer.Start();
            }
            else if (_uiTimer.IsEnabled)
            {
                _uiTimer.Stop();
            }
        }

        private void UpdateSessionUi()
        {
            RemoteSessionSnapshot snapshot = _session.GetSnapshot();
            HostTransmissionSnapshot host = _host.GetSnapshot();
            UpdateUiPolling(snapshot, host);

            LocalPcNameTextBlock.Text = snapshot.LocalName;
            var profile = _settings.GetStreamProfile();
            LocalProfileText.Text = $"Windows • perfil até {profile.MaxHeight}p / {profile.Fps} FPS";
            LocalMachineCodeTextBlock.Text = snapshot.LocalCode;
            LocalDirectEndpointTextBlock.Text = snapshot.LocalDirectAddress;
            LocalTailscaleIpTextBlock.Text = string.IsNullOrWhiteSpace(snapshot.CurrentTailscaleIp)
                ? "Tailscale indisponível"
                : snapshot.CurrentTailscaleIp;
            LocalTailscaleInterfaceTextBlock.Text = string.IsNullOrWhiteSpace(snapshot.TailscaleInterface)
                ? ""
                : $"Interface: {snapshot.TailscaleInterface}";
            LocalListenerStateTextBlock.Text = snapshot.Listening ? "PRONTO" : "INDISPONÍVEL";
            LocalListenerStateTextBlock.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                snapshot.Listening ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 228, 154) : Microsoft.UI.ColorHelper.FromArgb(255, 255, 155, 184));
            TopStatusText.Text = snapshot.Connected ? "Conectado" : snapshot.Listening ? "Pronto" : "Offline";
            OpenVideoButton.Visibility = snapshot.Connected && snapshot.Role == RemoteSessionRole.Client ? Visibility.Visible : Visibility.Collapsed;
            VideoStartupStatusText.Text = !string.IsNullOrWhiteSpace(snapshot.VideoStartupError) ? snapshot.VideoStartupError :
                snapshot.Connected && snapshot.Role == RemoteSessionRole.Client && snapshot.StreamConfig is null
                    ? "Conectado. No computador host, clique em Transmitir agora para iniciar o vídeo." : "";
            VideoStartupStatusText.Visibility = VideoStartupStatusText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            TransmissionButton.Content = host.CanStop ? "Parar transmissão" : "Transmitir agora";
            TransmissionButton.IsEnabled = !snapshot.Connected || snapshot.Role == RemoteSessionRole.Host;
            LocalRowTransmitButton.Content = host.CanStop ? "Parar" : "Transmitir";
            LocalRowTransmitButton.IsEnabled = TransmissionButton.IsEnabled;
            HomeResolutionCombo.IsEnabled = HomeFpsCombo.IsEnabled = HomeBitrateCombo.IsEnabled = !host.CanStop;
            TransmissionStateText.Text = host.StatusText;

            if (snapshot.Connected)
            {
                ConnectionButton.Content = "Desconectar";
                ConnectionStateTextBlock.Text = snapshot.Role == RemoteSessionRole.Host
                    ? $"HOST • {snapshot.RemoteName} • {snapshot.RemoteAddress}"
                    : $"CLIENTE • {snapshot.RemoteName} • {snapshot.RemoteAddress}";

                HomeLatencyValueTextBlock.Text = snapshot.RoundTripMs > 0 ? $"{snapshot.RoundTripMs:0.0} ms" : "-- ms";
                HomeLatencyCaptionTextBlock.Text = snapshot.BaseRoundTripMs > 0
                    ? $"base {snapshot.BaseRoundTripMs:0.0} • jitter {snapshot.JitterMs:0.0} ms"
                    : "RTT da sessão";

                RemoteStreamConfig? config = snapshot.StreamConfig;
                bool freshFeedback = snapshot.RemoteTelemetryAgeMs >= 0 && snapshot.RemoteTelemetryAgeMs < 2000;
                double? observedFps = snapshot.Role == RemoteSessionRole.Client
                    ? RemotePipelineTelemetry.Instance.GetSnapshot().PresentFps
                    : freshFeedback ? snapshot.RemoteVideoFps : null;
                HomeFpsValueTextBlock.Text = observedFps.HasValue ? $"{observedFps:0.0} FPS" : "-- FPS";
                HomeFpsCaptionTextBlock.Text = snapshot.Role == RemoteSessionRole.Host
                    ? "Recebimento medido no cliente" : "Apresentação medida";
                HomeBitrateValueTextBlock.Text = config?.BitrateKbps > 0
                    ? $"{config.BitrateKbps / 1000.0:0.#} Mbps"
                    : "-- Mbps";
                HomeBitrateCaptionTextBlock.Text = config is null
                    ? "Aguardando stream"
                    : $"{config.Width} × {config.Height}";

                double loss = snapshot.RemotePacketLossPercent;
                double rtt = snapshot.RoundTripMs;
                double jitter = snapshot.JitterMs;
                string quality = loss >= 3.0 || rtt >= 350 || jitter >= 100
                    ? "Ruim"
                    : loss >= 1.0 || rtt >= 180 || jitter >= 45
                        ? "Instável"
                        : loss >= 0.20 || rtt >= 100 || jitter >= 22
                            ? "Boa"
                            : "Excelente";
                HomeQualityValueTextBlock.Text = freshFeedback ? quality : "Sem amostra";
                HomeQualityCaptionTextBlock.Text = loss > 0
                    ? $"perda {loss:0.00}% • fila {snapshot.QueueDelayMs:0.0} ms"
                    : "Sessão conectada";

                RecentSessionText.Text = string.IsNullOrWhiteSpace(snapshot.RemoteName)
                    ? "PC remoto"
                    : snapshot.RemoteName;
                RecentSessionDetailText.Text = $"Conectado agora • {snapshot.RemoteAddress}";
                ReconnectButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ConnectionButton.Content = "Conectar";
                ConnectionStateTextBlock.Text = string.IsNullOrWhiteSpace(snapshot.LastError)
                    ? snapshot.StatusText
                    : snapshot.LastError;
                HomeLatencyValueTextBlock.Text = "-- ms";
                HomeLatencyCaptionTextBlock.Text = "Sem conexão";
                HomeFpsValueTextBlock.Text = "-- FPS";
                HomeFpsCaptionTextBlock.Text = "Aguardando transmissão";
                HomeBitrateValueTextBlock.Text = "-- Mbps";
                HomeBitrateCaptionTextBlock.Text = "Sem dados";
                HomeQualityValueTextBlock.Text = "Offline";
                HomeQualityCaptionTextBlock.Text = "Rede";
                UpdateRecentSessionCard();
            }
        }

        private void UpdateRecentSessionCard()
        {
            string target = _settings.GetString(MultiverseSettingsService.LastRemoteTargetKey, "");
            string name = _settings.GetString(MultiverseSettingsService.LastRemoteNameKey, "");
            if (string.IsNullOrWhiteSpace(target))
            {
                RecentSessionText.Text = "Nenhuma sessão recente";
                RecentSessionDetailText.Text = "Conecte-se a um PC para vê-lo aqui.";
                ReconnectButton.Visibility = Visibility.Collapsed;
                return;
            }

            RecentSessionText.Text = string.IsNullOrWhiteSpace(name) ? "PC remoto" : name;
            RecentSessionDetailText.Text = target;
            ReconnectButton.Visibility = Visibility.Visible;
        }

        private async void CopyLocalCodeButton_Click(object sender, RoutedEventArgs e) =>
            await CopyWithButtonFeedbackAsync(CopyLocalCodeButton, LocalMachineCodeTextBlock.Text);

        private async void CopyDirectEndpointButton_Click(object sender, RoutedEventArgs e) =>
            await CopyWithButtonFeedbackAsync(CopyDirectEndpointButton, LocalDirectEndpointTextBlock.Text);

        private static async Task CopyWithButtonFeedbackAsync(Button button, string text)
        {
            object original = button.Content;
            button.IsEnabled = false;
            try
            {
                var package = new DataPackage();
                package.SetText(text ?? "");
                Clipboard.SetContent(package);
                Clipboard.Flush();
                button.Content = "Copiado";
                await Task.Delay(700);
            }
            catch
            {
                button.Content = "Erro";
                await Task.Delay(700);
            }
            finally
            {
                button.Content = original;
                button.IsEnabled = true;
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_connectBusy)
                return;

            RemoteSessionSnapshot snapshot = _session.GetSnapshot();
            if (snapshot.Connected)
            {
                _connectBusy = true;
                ConnectionButton.IsEnabled = false;
                try
                {
                    await _session.DisconnectAsync();
                    await ShowConnectionFeedbackAsync("Sessão desconectada.");
                }
                catch (Exception ex)
                {
                    await ShowConnectionFeedbackAsync(ex.Message);
                }
                finally
                {
                    _connectBusy = false;
                    ConnectionButton.IsEnabled = true;
                    SafeUpdateSessionUi();
                }
                return;
            }

            await ConnectToTargetAsync(ConnectCodeEntryTextBox.Text?.Trim() ?? "");
        }

        private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
        {
            string target = _settings.GetString(MultiverseSettingsService.LastRemoteTargetKey, "");
            await ConnectToTargetAsync(target);
        }

        private async Task ConnectToTargetAsync(string target)
        {
            if (_connectBusy)
                return;
            if (string.IsNullOrWhiteSpace(target))
            {
                await ShowConnectionFeedbackAsync("Digite o código ou endereço do PC remoto.");
                return;
            }

            _connectBusy = true;
            ConnectionButton.IsEnabled = false;
            ReconnectButton.IsEnabled = false;
            ConnectCodeTextBox.Text = target;
            try
            {
                await PlayConnectFeedbackAsync();
                RemoteConnectResult result = await _session.ConnectAsync(target);
                await ShowConnectionFeedbackAsync(result.Message);
                if (!result.Success)
                    return;

                ConnectCodeEntryTextBox.Text = "";
                ConnectCodeTextBox.Text = "";
                RemoteSessionSnapshot connected = _session.GetSnapshot();
                _settings.SaveRecentSession(target, connected.RemoteName);

                if (connected.Role == RemoteSessionRole.Client &&
                    await StreamRuntime.EnsureClientStreamingAsync())
                {
                    MultiverseAppPreferences preferences = _settings.GetAppPreferences();
                    if (preferences.AutoOpenClientWindow)
                        StreamRuntime.ShowSessionWindow();
                }
            }
            catch (Exception ex)
            {
                await ShowConnectionFeedbackAsync($"Falha de conexão: {ex.Message}");
            }
            finally
            {
                _connectBusy = false;
                ConnectionButton.IsEnabled = true;
                ReconnectButton.IsEnabled = true;
                SafeUpdateSessionUi();
            }
        }

        private async void TransmissionButton_Click(object sender, RoutedEventArgs e)
        {
            TransmissionButton.IsEnabled = false;
            LocalRowTransmitButton.IsEnabled = false;
            try
            {
                HostTransmissionSnapshot state = _host.GetSnapshot();
                if (state.CanStop)
                    await System.Threading.Tasks.Task.Run(() => _host.StopAsync());
                else
                    await System.Threading.Tasks.Task.Run(() => _host.StartAsync());
            }
            catch (Exception ex)
            {
                ConnectionStateTextBlock.Text = $"Falha ao alterar transmissão: {ex.Message}";
            }
            finally
            {
                SafeUpdateSessionUi();
            }
        }

        private async void OpenVideoButton_Click(object sender, RoutedEventArgs e)
        {
            OpenVideoButton.IsEnabled = false;
            try
            {
                if (await StreamRuntime.EnsureClientStreamingAsync())
                    await StreamRuntime.ShowSessionWindowAsync();
                else await ShowConnectionFeedbackAsync(_session.GetSnapshot().VideoStartupError is { Length: > 0 } reason
                    ? reason : "Aguardando o host iniciar a transmissão.");
            }
            catch (Exception ex) { await ShowConnectionFeedbackAsync("Falha ao abrir vídeo: " + ex.Message); }
            finally { OpenVideoButton.IsEnabled = true; }
        }

        private Task PlayConnectFeedbackAsync()
        {
            // Feedback must not add an animation delay to the connection path.
            ConnectFxBorder.Visibility = Visibility.Collapsed;
            return Task.CompletedTask;
        }

        private Task ShowConnectionFeedbackAsync(string message)
        {
            ConnectionFeedbackText.Text = message ?? "";
            ConnectFxBorder.Visibility = Visibility.Visible;
            ConnectFxBorder.Opacity = 1;
            return Task.CompletedTask;
        }
    }
}
