using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Threading.Tasks;
using WinRT;
using WinRT.Interop;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using VirtualKey = Windows.System.VirtualKey;

namespace Multiverse.Services
{
    /// <summary>
    /// Independent remote-video surface. Resizing/maximizing this window only
    /// resizes the CLIENT presenter; it never asks the host to change capture,
    /// resolution, NVENC cadence or bitrate.
    /// </summary>
    internal sealed class RemoteSessionWindow : IDisposable
    {
        private readonly Window _window;
        private readonly DispatcherQueue? _dispatcher;
        private readonly Grid _root;
        private readonly SwapChainPanel _panel;
        private readonly Canvas _cursorCanvas;
        private readonly Ellipse _cursorVisual;
        private readonly Button _keySink;
        private readonly DispatcherTimer _cursorTimer;
        private readonly DispatcherTimer _hudTimer;
        private readonly DispatcherTimer _resizeTimer;
        private readonly Border _hudBorder;
        private readonly TextBlock _hudText;
        private readonly TextBlock _presentationStatus;
        private readonly PresentationRetry _presentationRetry = new();
        private readonly PresentationBusyRetry _presentationBusyRetry = new();
        private readonly PresenterRecoveryPolicy _presenterRecovery = new();
        private readonly ClientVideoStatus _videoStatus = new();
        private bool _presentationFailed;
        private readonly RemoteSessionService _session =
            RemoteSessionService.Instance;
        private readonly MultiverseSettingsService _settings =
            MultiverseSettingsService.Instance;
        private NativeNvdecPlayer? _decoder;
        private AppWindow? _appWindow;
        private bool _rendererAttached;
        private uint _lastRenderWidth;
        private uint _lastRenderHeight;
        private float _lastCompositionScaleX = 1.0f;
        private float _lastCompositionScaleY = 1.0f;
        private bool _compositionScaleKnown;
        private bool _isFullScreen;
        private TaskCompletionSource<bool>? _closeCompletion;
        private bool _disposed;
        private RemoteDesktopInputCapture? _desktopInput;
        private TextBlock? _inputHint;
        private Button? _inputButton;

        public event EventHandler? Closed;

        public RemoteSessionWindow(NativeNvdecPlayer decoder)
        {
            _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            _window = new Window
            {
                Title = "Leveling • Sessão remota"
            };

            _root = new Grid
            {
                Background = new SolidColorBrush(Colors.Black)
            };

            _panel = new SwapChainPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _cursorCanvas = new Canvas
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            _cursorVisual = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(Colors.White),
                Stroke = new SolidColorBrush(Colors.Black),
                StrokeThickness = 1.5,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            _cursorCanvas.Children.Add(_cursorVisual);

            TextBlock hint = new()
            {
                Text = "F8 • teclado/mouse  |  F9 • controle  |  Ctrl+Alt+Backspace • liberar",
                Foreground = new SolidColorBrush(Colors.White),
                Opacity = 0.45,
                FontSize = 11,
                Margin = new Thickness(14),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false
            };

            _hudText = new TextBlock
            {
                FontSize = 10,
                Foreground = new SolidColorBrush(Colors.White),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                IsHitTestVisible = false
            };
            var hudBackground = new SolidColorBrush(Colors.Black)
            {
                Opacity = 0.25
            };
            _hudBorder = new Border
            {
                Child = _hudText,
                Background = hudBackground,
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(10, 10, 10, 10),
                CornerRadius = new CornerRadius(5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };

            _keySink = new Button
            {
                Width = 1,
                Height = 1,
                Opacity = 0.01,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsTabStop = true
            };

            _root.Children.Add(_panel);
            _presentationStatus = new TextBlock
            {
                Text = "Preparando vídeo…", Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 560, Margin = new Thickness(24),
                IsHitTestVisible = false
            };
            _root.Children.Add(_presentationStatus);
            _root.Children.Add(_cursorCanvas);
            var inputBar=new StackPanel {Spacing=6,Margin=new Thickness(14),MaxWidth=480,
                HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top};
            hint.Margin=new Thickness(0);hint.TextWrapping=TextWrapping.Wrap;hint.Opacity=0.85;
            _inputButton=new Button {Content="Jogar com teclado e mouse",HorizontalAlignment=HorizontalAlignment.Right};
            _inputButton.Click+=(_,_)=>
            {
                EnsureDesktopInput();
                if(_desktopInput is null)return;
                _keySink.Focus(FocusState.Programmatic);
                if(DesktopInputService.Instance.IsCapturing)DesktopInputService.Instance.Release();
                else DesktopInputService.Instance.BeginCapture();
            };
            var disconnectButton = new Button { Content = "Desconectar",
                HorizontalAlignment = HorizontalAlignment.Right };
            disconnectButton.Click += async (_, _) =>
            {
                disconnectButton.IsEnabled = false;
                try
                {
                    DesktopInputService.Instance.Release();
                    await _session.DisconnectAsync();
                    await CloseAsync();
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.RecordException("CLIENT_DISCONNECT_ERROR", ex);
                    if (IsOpen)
                    {
                        _presentationStatus.Text = "Não foi possível concluir a desconexão. Tente novamente.";
                        _presentationStatus.Visibility = Visibility.Visible;
                        disconnectButton.IsEnabled = true;
                    }
                }
            };
            var sessionActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right };
            sessionActions.Children.Add(_inputButton);
            sessionActions.Children.Add(disconnectButton);
            inputBar.Children.Add(sessionActions);inputBar.Children.Add(hint);
            _root.Children.Add(inputBar);
            _inputHint=hint;
            _root.Children.Add(_hudBorder);
            _root.Children.Add(_keySink);
            _window.Content = _root;

            _cursorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _cursorTimer.Tick += CursorTimer_Tick;

            _hudTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _hudTimer.Tick += HudTimer_Tick;
            _resizeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(80)
            };
            _resizeTimer.Tick += ResizeTimer_Tick;
            _settings.TransmissionHudVisibilityChanged += Settings_TransmissionHudVisibilityChanged;

