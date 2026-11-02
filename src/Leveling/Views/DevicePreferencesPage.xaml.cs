using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Multiverse.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiverse.Views
{
    public sealed partial class DevicePreferencesPage : Page
    {
        private readonly MultiverseSettingsService _settings = MultiverseSettingsService.Instance;
        private HardwareSnapshot? _hardware;
        private bool _loading;

        public DevicePreferencesPage()
        {
            InitializeComponent();
            Loaded += DevicePreferencesPage_Loaded;
            Unloaded += DevicePreferencesPage_Unloaded;
        }

        private void DevicePreferencesPage_Loaded(object sender, RoutedEventArgs e)
        {
            FriendRequestPromptCoordinator.Instance.Attach(this);
            SafeRefresh();
        }

        private void DevicePreferencesPage_Unloaded(object sender, RoutedEventArgs e) =>
            FriendRequestPromptCoordinator.Instance.Detach(this);

        private void SafeRefresh()
        {
            try
            {
                _loading = true;
                _hardware = NativeCoreService.ReadHardware();
                IReadOnlyList<MonitorInfo> monitors = _hardware.Monitors
                    .Where(item => item.DesktopDuplicationReady)
                    .OrderByDescending(item => item.IsPrimary)
                    .ThenBy(item => item.DeviceName)
                    .ToArray();

                MonitorComboBox.ItemsSource = monitors;

                MultiverseStreamProfile profile = _settings.GetStreamProfile();
                MonitorInfo? selected = monitors.FirstOrDefault(item =>
                    !string.IsNullOrWhiteSpace(profile.MonitorDeviceName) &&
                    string.Equals(item.DeviceName, profile.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
                selected ??= monitors.FirstOrDefault(item => item.IsPrimary);
                selected ??= monitors.FirstOrDefault();
                MonitorComboBox.SelectedItem = selected;

                CoreStatusText.Text = _hardware.NativeCoreLoaded
                    ? $"CORE {(_hardware.ApiVersion > 0 ? $"API {_hardware.ApiVersion}" : "carregado")}"
                    : "CORE indisponível";
                GpuSummaryText.Text = _hardware.Gpus.Count == 0
                    ? (_hardware.Error ?? "Nenhuma GPU listada pelo núcleo nativo.")
                    : string.Join(" • ", _hardware.Gpus.Select(gpu => $"{gpu.Name} ({gpu.DedicatedMemoryGb:0.#} GB)"));
                ProfileSummaryText.Text = $"{profile.Fps} FPS • {profile.BitrateKbps / 1000.0:0.#} Mbps";
                StatusText.Text = monitors.Count == 0
                    ? "Nenhum monitor compatível com captura foi encontrado."
                    : "Selecione o monitor e clique em Salvar monitor.";
                UpdateMonitorDetail();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Não foi possível listar os dispositivos: {ex.Message}";
                CoreStatusText.Text = "Falha ao consultar o CORE";
                GpuSummaryText.Text = "A sessão atual não foi encerrada.";
            }
            finally
            {
                _loading = false;
            }
        }

        private void MonitorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
                return;
            UpdateMonitorDetail();
            StatusText.Text = "Alteração pendente. Clique em Salvar monitor.";
        }

        private void UpdateMonitorDetail()
        {
            if (MonitorComboBox.SelectedItem is not MonitorInfo monitor)
            {
                MonitorDetailText.Text = "Nenhum monitor selecionado.";
                SaveButton.IsEnabled = false;
                return;
            }

            SaveButton.IsEnabled = true;
            MonitorDetailText.Text = $"{monitor.Width} × {monitor.Height} • {monitor.RefreshRate} Hz • {monitor.DeviceName}" +
                (monitor.IsPrimary ? " • principal" : "");
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => SafeRefresh();

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (MonitorComboBox.SelectedItem is not MonitorInfo monitor)
                {
                    StatusText.Text = "Selecione um monitor antes de salvar.";
                    return;
                }

                _settings.SaveMonitor(monitor.DeviceName);
                StatusText.Text = $"Monitor salvo: {monitor.DeviceName}. Será usado na próxima transmissão iniciada.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Falha ao salvar o monitor: {ex.Message}";
            }
        }
    }
}
