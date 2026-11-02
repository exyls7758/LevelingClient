using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Multiverse.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Multiverse.Views
{
    public sealed partial class AudioPage : Page
    {
        private readonly RemoteAudioService _audio = RemoteAudioService.Instance;
        private bool _updatingUi;

        public AudioPage()
        {
            InitializeComponent();
            Loaded += AudioPage_Loaded;
            Unloaded += AudioPage_Unloaded;
        }

        private void AudioPage_Loaded(object sender, RoutedEventArgs e)
        {
            FriendRequestPromptCoordinator.Instance.Attach(this);
            _audio.AudioChanged += Audio_AudioChanged;
            SafeRefreshAll();
        }

        private void AudioPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _audio.AudioChanged -= Audio_AudioChanged;
            FriendRequestPromptCoordinator.Instance.Detach(this);
        }

        private void Audio_AudioChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(SafeUpdateUi);
        }

        private void SafeRefreshAll()
        {
            try
            {
                _updatingUi = true;
                RefreshProcesses();
                RefreshOutputs();
                RefreshCaptureSettings();
                SafeUpdateUi();
            }
            catch (Exception ex)
            {
                ShowPageError(ex);
            }
            finally
            {
                _updatingUi = false;
            }
        }

        private void RefreshProcesses()
        {
            RemoteAudioSnapshot snapshot = _audio.GetSnapshot();
            IReadOnlyList<AudioProcessOption> items = _audio.EnumerateAudioProcesses();
            ProcessComboBox.ItemsSource = items;
            AudioProcessOption? selected = items.FirstOrDefault(
                item => item.ProcessId == snapshot.SelectedProcessId);
            if (selected is null && !string.IsNullOrWhiteSpace(snapshot.SelectedProcessName))
            {
                selected = items.FirstOrDefault(item => string.Equals(
                    AudioApplicationPolicy.NormalizeExecutable(item.ProcessName),
                    AudioApplicationPolicy.NormalizeExecutable(snapshot.SelectedProcessName),
                    StringComparison.OrdinalIgnoreCase));
                if (selected is not null)
                    _audio.SelectProcess(selected.ProcessId, selected.ProcessName);
            }
            ProcessComboBox.SelectedItem = selected;
        }

        private void RefreshOutputs()
        {
            RemoteAudioSnapshot snapshot = _audio.GetSnapshot();
            IReadOnlyList<AudioOutputDeviceOption> items = _audio.EnumerateOutputDevices();
            OutputDeviceComboBox.ItemsSource = items;
            OutputDeviceComboBox.SelectedItem = items.FirstOrDefault(
                item => string.Equals(
                    item.DeviceId,
                    snapshot.SelectedOutputDeviceId,
                    StringComparison.Ordinal)) ?? items.FirstOrDefault();
        }

        private void RefreshCaptureSettings()
        {
            AudioCaptureMode mode = _audio.CaptureMode;
            foreach (object item in CaptureModeComboBox.Items)
            {
                if (item is ComboBoxItem comboItem &&
                    string.Equals(comboItem.Tag?.ToString(), mode.ToString(), StringComparison.Ordinal))
                {
                    CaptureModeComboBox.SelectedItem = comboItem;
                    break;
                }
            }
            RefreshExclusions();
            UpdateModeUi(mode);
        }

        private void RefreshExclusions()
        {
            ExclusionsList.ItemsSource = _audio.GetAutomaticExclusions().ToArray();
        }

        private void UpdateModeUi(AudioCaptureMode mode)
        {
            bool automatic = mode == AudioCaptureMode.AutomaticGamesAndMedia;
            ManualAudioPanel.Visibility = automatic ? Visibility.Collapsed : Visibility.Visible;
            AutomaticAudioPanel.Visibility = automatic ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SafeUpdateUi()
        {
            try
            {
                RemoteAudioSnapshot snapshot = _audio.GetSnapshot();
                _updatingUi = true;
                UpdateModeUi(snapshot.CaptureMode);
                VolumeSlider.Value = snapshot.ClientVolumePercent;
                VolumeValueText.Text = $"{snapshot.ClientVolumePercent}%";
                bool automatic = snapshot.CaptureMode == AudioCaptureMode.AutomaticGamesAndMedia;
                HostAudioButton.IsEnabled = !automatic;
                HostAudioButton.Content = automatic
                    ? snapshot.HostCaptureRunning
                        ? "Automático ativo • acompanha transmissão"
                        : "Automático • acompanha transmissão"
                    : snapshot.HostCaptureRunning
                        ? "Parar áudio do host"
                        : "Iniciar áudio";
                AudioStatusText.Text = string.IsNullOrWhiteSpace(snapshot.StatusText)
                    ? "Áudio parado"
                    : snapshot.StatusText;
                VoicePrivacyStatusText.Text = string.IsNullOrWhiteSpace(snapshot.VoicePrivacyStatus)
                    ? "PRIVACIDADE DE VOZ • verificando..."
                    : snapshot.VoicePrivacyStatus;
                VoicePrivacyDetailText.Text = snapshot.VoicePrivacyFailClosed
                    ? (string.IsNullOrWhiteSpace(snapshot.VoicePrivacyError)
                        ? "Nenhuma fonte automática será transmitida até a verificação voltar."
                        : $"Nenhuma fonte automática será transmitida até a verificação voltar. {snapshot.VoicePrivacyError}")
                    : snapshot.VoicePrivacyBlockedSources > 0
                        ? $"{snapshot.VoicePrivacyBlockedSources} fonte(s) de comunicação/microfone bloqueada(s) nesta verificação."
                        : "Sessões de entrada verificadas; comunicação permanece na lista obrigatória de bloqueio.";
                AudioDetailText.Text = string.IsNullOrWhiteSpace(snapshot.LastError)
                    ? $"TX {snapshot.SentPackets:N0} • RX {snapshot.ReceivedPackets:N0} • perdas {snapshot.DroppedPackets:N0} • buffer {snapshot.BufferedMs:0} ms"
                    : snapshot.LastError;
            }
            catch (Exception ex)
            {
                ShowPageError(ex);
            }
            finally
            {
                _updatingUi = false;
            }
        }

        private void CaptureModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingUi)
                return;

            try
            {
                if (CaptureModeComboBox.SelectedItem is ComboBoxItem item &&
                    Enum.TryParse(item.Tag?.ToString(), out AudioCaptureMode mode))
                {
                    _audio.SetCaptureMode(mode);
                    UpdateModeUi(mode);
                }
            }
            catch (Exception ex)
            {
                ShowPageError(ex);
            }
        }

        private void AddExclusionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string value = ExclusionExecutableTextBox.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(value))
                    return;

                var items = _audio.GetAutomaticExclusions().ToList();
                items.Add(value);
                _audio.SetAutomaticExclusions(items);
                ExclusionExecutableTextBox.Text = "";
                RefreshExclusions();
            }
            catch (Exception ex)
            {
                ShowPageError(ex);
            }
        }

        private void RemoveExclusionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ExclusionsList.SelectedItem is not string selected)
                    return;

                var items = _audio.GetAutomaticExclusions()
                    .Where(x => !string.Equals(x, selected, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                _audio.SetAutomaticExclusions(items);
                RefreshExclusions();
            }
            catch (Exception ex)
            {
                ShowPageError(ex);
            }
        }

        private void ProcessComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingUi)
                return;

            try
            {
                if (ProcessComboBox.SelectedItem is AudioProcessOption option)
                    _audio.SelectProcess(option.ProcessId, option.ProcessName);
                else
                    _audio.SelectProcess(null);
            }
            catch (Exception ex)
            {
                ShowPageError(ex);
            }
        }

        private void OutputDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingUi)
                return;

            try
            {
                if (OutputDeviceComboBox.SelectedItem is AudioOutputDeviceOption option)
                    _audio.SelectClientOutputDevice(option.DeviceId, option.DisplayName);
            }
            catch (Exception ex)
            {
                ShowPageError(ex);
            }
        }

        private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_updatingUi)
                return;

            try
            {
                int value = (int)Math.Round(e.NewValue);
                VolumeValueText.Text = $"{value}%";
                _audio.SetClientVolumePercent(value);
            }
            catch (Exception ex)
            {
                ShowPageError(ex);
            }
        }

        private async void HostAudioButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                HostAudioButton.IsEnabled = false;
                RemoteAudioSnapshot snapshot = _audio.GetSnapshot();
                if (snapshot.HostCaptureRunning)
                    _audio.StopHostCaptureOnly();
                else
                    await _audio.StartSelectedAsync();
            }
            catch (Exception ex)
            {
                ShowPageError(ex);
            }
            finally
            {
                HostAudioButton.IsEnabled = true;
                SafeUpdateUi();
            }
        }

        private void RefreshProcessesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _updatingUi = true;
                RefreshProcesses();
            }
            catch (Exception ex)
            {
                ShowPageError(ex);
            }
            finally
            {
                _updatingUi = false;
            }
        }

        private void StopAllAudioButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _audio.StopAudioOnly();
                SafeUpdateUi();
            }
            catch (Exception ex)
            {
                ShowPageError(ex);
            }
        }

        private void ShowPageError(Exception ex)
        {
            AudioStatusText.Text = "A página de áudio encontrou um problema, mas a sessão continua ativa.";
            AudioDetailText.Text = ex.Message;
        }
    }
}
