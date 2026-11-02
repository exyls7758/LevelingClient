using System;
using System.Runtime.InteropServices;

namespace Multiverse.Services
{
    /// <summary>
    /// Minimal native notification-area icon. No WinForms dependency is added to the
    /// WinUI project, and the message window remains hidden for the process lifetime.
    /// </summary>
    internal sealed class WindowsTrayIconService : IDisposable
    {
        private const uint TrayIconId = 1;
        private const uint CallbackMessage = NativeMethods.WM_APP + 41;
        private const uint OpenCommand = 1001;
        private const uint ExitCommand = 1002;

        private readonly Action _openAction;
        private readonly Action _exitAction;
        private readonly NativeMethods.WndProc _wndProc;
        private readonly string _className;
        private IntPtr _messageWindow;
        private IntPtr _icon;
        private bool _iconVisible;
        private bool _disposed;

        public WindowsTrayIconService(Action openAction, Action exitAction)
        {
            _openAction = openAction ?? throw new ArgumentNullException(nameof(openAction));
            _exitAction = exitAction ?? throw new ArgumentNullException(nameof(exitAction));
            _wndProc = WindowProc;
            _className = $"LevelingTray_{Environment.ProcessId}";
            CreateMessageWindow();
            LoadApplicationIcon();
        }

        public bool Show()
        {
            if (_disposed || _iconVisible || _messageWindow == IntPtr.Zero || _icon == IntPtr.Zero)
                return _iconVisible;

            NativeMethods.NOTIFYICONDATAW data = CreateNotifyData();
            _iconVisible = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data);
            return _iconVisible;
        }

        public void Hide()
        {
            if (!_iconVisible || _messageWindow == IntPtr.Zero)
                return;

            NativeMethods.NOTIFYICONDATAW data = CreateNotifyData();
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
            _iconVisible = false;
        }

        private NativeMethods.NOTIFYICONDATAW CreateNotifyData() => new()
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _messageWindow,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = "Leveling",
            szInfo = "",
            szInfoTitle = ""
        };

        private void CreateMessageWindow()
        {
            IntPtr instance = NativeMethods.GetModuleHandleW(null);
            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                lpfnWndProc = _wndProc,
                hInstance = instance,
                lpszClassName = _className
            };

            ushort atom = NativeMethods.RegisterClassExW(ref wc);
            if (atom == 0 && Marshal.GetLastWin32Error() != NativeMethods.ERROR_CLASS_ALREADY_EXISTS)
                return;

            _messageWindow = NativeMethods.CreateWindowExW(
                NativeMethods.WS_EX_TOOLWINDOW,
                _className,
                "Leveling Tray",
                0,
                0, 0, 0, 0,
                IntPtr.Zero,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);
        }

        private void LoadApplicationIcon()
        {
            string? executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                return;

            uint count = NativeMethods.ExtractIconExW(executable, 0, out IntPtr large, out IntPtr small, 1);
            if (count == 0)
                return;

            if (small != IntPtr.Zero)
            {
                _icon = small;
                if (large != IntPtr.Zero)
                    NativeMethods.DestroyIcon(large);
            }
            else
            {
                _icon = large;
            }
        }

        private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == CallbackMessage)
            {
                uint mouseMessage = unchecked((uint)lParam.ToInt64());
                if (mouseMessage == NativeMethods.WM_LBUTTONDBLCLK)
                {
                    _openAction();
                    return IntPtr.Zero;
                }

                if (mouseMessage == NativeMethods.WM_RBUTTONUP || mouseMessage == NativeMethods.WM_CONTEXTMENU)
                {
                    ShowContextMenu();
                    return IntPtr.Zero;
                }
            }

            return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        private void ShowContextMenu()
        {
            if (_messageWindow == IntPtr.Zero)
                return;

            IntPtr menu = NativeMethods.CreatePopupMenu();
            if (menu == IntPtr.Zero)
                return;

            try
            {
                NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, OpenCommand, "Abrir Leveling");
                NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);
                NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, ExitCommand, "Sair");
                NativeMethods.GetCursorPos(out NativeMethods.POINT point);
                NativeMethods.SetForegroundWindow(_messageWindow);

                uint command = NativeMethods.TrackPopupMenu(
                    menu,
                    NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY,
                    point.X,
                    point.Y,
                    0,
                    _messageWindow,
                    IntPtr.Zero);

                if (command == OpenCommand)
                    _openAction();
                else if (command == ExitCommand)
                    _exitAction();
            }
            finally
            {
                NativeMethods.DestroyMenu(menu);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            Hide();
            if (_icon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_icon);
                _icon = IntPtr.Zero;
            }
            if (_messageWindow != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_messageWindow);
                _messageWindow = IntPtr.Zero;
            }
        }

        private static class NativeMethods
        {
            internal const int ERROR_CLASS_ALREADY_EXISTS = 1410;
            internal const uint WM_APP = 0x8000;
            internal const uint WM_LBUTTONDBLCLK = 0x0203;
            internal const uint WM_RBUTTONUP = 0x0205;
            internal const uint WM_CONTEXTMENU = 0x007B;
            internal const uint WS_EX_TOOLWINDOW = 0x00000080;
            internal const uint NIM_ADD = 0x00000000;
            internal const uint NIM_DELETE = 0x00000002;
            internal const uint NIF_MESSAGE = 0x00000001;
            internal const uint NIF_ICON = 0x00000002;
            internal const uint NIF_TIP = 0x00000004;
            internal const uint MF_STRING = 0x00000000;
            internal const uint MF_SEPARATOR = 0x00000800;
            internal const uint TPM_RIGHTBUTTON = 0x0002;
            internal const uint TPM_RETURNCMD = 0x0100;
            internal const uint TPM_NONOTIFY = 0x0080;

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

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct NOTIFYICONDATAW
            {
                internal uint cbSize;
                internal IntPtr hWnd;
                internal uint uID;
                internal uint uFlags;
                internal uint uCallbackMessage;
                internal IntPtr hIcon;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string szTip;
                internal uint dwState;
                internal uint dwStateMask;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string szInfo;
                internal uint uTimeoutOrVersion;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string szInfoTitle;
                internal uint dwInfoFlags;
                internal Guid guidItem;
                internal IntPtr hBalloonIcon;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct POINT
            {
                internal int X;
                internal int Y;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            internal static extern IntPtr GetModuleHandleW(string? moduleName);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern ushort RegisterClassExW(ref WNDCLASSEXW wndClass);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr CreateWindowExW(
                uint exStyle,
                string className,
                string windowName,
                uint style,
                int x,
                int y,
                int width,
                int height,
                IntPtr parent,
                IntPtr menu,
                IntPtr instance,
                IntPtr param);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool DestroyWindow(IntPtr hwnd);

            [DllImport("user32.dll")]
            internal static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            internal static extern uint ExtractIconExW(
                string file,
                int iconIndex,
                out IntPtr largeIcon,
                out IntPtr smallIcon,
                uint iconCount);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool DestroyIcon(IntPtr icon);

            [DllImport("user32.dll")]
            internal static extern IntPtr CreatePopupMenu();

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool AppendMenuW(IntPtr menu, uint flags, uint newItem, string? text);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool DestroyMenu(IntPtr menu);

            [DllImport("user32.dll")]
            internal static extern uint TrackPopupMenu(
                IntPtr menu,
                uint flags,
                int x,
                int y,
                int reserved,
                IntPtr hwnd,
                IntPtr rect);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetCursorPos(out POINT point);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetForegroundWindow(IntPtr hwnd);
        }
    }
}
