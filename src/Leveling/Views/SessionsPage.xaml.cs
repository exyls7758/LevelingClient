using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Multiverse.Services;
using System;

namespace Multiverse.Views
{
    /// <summary>
    /// CORE 12 Audio page. This page only exposes the already validated
    /// CORE 11.21 audio service; it does not own capture, codec or transport.
    /// </summary>
    public sealed partial class SessionsPage : Page
    {
        private readonly RemoteAudioService _audio = RemoteAudioService.Instance;
        private readonly RemoteSessionService _session = RemoteSessionService.Instance;
        private readonly DispatcherTimer _uiTimer = new();
        private bool _refreshingProcesses;
        private bool _refreshingOutputs;
        private bool _loadingClientUi;

        public SessionsPage()
        {
            InitializeComponent();
            _session.Start();
            _uiTimer.Interval = TimeSpan.FromMilliseconds(750);
            _uiTimer.Tick += (_, _) => UpdateUi();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            FriendRequestPromptCoordinator.Instance.Attach(this);
            _audio.AudioChanged += Audio_AudioChanged;
            _session.SessionChanged += Session_SessionChanged;
            RefreshAudioProcessList();
            RefreshAudioOutputDeviceList();
            UpdateUi();
            _uiTimer.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _uiTimer.Stop();
            FriendRequestPromptCoordinator.Instance.Detach(this);
            _audio.AudioChanged -= Audio_AudioChanged;
            _session.SessionChanged -= Session_SessionChanged;
        }

        private void Audio_AudioChanged(object? sender, EventArgs e) =>
            DispatcherQueue.TryEnqueue(UpdateUi);

        private void Session_SessionChanged(object? sender, EventArgs e) =>
            DispatcherQueue.TryEnqueue(UpdateUi);

        private void RefreshAudioProcessList()
        {
            RemoteAudioSnapshot snapshot = _audio.GetSnapshot();
            var items = _audio.EnumerateAudioProcesses();
            _refreshingProcesses = true;
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
            finally { _refreshingProcesses = false; }
        }

        private void RefreshAudioOutputDeviceList()
        {
            RemoteAudioSnapshot snapshot = _audio.GetSnapshot();
            var items = _audio.EnumerateOutputDevices();
            _refreshingOutputs = true;
            try
            {
                AudioOutputDeviceComboBox.ItemsSource = items;
                AudioOutputDeviceComboBox.SelectedItem = null;
                foreach (AudioOutputDeviceOption item in items)
                {
                    if (string.Equals(item.DeviceId, snapshot.SelectedOutputDeviceId, StringComparison.Ordinal))
                    {
                        AudioOutputDeviceComboBox.SelectedItem = item;
                        break;
                    }
                }
                if (AudioOutputDeviceComboBox.SelectedItem is null && items.Count > 0)
                    AudioOutputDeviceComboBox.SelectedItem = items[0];
            }
            finally { _refreshingOutputs = false; }
        }

        private void RefreshAudioProcessesButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshAudioProcessList();
            UpdateUi();
        }

        private void RefreshAudioOutputDevicesButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshAudioOutputDeviceList();
            UpdateUi();
        }

        private void AudioProcessComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshingProcesses) return;
            AudioProcessOption? selected = AudioProcessComboBox.SelectedItem as AudioProcessOption;
            _audio.SelectProcess(selected?.ProcessId, selected?.DisplayName);
            UpdateUi();
        }

        private void AudioOutputDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshingOutputs) return;
            if (AudioOutputDeviceComboBox.SelectedItem is not AudioOutputDeviceOption selected) return;
            _audio.SelectClientOutputDevice(selected.DeviceId, selected.DisplayName);
            UpdateUi();
        }

        private void RemoteAudioVolumeSlider_ValueChanged(
            object sender,
            Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loadingClientUi) return;
            int volume = (int)Math.Round(e.NewValue);
            _audio.SetClientVolumePercent(volume);
            RemoteAudioVolumeValueTextBlock.Text = $"{volume}%";
        }

        private async void ToggleRemoteAudioButton_Click(object sender, RoutedEventArgs e)
        {
            RemoteAudioSnapshot snapshot = _audio.GetSnapshot();
            if (snapshot.HostCaptureRunning)
            {
                _audio.StopHostCaptureOnly();
                UpdateUi();
                return;
            }

            ToggleRemoteAudioButton.IsEnabled = false;
            try { await _audio.StartSelectedAsync(); }
            finally { UpdateUi(); }
        }

        private void UpdateUi()
        {
            RemoteSessionSnapshot session = _session.GetSnapshot();
            RemoteAudioSnapshot audio = _audio.GetSnapshot();
            bool client = session.Connected && session.Role == RemoteSessionRole.Client;
            bool host = session.Connected && session.Role == RemoteSessionRole.Host;

            AudioProcessComboBox.IsEnabled = !client && !audio.HostCaptureRunning;
            RefreshAudioProcessesButton.IsEnabled = !client && !audio.HostCaptureRunning;
            AudioOutputDeviceComboBox.IsEnabled = client;
            RefreshAudioOutputDevicesButton.IsEnabled = client;
            RemoteAudioVolumeSlider.IsEnabled = client;

            _loadingClientUi = true;
            try
            {
                if (Math.Abs(RemoteAudioVolumeSlider.Value - audio.ClientVolumePercent) >= 0.5)
                    RemoteAudioVolumeSlider.Value = audio.ClientVolumePercent;
                RemoteAudioVolumeValueTextBlock.Text = $"{audio.ClientVolumePercent}%";
            }
            finally { _loadingClientUi = false; }

            ToggleRemoteAudioButton.IsEnabled = host &&
                (audio.HostCaptureRunning || audio.SelectedProcessId is not null);
            ToggleRemoteAudioButton.Content = audio.HostCaptureRunning
                ? "Parar áudio remoto"
                : "Transmitir áudio selecionado";

            if (client)
            {
                RemoteAudioStatusTextBlock.Text = audio.ClientPlaybackRunning
                    ? $"CLIENTE • RX {audio.ReceivedPackets} • buffer {audio.BufferedMs:0} ms • {audio.SelectedOutputDeviceName} • {audio.ClientVolumePercent}%"
                    : $"CLIENTE • {audio.StatusText}";
            }
            else if (audio.HostCaptureRunning)
            {
                RemoteAudioStatusTextBlock.Text = $"HOST • {audio.StatusText} • TX {audio.SentPackets}";
            }
            else if (!string.IsNullOrWhiteSpace(audio.LastError))
            {
                RemoteAudioStatusTextBlock.Text = audio.LastError;
            }
            else if (audio.SelectedProcessId is not null)
            {
                RemoteAudioStatusTextBlock.Text = $"Selecionado • {audio.SelectedProcessName}";
            }
            else
            {
                RemoteAudioStatusTextBlock.Text = "Selecione o aplicativo cujo som será transmitido.";
            }

            bool healthy = audio.HostCaptureRunning || (client && audio.ClientPlaybackRunning);
            RemoteAudioStatusTextBlock.Foreground = new SolidColorBrush(
                healthy ? ColorHelper.FromArgb(255, 239, 43, 86) : ColorHelper.FromArgb(255, 174, 182, 216));
        }
    }
}
