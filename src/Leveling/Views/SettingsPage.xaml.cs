using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Multiverse.Services;
using System;

namespace Multiverse.Views
{
    public sealed partial class SettingsPage : Page
    {
        private readonly MultiverseSettingsService _settings = MultiverseSettingsService.Instance;
        private bool _loading;
        private bool _showUpdates;

        public void ShowUpdates() {
            _showUpdates = true;
            if (IsLoaded) ScrollToUpdates();
        }
        private void ScrollToUpdates() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => UpdatesPanel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 1.0 }));

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += SettingsPage_Loaded;
            Unloaded += SettingsPage_Unloaded;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            FriendRequestPromptCoordinator.Instance.Attach(this);
            LoadFromSettings();
            if (_showUpdates) ScrollToUpdates();
        }

        private void SettingsPage_Unloaded(object sender, RoutedEventArgs e) =>
            FriendRequestPromptCoordinator.Instance.Detach(this);

        private void LoadFromSettings()
        {
            try
            {
                _loading = true;
                MultiverseStreamProfile profile = _settings.GetStreamProfile();
                MultiverseAppPreferences preferences = _settings.GetAppPreferences();

                SelectComboTag(FpsComboBox, profile.Fps.ToString());
                SelectComboTag(BitrateComboBox, profile.BitrateKbps.ToString());
                SelectComboTag(PresetComboBox, profile.Preset.ToString());
                UltraLowLatencyToggle.IsOn = profile.UltraLowLatency;
                AutoOpenClientWindowToggle.IsOn = preferences.AutoOpenClientWindow;
                AutoStartHostStreamToggle.IsOn = preferences.AutoStartHostStream;
                LowMotionToggle.IsOn = preferences.LowMotion;
                TransmissionHudToggle.IsOn = _settings.GetTransmissionHudVisible();
                HostTransmissionHudToggle.IsOn = _settings.GetHostTransmissionHudVisible();
                MinimizeToTrayToggle.IsOn = _settings.GetMinimizeToTrayOnClose();
                SelectComboTag(DiagnosticsLevelComboBox, preferences.DiagnosticsLevel);

                string audioName = _settings.GetString(
                    MultiverseSettingsService.AudioOutputDeviceNameKey,
                    "Padrão do Windows");
                int audioVolume = Math.Clamp(
                    _settings.GetInt(
                        MultiverseSettingsService.AudioClientVolumePercentKey,
                        100),
                    0,
                    200);
                AudioPreferenceText.Text = $"{audioName} • {audioVolume}%";
            }
            catch (Exception ex)
            {
                SaveStatusText.Text = $"Não foi possível carregar as configurações: {ex.Message}";
            }
            finally
            {
                _loading = false;
            }
        }

        private static void SelectComboTag(ComboBox combo, string tag)
        {
            foreach (object item in combo.Items)
            {
                if (item is ComboBoxItem comboItem &&
                    string.Equals(comboItem.Tag?.ToString(), tag, StringComparison.Ordinal))
                {
                    combo.SelectedItem = comboItem;
                    return;
                }
            }

            // Preserve a saved/custom profile even when it is not one of the
            // presets. Selecting the first item silently reduced 35 Mbps to
            // 5 Mbps when saving an unrelated application preference.
            var saved = new ComboBoxItem { Tag = tag, Content = $"Atual • {tag}" };
            combo.Items.Add(saved);
            combo.SelectedItem = saved;
        }

        private static uint ReadComboUInt(ComboBox combo, uint fallback)
        {
            if (combo.SelectedItem is ComboBoxItem item &&
                uint.TryParse(item.Tag?.ToString(), out uint value))
            {
                return value;
            }
            return fallback;
        }

        private void TransmissionHudToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;
            _settings.SetTransmissionHudVisible(TransmissionHudToggle.IsOn);
        }

        private void HostTransmissionHudToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;
            _settings.SetHostTransmissionHudVisible(HostTransmissionHudToggle.IsOn);
        }

        private void MinimizeToTrayToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;
            _settings.SetMinimizeToTrayOnClose(MinimizeToTrayToggle.IsOn);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            try
            {
                MultiverseStreamProfile current = _settings.GetStreamProfile();
                uint fps = ReadComboUInt(FpsComboBox, current.Fps);
                uint bitrate = ReadComboUInt(BitrateComboBox, current.BitrateKbps);
                uint preset = ReadComboUInt(PresetComboBox, current.Preset);

                _settings.SaveStreamProfile(
                    fps,
                    bitrate,
                    preset,
                    UltraLowLatencyToggle.IsOn,
                    current.PipelineMode);

                string diagnosticsLevel =
                    DiagnosticsLevelComboBox.SelectedItem is ComboBoxItem diagnosticsItem
                        ? diagnosticsItem.Tag?.ToString() ?? "Basic"
                        : "Basic";

                _settings.SaveAppPreferences(
                    LowMotionToggle.IsOn,
                    AutoOpenClientWindowToggle.IsOn,
                    AutoStartHostStreamToggle.IsOn,
                    diagnosticsLevel);

                SaveStatusText.Text = $"Salvo • {fps} FPS • {bitrate / 1000.0:0.#} Mbps";
            }
            catch (Exception ex)
            {
                SaveStatusText.Text = $"Falha ao salvar: {ex.Message}";
            }
        }

        private void OpenAudioButton_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(AudioPage));

        private void OpenControllerButton_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(ControllerPage));

        private void OpenDiagnosticsButton_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(DiagnosticsPage));

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _settings.ResetUserPreferences();
                GamepadTuningService.Instance.Reset();
                RemoteGamepadService.Instance.RebindVirtualController();
                LoadFromSettings();
                SaveStatusText.Text = "Preferências restauradas para os valores seguros padrão.";
            }
            catch (Exception ex)
            {
                SaveStatusText.Text = $"Falha ao restaurar: {ex.Message}";
            }
        }
    }
}