            _root.SizeChanged += Root_SizeChanged;
            _panel.SizeChanged += Panel_SizeChanged;
            _keySink.KeyDown += Root_KeyDown;
            _window.Activated += Window_Activated;
            _window.Closed += Window_Closed;
        }

        public bool IsOpen { get; private set; }

        public void PlayWithKeyboardMouse()
        {
            if(_disposed)return;
            _window.Activate();
            EnsureDesktopInput();
            if(_desktopInput is null)return;
            _keySink.Focus(FocusState.Programmatic);
            DesktopInputService.Instance.BeginCapture();
        }

        private void EnsureDesktopInput()
        {
            if(_desktopInput is null)
            {
                try { _desktopInput=new RemoteDesktopInputCapture(WindowNative.GetWindowHandle(_window)); }
                catch(Exception ex)
                {
                    // Input setup must never prevent the video window opening.
                    StartupDiagnostics.RecordException("DESKTOP_INPUT_SETUP_ERROR",ex);
                    DesktopInputService.Instance.CaptureUnavailable(ex.Message);
                }
            }
        }

        public void ShowWindowed()
        {
            if (_disposed)
                return;

            if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
            {
                _dispatcher.TryEnqueue(ShowWindowed);
                return;
            }

            _window.Activate();
            IsOpen = true;
            EnsureAppWindow();
            EnsureDesktopInput();
            ConfigureOverlappedPresenter();
            _isFullScreen = false;
            UpdateVideoSurfaceLayout();
            ScheduleResize();
            _cursorTimer.Start();
            _hudTimer.Start();
            UpdateHud();
            _keySink.Focus(FocusState.Programmatic);
        }

        public void ShowFullScreen()
        {
            if (_disposed)
                return;

            if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
            {
                _dispatcher.TryEnqueue(ShowFullScreen);
                return;
            }

            _window.Activate();
            IsOpen = true;
            EnsureAppWindow();
            EnsureDesktopInput();
            try
            {
                _appWindow?.SetPresenter(FullScreenPresenter.Create());
                _isFullScreen = true;
            }
            catch
            {
                _isFullScreen = false;
                ConfigureOverlappedPresenter();
            }

            UpdateVideoSurfaceLayout();
            ScheduleResize();
            _cursorTimer.Start();
            _hudTimer.Start();
            UpdateHud();
            _keySink.Focus(FocusState.Programmatic);
        }

