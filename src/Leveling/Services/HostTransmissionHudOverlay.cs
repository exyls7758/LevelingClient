using Microsoft.UI.Dispatching;
using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace Multiverse.Services
{
    /// <summary>
    /// Host-only, native Win32 HUD. It deliberately does not hook/inject into games and
    /// does not create any new streaming telemetry path. The existing encoder/transport/
    /// session snapshots are sampled at 5 Hz and painted into a click-through tool window.
    /// </summary>
    internal sealed class HostTransmissionHudOverlay : IDisposable
    {
        private const int HudWidth = 300;
        private const int HudHeight = 24;
        private const int HudMargin = 10;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const uint WDA_MONITOR = 0x00000001;

        private static readonly Lazy<HostTransmissionHudOverlay> LazyInstance =
            new(() => new HostTransmissionHudOverlay());

        public static HostTransmissionHudOverlay Instance => LazyInstance.Value;

        private readonly HostVideoPipelineController _host = HostTransmissionService.Instance;
        private RemoteStreamingRuntime Runtime => RemoteStreamingRuntime.Instance;
        private readonly RemoteSessionService _session = RemoteSessionService.Instance;
        private readonly MultiverseSettingsService _settings = MultiverseSettingsService.Instance;
        private readonly DispatcherQueue? _dispatcher;
        private readonly DispatcherQueueTimer? _timer;
        private readonly NativeMethods.WndProc _wndProc;
        private readonly string _windowClassName;

        private IntPtr _window;
        private IntPtr _backgroundBrush;
        private IntPtr _font;
        private string _text = "FPS --  |  LAT -- ms  |  LOSS --  |  TX -- Mbps";
        private ulong _lastEncodedFrames;
        private long _lastSampleTick;
        private double _smoothedFps;
        private bool _disposed;

        private HostTransmissionHudOverlay()
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            _wndProc = WindowProc;
            _windowClassName = $"LevelingHostHud_{Environment.ProcessId}";
            _backgroundBrush = NativeMethods.CreateSolidBrush(NativeMethods.Rgb(8, 11, 16));
            _font = NativeMethods.CreateFontW(
                -13, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

            if (_dispatcher is not null)
            {
                _timer = _dispatcher.CreateTimer();
                _timer.Interval = TimeSpan.FromMilliseconds(200);
                _timer.IsRepeating = true;
                _timer.Tick += Timer_Tick;
            }

            _host.StateChanged += Host_StateChanged;
            _settings.HostTransmissionHudVisibilityChanged += Settings_HostTransmissionHudVisibilityChanged;
        }

        public void Initialize() => Dispatch(EvaluateVisibility);

        private void Host_StateChanged(object? sender, EventArgs e) =>
            Dispatch(EvaluateVisibility);

        private void Settings_HostTransmissionHudVisibilityChanged(object? sender, EventArgs e) =>
            Dispatch(EvaluateVisibility);

        private void Dispatch(Action action)
        {
            if (_disposed)
                return;

            if (_dispatcher is null || _dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            _dispatcher.TryEnqueue(() => action());
        }

        private void EvaluateVisibility()
        {
            if (_disposed)
                return;

            HostTransmissionSnapshot host = _host.GetSnapshot();
            RemoteSessionSnapshot session = _session.GetSnapshot();
            bool show =
                _settings.GetHostTransmissionHudVisible() &&
                host.RemoteTransportRunning &&
                session.Connected &&
                session.Role == RemoteSessionRole.Host;

            if (!show)
            {
                CloseOverlay();
                return;
            }

            EnsureOverlay(host.MonitorDeviceName);
            _timer?.Start();
            UpdateHud();
        }

        private void EnsureOverlay(string monitorDeviceName)
        {
            if (_window != IntPtr.Zero)
                return;

            IntPtr instance = NativeMethods.GetModuleHandleW(null);
            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                lpfnWndProc = _wndProc,
                hInstance = instance,
                lpszClassName = _windowClassName
            };

            ushort atom = NativeMethods.RegisterClassExW(ref wc);
            if (atom == 0 && Marshal.GetLastWin32Error() != NativeMethods.ERROR_CLASS_ALREADY_EXISTS)
                return;

            (int x, int y) = ResolveHudPosition(monitorDeviceName);
            _window = NativeMethods.CreateWindowExW(
                NativeMethods.WS_EX_TOPMOST |
                NativeMethods.WS_EX_TRANSPARENT |
                NativeMethods.WS_EX_TOOLWINDOW |
                NativeMethods.WS_EX_LAYERED |
                NativeMethods.WS_EX_NOACTIVATE,
                _windowClassName,
                "Leveling HUD",
                NativeMethods.WS_POPUP,
                x,
                y,
                HudWidth,
                HudHeight,
                IntPtr.Zero,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);

            if (_window == IntPtr.Zero)
                return;

            NativeMethods.SetLayeredWindowAttributes(_window, 0, 150, NativeMethods.LWA_ALPHA);

            // WDA_EXCLUDEFROMCAPTURE keeps the HUD local to the host. Older Windows
            // versions may reject 0x11; WDA_MONITOR is a best-effort fallback that
            // still prevents the overlay contents from being retransmitted.
            if (!NativeMethods.SetWindowDisplayAffinity(_window, WDA_EXCLUDEFROMCAPTURE))
                NativeMethods.SetWindowDisplayAffinity(_window, WDA_MONITOR);

            NativeMethods.SetWindowPos(
                _window,
                NativeMethods.HWND_TOPMOST,
                x,
                y,
                HudWidth,
                HudHeight,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.ShowWindow(_window, NativeMethods.SW_SHOWNOACTIVATE);

            _lastEncodedFrames = 0;
            _lastSampleTick = 0;
            _smoothedFps = 0;
        }

        private static (int X, int Y) ResolveHudPosition(string monitorDeviceName)
        {
            try
            {
                HardwareSnapshot hardware = NativeCoreService.ReadHardware();
                MonitorInfo? monitor = hardware.Monitors.FirstOrDefault(m =>
                    !string.IsNullOrWhiteSpace(monitorDeviceName) &&
                    string.Equals(m.DeviceName, monitorDeviceName, StringComparison.OrdinalIgnoreCase));
                monitor ??= hardware.Monitors.FirstOrDefault(m => m.IsPrimary);
                monitor ??= hardware.Monitors.FirstOrDefault();

                if (monitor is not null)
                {
                    int x = monitor.Left + HudMargin;
                    int y = monitor.Top + HudMargin;
                    return (x, y);
                }
            }
            catch
            {
            }

            return (HudMargin, HudMargin);
        }

        private void Timer_Tick(DispatcherQueueTimer sender, object args)
        {
            HostTransmissionSnapshot host = _host.GetSnapshot();
            RemoteSessionSnapshot session = _session.GetSnapshot();
            if (!_settings.GetHostTransmissionHudVisible() ||
                !host.RemoteTransportRunning ||
                !session.Connected ||
                session.Role != RemoteSessionRole.Host)
            {
                CloseOverlay();
                return;
            }

            UpdateHud();
        }

        private void UpdateHud()
        {
            if (_window == IntPtr.Zero)
                return;

            RemoteStreamingState runtime = Runtime.GetState();
            RemoteSessionSnapshot session = _session.GetSnapshot();
            EncoderStats? encoder = runtime.Capture?.GetEncoderStats();
            TransportSnapshot? transport = runtime.Transport?.GetSnapshot();

            double fps = CalculateEncoderFps(encoder);
            double rtt = Math.Max(0, session.RoundTripMs);
            double loss = Math.Max(0, session.RemotePacketLossPercent);
            double tx = Math.Max(0, transport?.ActualSendMbps ?? 0);

            _text = string.Format(
                CultureInfo.InvariantCulture,
                "FPS {0:0}   LAT {1:0}ms   LOSS {2:0.0}%   TX {3:0.0}",
                fps,
                rtt,
                loss,
                tx);

            NativeMethods.InvalidateRect(_window, IntPtr.Zero, false);
        }

        private double CalculateEncoderFps(EncoderStats? encoder)
        {
            if (encoder is null || !encoder.Running)
                return 0;

            long now = Environment.TickCount64;
            ulong frames = encoder.EncodedFrames;
            if (_lastSampleTick == 0 || now <= _lastSampleTick || frames < _lastEncodedFrames)
            {
                _lastSampleTick = now;
                _lastEncodedFrames = frames;
                _smoothedFps = encoder.TargetFps;
                return _smoothedFps;
            }

            double seconds = (now - _lastSampleTick) / 1000.0;
            if (seconds >= 0.05)
            {
                double sample = (frames - _lastEncodedFrames) / seconds;
                if (sample >= 0 && sample <= 300)
                {
                    _smoothedFps = _smoothedFps <= 0
                        ? sample
                        : _smoothedFps * 0.65 + sample * 0.35;
                }
                _lastSampleTick = now;
                _lastEncodedFrames = frames;
            }

            return _smoothedFps;
        }

        private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == NativeMethods.WM_ERASEBKGND)
                return new IntPtr(1);

            if (msg == NativeMethods.WM_PAINT)
            {
                IntPtr hdc = NativeMethods.BeginPaint(hwnd, out NativeMethods.PAINTSTRUCT paint);
                try
                {
                    NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT rect);
                    NativeMethods.FillRect(hdc, ref rect, _backgroundBrush);
                    NativeMethods.SetBkMode(hdc, NativeMethods.TRANSPARENT);
                    NativeMethods.SetTextColor(hdc, NativeMethods.Rgb(244, 246, 250));
                    IntPtr previous = _font != IntPtr.Zero
                        ? NativeMethods.SelectObject(hdc, _font)
                        : IntPtr.Zero;
                    try
                    {
                        NativeMethods.DrawTextW(
                            hdc,
                            _text,
                            -1,
                            ref rect,
                            NativeMethods.DT_CENTER |
                            NativeMethods.DT_VCENTER |
                            NativeMethods.DT_SINGLELINE |
                            NativeMethods.DT_NOPREFIX);
                    }
                    finally
                    {
                        if (previous != IntPtr.Zero)
                            NativeMethods.SelectObject(hdc, previous);
                    }
                }
                finally
                {
                    NativeMethods.EndPaint(hwnd, ref paint);
                }
                return IntPtr.Zero;
            }

            return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        private void CloseOverlay()
        {
            _timer?.Stop();
            if (_window != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_window);
                _window = IntPtr.Zero;
            }

            _lastEncodedFrames = 0;
            _lastSampleTick = 0;
            _smoothedFps = 0;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _host.StateChanged -= Host_StateChanged;
            _settings.HostTransmissionHudVisibilityChanged -= Settings_HostTransmissionHudVisibilityChanged;
            if (_timer is not null)
            {
                _timer.Stop();
                _timer.Tick -= Timer_Tick;
            }
            CloseOverlay();

            if (_backgroundBrush != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(_backgroundBrush);
                _backgroundBrush = IntPtr.Zero;
            }
            if (_font != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(_font);
                _font = IntPtr.Zero;
            }
        }

        private static class NativeMethods
        {
            internal const int ERROR_CLASS_ALREADY_EXISTS = 1410;
            internal const uint WS_POPUP = 0x80000000;
            internal const uint WS_EX_TOPMOST = 0x00000008;
            internal const uint WS_EX_TRANSPARENT = 0x00000020;
            internal const uint WS_EX_TOOLWINDOW = 0x00000080;
            internal const uint WS_EX_LAYERED = 0x00080000;
            internal const uint WS_EX_NOACTIVATE = 0x08000000;
            internal const uint LWA_ALPHA = 0x00000002;
            internal const uint SWP_NOACTIVATE = 0x0010;
            internal const uint SWP_SHOWWINDOW = 0x0040;
            internal const int SW_SHOWNOACTIVATE = 4;
            internal const int SM_CXSCREEN = 0;
            internal const uint WM_PAINT = 0x000F;
            internal const uint WM_ERASEBKGND = 0x0014;
            internal const int TRANSPARENT = 1;
            internal const uint DT_CENTER = 0x00000001;
            internal const uint DT_VCENTER = 0x00000004;
            internal const uint DT_SINGLELINE = 0x00000020;
            internal const uint DT_NOPREFIX = 0x00000800;
            internal static readonly IntPtr HWND_TOPMOST = new(-1);

            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            internal delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct WNDCLASSEXW
            {
                internal uint cbSize;
                internal uint style;
                [MarshalAs(UnmanagedType.FunctionPtr)] internal WndProc lpfnWndProc;
                internal int cbClsExtra;
                internal int cbWndExtra;
                internal IntPtr hInstance;
                internal IntPtr hIcon;
                internal IntPtr hCursor;
                internal IntPtr hbrBackground;
                [MarshalAs(UnmanagedType.LPWStr)] internal string? lpszMenuName;
                [MarshalAs(UnmanagedType.LPWStr)] internal string lpszClassName;
                internal IntPtr hIconSm;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct RECT
            {
                internal int Left;
                internal int Top;
                internal int Right;
                internal int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct PAINTSTRUCT
            {
                internal IntPtr hdc;
                [MarshalAs(UnmanagedType.Bool)] internal bool fErase;
                internal RECT rcPaint;
                [MarshalAs(UnmanagedType.Bool)] internal bool fRestore;
                [MarshalAs(UnmanagedType.Bool)] internal bool fIncUpdate;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] rgbReserved;
            }

            internal static uint Rgb(byte r, byte g, byte b) =>
                (uint)(r | (g << 8) | (b << 16));

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr CreateWindowExW(
                uint dwExStyle,
                string lpClassName,
                string lpWindowName,
                uint dwStyle,
                int x,
                int y,
                int nWidth,
                int nHeight,
                IntPtr hWndParent,
                IntPtr hMenu,
                IntPtr hInstance,
                IntPtr lpParam);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool DestroyWindow(IntPtr hwnd);

            [DllImport("user32.dll")]
            internal static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

            [DllImport("user32.dll")]
            internal static extern int GetSystemMetrics(int nIndex);

            [DllImport("user32.dll")]
            internal static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT paint);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT paint);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

            [DllImport("user32.dll")]
            internal static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            internal static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);

            [DllImport("gdi32.dll")]
            internal static extern IntPtr CreateSolidBrush(uint colorRef);

            [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
            internal static extern IntPtr CreateFontW(
                int height, int width, int escapement, int orientation, int weight,
                uint italic, uint underline, uint strikeOut, uint charSet,
                uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily,
                string faceName);

            [DllImport("gdi32.dll")]
            internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

            [DllImport("gdi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool DeleteObject(IntPtr obj);

            [DllImport("gdi32.dll")]
            internal static extern int SetBkMode(IntPtr hdc, int mode);

            [DllImport("gdi32.dll")]
            internal static extern uint SetTextColor(IntPtr hdc, uint colorRef);
        }
    }
}
