using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Multiverse.Views;
using Multiverse.Services;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Multiverse
{
    public sealed partial class MainWindow : Window
    {
        private AppWindow? _appWindow;
        private readonly MultiverseSettingsService _settings;
        private WindowsTrayIconService? _trayIcon;
        private IntPtr _mainHwnd;
        private bool _allowClose;
        private bool _exitCleanupDone;
        private bool _sidebarManuallyCollapsed;

        public MainWindow()
        {
            StartupDiagnostics.Stage("MainWindow.Settings");
            _settings = MultiverseSettingsService.Instance;
            StartupDiagnostics.Stage("MainWindow.InitializeComponent");
            InitializeComponent();
            RootLayout.SizeChanged += (_, e) => ApplyShellLayout(e.NewSize.Width);
            RemoteStreamingRuntime.Instance.InitializeUi(DispatcherQueue);
            _ = DesktopInputService.Instance;
            ContentFrame.Navigated += ContentFrame_Navigated;
            ConnectionsPanel.ManageRequested += OpenConnections;
            VersionText.Text = BuildInfo.ApplicationLabel;
            StartupDiagnostics.Stage("MainWindow.ConfigureWindow");
            ConfigureWindow();
            StartupDiagnostics.Stage("MainWindow.ConfigureTitleBar");
            ConfigureTitleBar();
            ConfigureDesktopGlass();
            StartupDiagnostics.Stage("MainWindow.ConfigureTray");
            ConfigureTrayBehavior();
            StartupDiagnostics.Stage("MainWindow.HostHud");
            HostTransmissionHudOverlay.Instance.Initialize();
            StartupDiagnostics.Stage("MainWindow.Home");
            NavigateTo(typeof(HomePage), NavHome);
            RemoteSessionService.Instance.SessionChanged += ShellSessionChanged;
            UpdateShellStatus();
            LevelingUpdates.Instance.Changed += UpdateChanged;
            _ = CheckUpdatesAfterStartupAsync();
        }

        private async System.Threading.Tasks.Task CheckUpdatesAfterStartupAsync() {
            await System.Threading.Tasks.Task.Delay(5000);
            if (!_allowClose) await LevelingUpdates.Instance.CheckAsync();
        }
        private void UpdateChanged() => DispatcherQueue.TryEnqueue(() => {
            UpdateNotice.IsOpen = LevelingUpdates.Instance.Offer is not null && !LevelingUpdates.Instance.Deferred;
            UpdateNotice.Message = "Versão " + LevelingUpdates.Instance.Offer?.Version + " disponível em Configurações.";
        });
        private void UpdateNotice_Closed(InfoBar sender, InfoBarClosedEventArgs args) { if(args.Reason==InfoBarCloseReason.CloseButton) LevelingUpdates.Instance.Later(); }
        private void OpenUpdate_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(SettingsPage), NavSettings);
        private void ShellSessionChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateShellStatus);
        private void OpenConnections() => NavigateTo(typeof(FriendsPage), NavFriends);
        private void UpdateShellStatus()
        {
            var session = RemoteSessionService.Instance.GetSnapshot();
            SidebarLocalName.Text = session.LocalName;
            SidebarStatusDot.Fill = new SolidColorBrush(session.Listening || session.Connected ? ColorHelper.FromArgb(255, 32, 228, 154) : ColorHelper.FromArgb(255, 166, 183, 202));
            SidebarReadyText.Text = session.Connected ? "Em sessão" : session.Listening ? "Pronto" : "Indisponível";
            ShellStatusText.Text = session.Connected ? $"Conectado • {session.RemoteName}" :
                session.Listening ? "Pronto para receber uma conexão" : "Aguardando conexão";
            FooterStatusText.Text = session.Connected ? "Em sessão" : session.Listening ? "Disponível" : "Indisponível";
        }

        private void ConfigureDesktopGlass()
        {
            void SolidFallback() => RootLayout.Background = new SolidColorBrush(
                ColorHelper.FromArgb(255, 16, 23, 29));
            if (!Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported())
            {
                SolidFallback();
                return;
            }
            try { SystemBackdrop = new LevelingDesktopBackdrop { Failed = SolidFallback }; }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("UI_DESKTOP_ACRYLIC_SETUP", ex);
                SolidFallback();
            }
        }

        private void ConfigureWindow()
        {
            _appWindow = AppWindow;
            _appWindow.Resize(new SizeInt32(1672, 941));
        }

        private void ConfigureTrayBehavior()
        {
            try
            {
                _mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                _trayIcon = new WindowsTrayIconService(RestoreFromTray, ExitFromTray);
                if (_appWindow is not null)
                    _appWindow.Closing += AppWindow_Closing;
                Closed += MainWindow_Closed;
            }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("TRAY_INIT_ERROR", ex);
                // Tray integration is optional UI behavior. If Windows rejects it,
                // normal window closing must remain available.
                _trayIcon?.Dispose();
                _trayIcon = null;
            }
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_allowClose || !_settings.GetMinimizeToTrayOnClose())
                return;

            // Never make the app unreachable: only cancel the close if the tray
            // icon was successfully registered first.
            if (_trayIcon?.Show() != true)
                return;

            args.Cancel = true;
            if (_mainHwnd != IntPtr.Zero)
                NativeWindow.ShowWindow(_mainHwnd, NativeWindow.SW_HIDE);
        }

        private void RestoreFromTray()
        {
            if (_mainHwnd == IntPtr.Zero)
                return;

            _trayIcon?.Hide();
            NativeWindow.ShowWindow(_mainHwnd, NativeWindow.SW_SHOW);
            Activate();
            NativeWindow.SetForegroundWindow(_mainHwnd);
        }

        private void ExitFromTray()
        {
            // Run after the native tray callback returns, so its hidden message
            // window is never destroyed while inside its own WndProc.
            Microsoft.UI.Dispatching.DispatcherQueue? dispatcher =
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher is not null)
            {
                dispatcher.TryEnqueue(ExitApplication);
                return;
            }

            ExitApplication();
        }

        private void ExitApplication()
        {
            if (_allowClose)
                return;

            _allowClose = true;
            _trayIcon?.Hide();
            Close();
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _allowClose = true;
            StartupDiagnostics.Stage("MainWindow.Closed");
            CleanupForExit();
        }

        private void CleanupForExit()
        {
            if (_exitCleanupDone)
                return;
            _exitCleanupDone = true;
            LevelingUpdates.Instance.Changed -= UpdateChanged;
            RemoteSessionService.Instance.SessionChanged -= ShellSessionChanged;
            ContentFrame.Navigated -= ContentFrame_Navigated;
            ConnectionsPanel.ManageRequested -= OpenConnections;
            ConnectionsPanel.Dispose();

            if (_appWindow is not null)
                _appWindow.Closing -= AppWindow_Closing;
            Closed -= MainWindow_Closed;

            try { HostTransmissionHudOverlay.Instance.Dispose(); } catch { }
            try { HostTransmissionService.Instance.Dispose(); } catch { }
            if (RemoteAudioService.IsValueCreated)
            {
                try { RemoteAudioService.Instance.Dispose(); } catch { }
            }
            if (RemoteGamepadService.IsValueCreated)
            {
                try { RemoteGamepadService.Instance.Dispose(); } catch { }
            }
            if (RemoteStreamingRuntime.IsValueCreated)
            {
                try { RemoteStreamingRuntime.Instance.Dispose(); } catch { }
            }
            try { RemoteSessionService.Instance.Dispose(); } catch { }
            if (VideoIncidentRecorder.IsValueCreated)
            {
                try { VideoIncidentRecorder.Instance.Dispose(); } catch { }
            }

            try { _trayIcon?.Dispose(); } catch { }
            _trayIcon = null;
            StartupDiagnostics.Stage("MainWindow.CleanupComplete");
        }

        private void ConfigureTitleBar()
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(ShellDragRegion);
            if (AppWindow.TitleBar is null)
                return;

            AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 244, 246, 250);
            AppWindow.TitleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 112, 125, 142);
            AppWindow.TitleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 24, 32, 45);
            AppWindow.TitleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 42, 20, 31);
        }

        private void NavHome_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(HomePage), NavHome);
        private void NavDevices_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(DevicePreferencesPage), NavDevices);
        private void NavFriends_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(FriendsPage), NavFriends);
        private void NavTransmission_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(TransmissionPage), NavTransmission);
        private void NavAudio_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(AudioPage), NavAudio);
        private void NavController_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(ControllerPage), NavController);
        private void NavSettings_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(SettingsPage), NavSettings);

        private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            // UI motion is brief and event-driven; no animation loop competes
            // with an active capture or a remote presentation session.
            if (ContentFrame.Content is FrameworkElement page &&
                !InterfaceMotion.ShouldReduceMotion())
            {
                page.Opacity = 0;
                var fade = new DoubleAnimation
                {
                    From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(140))
                };
                Storyboard.SetTarget(fade, page);
                Storyboard.SetTargetProperty(fade, "Opacity");
                var transition = new Storyboard();
                transition.Children.Add(fade);
                transition.Begin();
            }
            Type type = e.SourcePageType;
            SetButtonState(NavHome, type == typeof(HomePage));
            SetButtonState(NavDevices, type == typeof(DevicePreferencesPage) || type == typeof(DevicesPage));
            SetButtonState(NavFriends, type == typeof(FriendsPage));
            SetButtonState(NavTransmission, type == typeof(TransmissionPage) || type == typeof(DiagnosticsPage) || type == typeof(SessionsPage));
            SetButtonState(NavAudio, type == typeof(AudioPage));
            SetButtonState(NavController, type == typeof(ControllerPage));
            SetButtonState(NavSettings, type == typeof(SettingsPage));
            SetButtonState(NavKeyboardMouse, type == typeof(KeyboardMousePage));
        }

        private void NavigateTo(Type pageType, Button selectedButton)
        {
            try
            {
                StartupDiagnostics.Stage("Navigation." + pageType.Name);
                NavigationErrorText.Visibility = Visibility.Collapsed;
                NavigationErrorText.Text = "";

                if (ContentFrame.CurrentSourcePageType != pageType)
                {
                    bool navigated = ContentFrame.Navigate(pageType);
                    if (!navigated)
                        throw new InvalidOperationException($"A página {pageType.Name} recusou a navegação.");
                }

                SetButtonState(NavHome, selectedButton == NavHome);
                SetButtonState(NavDevices, selectedButton == NavDevices);
                SetButtonState(NavFriends, selectedButton == NavFriends);
                SetButtonState(NavTransmission, selectedButton == NavTransmission);
                SetButtonState(NavAudio, selectedButton == NavAudio);
                SetButtonState(NavController, selectedButton == NavController);
                SetButtonState(NavSettings, selectedButton == NavSettings);
                SetButtonState(NavKeyboardMouse, selectedButton == NavKeyboardMouse);
            }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("NAVIGATION_ERROR", ex, pageType.FullName);
                NavigationErrorText.Text = $"Não foi possível abrir {pageType.Name}: {ex.Message}";
                NavigationErrorText.Visibility = Visibility.Visible;
            }
        }

        private void NavKeyboardMouse_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(KeyboardMousePage), NavKeyboardMouse);

        private void CollapseSidebarButton_Click(object sender, RoutedEventArgs e)
        {
            _sidebarManuallyCollapsed = !_sidebarManuallyCollapsed;
            ApplyShellLayout(RootLayout.ActualWidth);
        }

        private void ApplyShellLayout(double width)
        {
            // Window content has no Page template to activate AdaptiveTriggers.
            // Use actual content width so snapping and DPI changes free space.
            bool collapsed = width < 1050 || _sidebarManuallyCollapsed;
            SidebarColumn.Width = new GridLength(collapsed ? 88 : 218);
            TitleSidebarColumn.Width = SidebarColumn.Width;
            SidebarComputerCard.Padding = new Thickness(collapsed ? 0 : 12);
            SidebarComputerCard.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            foreach (var button in new[] { NavHome, NavDevices, NavFriends, NavTransmission, NavAudio, NavController, NavKeyboardMouse, NavSettings })
            {
                button.Padding = new Thickness(collapsed ? 0 : 10, 0, collapsed ? 0 : 10, 0);
                if (button.Content is Grid grid)
                {
                    grid.ColumnDefinitions[0].Width = collapsed ? new GridLength(1, GridUnitType.Star) : new GridLength(48);
                    grid.ColumnDefinitions[1].Width = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
                }
            }
            foreach (var element in new FrameworkElement[] { SidebarAppName, ComputerInfoPanel,
                NavHomeText, NavDevicesText, NavFriendsText, NavTransmissionText, NavAudioText,
                NavControllerText, NavKeyboardMouseText, NavSettingsText, VersionText, FooterStatusText })
                element.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            CollapseSidebarButton.Content = collapsed ? "›" : "‹";
            ConnectionsColumn.Width = new GridLength(width >= 1500 ? 276 : 0);
            ConnectionsRail.Visibility = width >= 1500 ? Visibility.Visible : Visibility.Collapsed;
            DispatcherQueue.TryEnqueue(() => { RootLayout.UpdateLayout(); SidebarIcon.RefreshShell(RootLayout); });
        }

        private static void SetButtonState(Button button, bool selected)
        {
            button.Background = selected ? (Brush)Application.Current.Resources["DesignSelected"] : new SolidColorBrush(Colors.Transparent);
            button.BorderBrush = selected ? (Brush)Application.Current.Resources["DesignAccent"] : new SolidColorBrush(Colors.Transparent);
            button.BorderThickness = new Thickness(0);

            if (button.Content is not Grid grid)
                return;

            foreach (UIElement element in grid.Children)
            {
                if (element is SidebarIcon sidebarIcon)
                {
                    sidebarIcon.Selected = selected;
                }
                else if (element is FontIcon icon)
                {
                    icon.Foreground = new SolidColorBrush(
                        selected ? ColorHelper.FromArgb(255, 255, 255, 255) : ColorHelper.FromArgb(255, 152, 164, 181));
                }
                else if (element is TextBlock text)
                {
                    text.Foreground = new SolidColorBrush(
                        selected ? ColorHelper.FromArgb(255, 255, 255, 255) : ColorHelper.FromArgb(255, 199, 207, 218));
                }
            }
        }
        private static class NativeWindow
        {
            internal const int SW_HIDE = 0;
            internal const int SW_SHOW = 5;

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool ShowWindow(IntPtr hwnd, int command);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetForegroundWindow(IntPtr hwnd);
        }

    }
}