        public void ToggleFullScreen()
        {
            if (_isFullScreen)
                ExitFullScreen();
            else
                ShowFullScreen();
        }

        public void ExitFullScreen()
        {
            if (_disposed)
                return;

            if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
            {
                _dispatcher.TryEnqueue(ExitFullScreen);
                return;
            }

            EnsureAppWindow();
            ConfigureOverlappedPresenter();
            _isFullScreen = false;
            UpdateVideoSurfaceLayout();
            ScheduleResize();
            _keySink.Focus(FocusState.Programmatic);
        }

        private void EnsureAppWindow()
        {
            if (_appWindow is not null)
                return;

            try
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(_window);
                WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
                _appWindow = AppWindow.GetFromWindowId(id);
            }
            catch
            {
                _appWindow = null;
            }
        }

        private void ConfigureOverlappedPresenter()
        {
            try
            {
                if (_appWindow is null)
                    return;

                _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                if (_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsResizable = true;
                    presenter.IsMaximizable = true;
                    presenter.IsMinimizable = true;
                }
            }
            catch
            {
                // Window chrome failure must not become a streaming failure.
            }
        }

        public void SetDecoder(NativeNvdecPlayer decoder)
        {
            if (_disposed)
                return;

            if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
            {
                _dispatcher.TryEnqueue(() => SetDecoder(decoder));
                return;
            }

            if (!ReferenceEquals(_decoder, decoder))
            {
                if (!DetachRendererCore()) return;
                _decoder = decoder;
                _presenterRecovery.Reset();
                _presentationRetry.Reset();
                _presentationBusyRetry.Reset();
                _presentationFailed = false;
                _resizeTimer.Interval = TimeSpan.FromMilliseconds(80);
            }

            UpdateVideoSurfaceLayout();
            ScheduleResize();
        }

        public Task DetachRendererAsync()
        {
            if (_disposed)
                return Task.CompletedTask;
            var expectedDecoder = _decoder;
            var ui = new UiThreadOperation(
                () => _dispatcher is null || _dispatcher.HasThreadAccess,
                action => _dispatcher is not null && _dispatcher.TryEnqueue(() => action()));
            return SurfaceDetachRetry.RunAsync(async () =>
            {
                bool detached = false;
                await ui.RunAsync(() =>
                {
                    // A delayed retry must never detach a replacement session.
                    if (_disposed || !ReferenceEquals(expectedDecoder, _decoder))
                        throw new InvalidOperationException("A janela ou o decoder mudou durante a liberação da superfície.");
                    detached = DetachRendererCore();
                });
                return detached;
            }, () => Task.Delay(100));
        }

        private bool DetachRendererCore()
        {
            try { _decoder?.DetachRenderer(); }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("CLIENT_PRESENTER_DETACH_PENDING", ex);
                return false;
            }
            _rendererAttached = false;
            _lastRenderWidth = 0;
            _lastRenderHeight = 0;
            _compositionScaleKnown = false;
            _resizeTimer.Stop();
            return true;
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateVideoSurfaceLayout();
            ScheduleResize();
        }

        private void UpdateVideoSurfaceLayout()
        {
            double availableWidth = _root.ActualWidth;
            double availableHeight = _root.ActualHeight;
            if (availableWidth < 2 || availableHeight < 2)
                return;

            RemoteStreamConfig? config = _session.GetStreamConfig();
            if (config is null || config.Width == 0 || config.Height == 0)
            {
                _panel.Width = availableWidth;
                _panel.Height = availableHeight;
                _cursorCanvas.Width = availableWidth;
                _cursorCanvas.Height = availableHeight;
                return;
            }

            double sourceAspect = config.Width / (double)config.Height;
            double availableAspect = availableWidth / availableHeight;
            double width;
            double height;

            if (availableAspect > sourceAspect)
            {
                height = availableHeight;
                width = height * sourceAspect;
            }
            else
            {
                width = availableWidth;
                height = width / sourceAspect;
            }

            _panel.Width = Math.Max(2, width);
            _panel.Height = Math.Max(2, height);
            _cursorCanvas.Width = _panel.Width;
            _cursorCanvas.Height = _panel.Height;
        }

        private void CursorTimer_Tick(object? sender, object e)
        {
            RemoteSessionSnapshot snapshot =
                _session.GetSnapshot();

            if (!snapshot.Connected ||
                snapshot.Role != RemoteSessionRole.Client ||
                !snapshot.RemoteCursorVisible ||
                _panel.ActualWidth < 2 ||
                _panel.ActualHeight < 2)
            {
                _cursorVisual.Visibility = Visibility.Collapsed;
                return;
            }

            double x = Math.Clamp(snapshot.RemoteCursorX, 0.0, 1.0) *
                _panel.ActualWidth;
            double y = Math.Clamp(snapshot.RemoteCursorY, 0.0, 1.0) *
                _panel.ActualHeight;

            Canvas.SetLeft(_cursorVisual, x);
            Canvas.SetTop(_cursorVisual, y);
            _cursorVisual.Visibility = Visibility.Visible;
        }

        private void Settings_TransmissionHudVisibilityChanged(object? sender, EventArgs e)
        {
            if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
            {
                _dispatcher.TryEnqueue(UpdateHud);
                return;
            }
            UpdateHud();
        }

        private void HudTimer_Tick(object? sender, object e)
        {
            var input=DesktopInputService.Instance;
            if(_inputHint is not null) _inputHint.Text=input.Status+"\nF"+(input.Shortcuts.Capture-111)+" • ativar/liberar  |  F"+(input.Shortcuts.Gamepad-111)+" • controle  |  Ctrl+Alt+Backspace • liberar";
            if(_inputButton is not null) _inputButton.Content=input.IsCapturing?"Liberar teclado e mouse":"Jogar com teclado e mouse";
            UpdateHud();
            UpdateVideoStatus();
        }

        private void UpdateVideoStatus()
        {
            if (_disposed || _presentationFailed || _decoder is null) return;
            try
            {
                var session = _session.GetSnapshot();
                if (!session.Connected || session.Role != RemoteSessionRole.Client) return;
                var decoder = _decoder.GetSnapshot();
                if (_rendererAttached && _presenterRecovery.Observe(Environment.TickCount64,
                    decoder.DecodedPictures, decoder.D3D11PresentedFrames, decoder.D3D11PresentInFlight))
                {
                    StartupDiagnostics.Stage("CLIENT_PRESENTER_REATTACH", "Decoded frames advance while presentation is stopped.");
                    if (!DetachRendererCore()) return;
                    _compositionScaleKnown = false;
                    AttachOrResize();
                    return;
                }
                var transport = RemoteStreamingRuntime.Instance.GetState().Transport?.GetSnapshot();
                string message = _videoStatus.Observe(Environment.TickCount64,
                    session.SessionGeneration, session.StreamConfig?.Generation ?? 0,
                    transport?.ReceivedPackets ?? 0, decoder.NetworkFramesReceived,
                    (long)decoder.DecodedPictures, (long)decoder.D3D11PresentedFrames, decoder.AwaitingKeyFrame);
                _presentationStatus.Text = message;
                _presentationStatus.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (ObjectDisposedException) { } // Concurrent teardown; the next session owns its status.
        }

        private void UpdateHud()
        {
            if (_disposed)
                return;
            try
            {
                if (!_settings.GetTransmissionHudVisible())
                {
                    _hudBorder.Visibility = Visibility.Collapsed;
                    return;
                }

                RemoteSessionSnapshot snapshot = _session.GetSnapshot();
                if (!snapshot.Connected || snapshot.Role != RemoteSessionRole.Client)
                {
                    _hudBorder.Visibility = Visibility.Collapsed;
                    return;
                }

                // Zero is a valid measured FPS, not a reason to display the
                // configured target. Before the first feedback sample use N/A.
                RemotePipelineTelemetrySnapshot video =
                    RemotePipelineTelemetry.Instance.GetSnapshot();
                double? fps = video.PresenterAttached
                    ? video.PresentFps : video.ReceiveFps;
                double? rtt = snapshot.RoundTripMs > 0 ? snapshot.RoundTripMs : null;
                double? loss = snapshot.RemotePacketLossPercent >= 0 ? snapshot.RemotePacketLossPercent : null;
                double? rx = video.ReceiveMbps;
                _hudText.Text = RemoteSessionHudFormatter.Format(
                    new RemoteSessionHudValues(fps, rtt, loss, rx));
                _hudBorder.Visibility = Visibility.Visible;
            }
            catch
            {
                _hudBorder.Visibility = Visibility.Collapsed;
            }
        }

        private void Window_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                UpdateVideoSurfaceLayout();
                ScheduleResize();
                _keySink.Focus(FocusState.Programmatic);
            }
        }

        private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if(_desktopInput?.Capturing==true) {e.Handled=true;return;}
            if (e.Key == VirtualKey.F11)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Escape && _isFullScreen)
            {
                ExitFullScreen();
                e.Handled = true;
            }
        }

        private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleResize();
        }

        private void ScheduleResize()
        {
            if (_disposed || !IsOpen)
                return;
            // Coalesce an interactive resize. The native presenter must not
            // be stopped/restarted for every intermediate XAML size event.
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }

        private void ResizeTimer_Tick(object? sender, object e)
        {
            _resizeTimer.Stop();
            AttachOrResize();
        }

        private void AttachOrResize()
        {
            NativeNvdecPlayer? decoder = _decoder;
            if (_disposed || !IsOpen || decoder is null || !decoder.Running)
                return;

            double scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
            if (!double.IsFinite(scale) || scale < 0.5 || scale > 4.0)
                scale = 1.0;
            double logicalWidth = _panel.ActualWidth;
            double logicalHeight = _panel.ActualHeight;
            if (logicalWidth < 2 || logicalHeight < 2)
                return;

            // Physical back-buffer pixels / effective XAML pixels. Include
            // the rounding in the ratio, otherwise DPI changes can leave a
            // thin strip or an incorrectly sized image at the panel edge.
            uint width = Math.Max(2u, ((uint)Math.Round(logicalWidth * scale)) & ~1u);
            uint height = Math.Max(2u, ((uint)Math.Round(logicalHeight * scale)) & ~1u);
            float scaleX = (float)(width / logicalWidth);
            float scaleY = (float)(height / logicalHeight);

            try
            {
                // Configure the inverse DXGI composition matrix before
                // attaching a new swap chain or submitting the first frame.
                // An old native DLL without this additive API uses logical
                // buffer dimensions instead of rendering physical pixels
                // with an incorrect identity transform.
                int scaleStatus = 0;
                if (!_compositionScaleKnown ||
                    Math.Abs(_lastCompositionScaleX - scaleX) > 0.0001f ||
                    Math.Abs(_lastCompositionScaleY - scaleY) > 0.0001f)
                {
                    scaleStatus = decoder.SetPresentationScale(scaleX, scaleY);
                }
                if (scaleStatus == -20022)
                {
                    width = Math.Max(2u, ((uint)Math.Round(logicalWidth)) & ~1u);
                    height = Math.Max(2u, ((uint)Math.Round(logicalHeight)) & ~1u);
                    scaleX = 1.0f;
                    scaleY = 1.0f;
                }
                else if (scaleStatus < 0)
                {
                    ReportPresentationFailure("Escala D3D11", scaleStatus);
                    return;
                }
                _lastCompositionScaleX = scaleX;
                _lastCompositionScaleY = scaleY;
                _compositionScaleKnown = true;

                if (!_rendererAttached)
                {
                    IntPtr panelUnknown =
                        ((IWinRTObject)_panel).NativeObject.ThisPtr;

                    int hr = decoder.AttachRenderer(
                        panelUnknown,
                        width,
                        height);
                    if (hr < 0)
                    {
                        ReportPresentationFailure("Conexão D3D11 à janela", hr);
                        return;
                    }

                    _rendererAttached = true;
                    _presentationFailed = false;
                    _presentationRetry.Reset();
                    _presentationBusyRetry.Reset();
                    _presentationStatus.Visibility = Visibility.Collapsed;
                    _lastRenderWidth = width;
                    _lastRenderHeight = height;
                    return;
                }

                if (_lastRenderWidth != width ||
                    _lastRenderHeight != height)
                {
                    int hr = decoder.ResizeRenderer(width, height);
                    if (hr >= 0)
                    {
                        _lastRenderWidth = width;
                        _lastRenderHeight = height;
                        _presentationRetry.Reset();
                        _presentationBusyRetry.Reset();
                        _presentationFailed = false;
                        _presentationStatus.Visibility = Visibility.Collapsed;
                    }
                    else { ReportPresentationFailure("Redimensionamento D3D11", hr); return; }
                }
                _presentationBusyRetry.Reset();
            }
            catch (Exception ex)
            {
                ReportPresentationFailure("Apresentação do vídeo", ex.HResult);
            }
        }

        private void ReportPresentationFailure(string stage, int hr)
        {
            if (_presentationBusyRetry.TrySchedule(hr, Environment.TickCount64))
            {
                // Retain the current frame and retry without blocking the UI or stopping the host.
                _resizeTimer.Interval = TimeSpan.FromMilliseconds(150);
                _resizeTimer.Start();
                return;
            }
            _presentationFailed = true;
            string reason = $"{stage}: 0x{hr:X8}";
            _presentationStatus.Text = "Não foi possível exibir o vídeo. " + reason +
                "\nAbra o diagnóstico do cliente para consultar os detalhes.";
            _presentationStatus.Visibility = Visibility.Visible;
            StartupDiagnostics.Stage("CLIENT_PRESENTER_ERROR", reason);
            if (_presentationRetry.TrySchedule())
            {
                // Repeating Resize on a removed D3D device cannot recover it.
                // Reattach rebuilds the lost device and its dependent resources.
                DetachRendererCore();
                _compositionScaleKnown = false;
                _resizeTimer.Interval = TimeSpan.FromMilliseconds(500);
                _resizeTimer.Start();
                return;
            }
            var session = _session.GetSnapshot();
            if (session.StreamConfig is { } config)
                _ = _session.ReportClientVideoStartupFailureAsync(config.Generation, session.SessionGeneration, reason);
        }

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            IsOpen = false;
            _isFullScreen = false;
            _cursorTimer.Stop();
            _hudTimer.Stop();
            _resizeTimer.Stop();
            _hudBorder.Visibility = Visibility.Collapsed;
            _cursorVisual.Visibility = Visibility.Collapsed;
            bool detached = DetachRendererCore();
            Closed?.Invoke(this, EventArgs.Empty);
            if (detached) _closeCompletion?.TrySetResult(true);
            else _closeCompletion?.TrySetException(new TimeoutException("A superfície ainda está em uso."));
            _closeCompletion = null;
        }

        public Task CloseAsync()
        {
            if (_disposed || !IsOpen)
                return Task.CompletedTask;

            if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
            {
                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                if (!_dispatcher.TryEnqueue(
                        () =>
                        {
                            Task closeTask = CloseAsync();
                            _ = closeTask.ContinueWith(
                                task => completion.TrySetResult(!task.IsFaulted),
                                TaskScheduler.Default);
                        }))
                {
                    completion.TrySetResult(false);
                }

                return completion.Task;
            }

            _closeCompletion ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task task = _closeCompletion.Task;

            try
            {
                _window.Close();
            }
            catch
            {
                _closeCompletion?.TrySetResult(false);
                _closeCompletion = null;
            }

            return task;
        }

        public void Close() => _ = CloseAsync();

        public void Dispose()
        {
            if (_disposed)
                return;

            _desktopInput?.Dispose();
            _desktopInput=null;
            if (!DetachRendererCore()) return;
            _disposed = true;
            _cursorTimer.Stop();
            _cursorTimer.Tick -= CursorTimer_Tick;
            _hudTimer.Stop();
            _hudTimer.Tick -= HudTimer_Tick;
            _resizeTimer.Stop();
            _resizeTimer.Tick -= ResizeTimer_Tick;
            _settings.TransmissionHudVisibilityChanged -= Settings_TransmissionHudVisibilityChanged;
            _root.SizeChanged -= Root_SizeChanged;
            _panel.SizeChanged -= Panel_SizeChanged;
            _keySink.KeyDown -= Root_KeyDown;
            _window.Activated -= Window_Activated;
            _window.Closed -= Window_Closed;
            _decoder = null;
        }
    }
}
