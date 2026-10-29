using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using GearTray.Core;

namespace GearTray.Host.Tray;

/// <summary>
/// 托盘宿主：一个隐藏窗口收消息，Shell_NotifyIcon 挂图标，
/// 右键菜单走原生 TrackPopupMenuEx（比 WPF ContextMenu 更稳，不受可视化树影响）。
/// </summary>
internal sealed class TrayHost : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;
    private const int WM_NULL = 0x0000;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int CmdShowPanel = 1001;
    private const int CmdRefresh = 1002;
    private const int CmdExit = 1003;

    private const int TrayIconId = 1;

    private readonly HwndSource _source;
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _icon;
    private string _tooltip = "2.4G 设备电量";

    public TrayHost()
    {
        var parameters = new HwndSourceParameters("GearTray.Tray")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP：不显示，只收消息
            ExtendedWindowStyle = 0x00000080,         // WS_EX_TOOLWINDOW
            Width = 1,
            Height = 1,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
    }

    public event Action? ShowPanelRequested;

    public event Action? RefreshRequested;

    public event Action? ExitRequested;

    public void Install()
    {
        _icon = TrayIconRenderer.Create(null, true);

        var data = CreateData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.hIcon = _icon;
        data.szTip = _tooltip;

        Shell_NotifyIconW(NIM_ADD, ref data);
    }

    /// <summary>把新的设备快照反映到图标与 tooltip 上。必须在 UI 线程调用。</summary>
    public void Apply(IReadOnlyList<DeviceSnapshot> snapshots)
    {
        int? percent = null;
        var stale = false;

        var withBattery = snapshots.Where(s => s.Battery.Percent.HasValue).ToList();
        if (withBattery.Count > 0)
        {
            // 图标显示「最危险的那台」，多设备时最有信息量。
            var lowest = withBattery.OrderBy(s => s.Battery.Percent!.Value).First().Battery;
            percent = lowest.Percent;
            stale = lowest.IsStale;
        }

        _tooltip = BuildTooltip(snapshots);

        var newIcon = TrayIconRenderer.Create(percent, stale);
        if (newIcon != IntPtr.Zero)
        {
            var data = CreateData();
            data.uFlags = NIF_ICON | NIF_TIP;
            data.hIcon = newIcon;
            data.szTip = _tooltip;

            if (!Shell_NotifyIconW(NIM_MODIFY, ref data))
            {
                // 图标被删过（explorer 重启等）时 NIM_MODIFY 改不回来，必须重新 NIM_ADD。
                Shell_NotifyIconW(NIM_ADD, ref data);
            }

            if (_icon != IntPtr.Zero)
            {
                TrayIconRenderer.DestroyIcon(_icon);
            }

            _icon = newIcon;
        }
        else
        {
            var data = CreateData();
            data.uFlags = NIF_TIP;
            data.szTip = _tooltip;
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
    }

    private static string BuildTooltip(IReadOnlyList<DeviceSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return "2.4G 设备电量\n未发现设备";
        }

        var builder = new StringBuilder();
        builder.Append("2.4G 设备电量\n");

        foreach (var snapshot in snapshots.Take(4))
        {
            var name = snapshot.Identity.DisplayName;
            if (name.Length > 20)
            {
                name = name[..20];
            }

            builder.Append(name).Append("：").Append(snapshot.Battery.Display);
            if (snapshot.Battery.IsStale && snapshot.Battery.Percent is not null)
            {
                builder.Append("（旧值）");
            }

            builder.Append('\n');
        }

        if (snapshots.Count > 4)
        {
            builder.Append("…还有 ").Append(snapshots.Count - 4).Append(" 台");
        }

        var text = builder.ToString().TrimEnd('\n');

        // szTip 上限 128 字符（含结尾 null）。
        return text.Length > 120 ? text[..120] : text;
    }

    private NOTIFYICONDATAW CreateData()
    {
        return new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _source.Handle,
            uID = TrayIconId,
            uCallbackMessage = WM_TRAYICON,
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            var mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);

            switch (mouseMessage)
            {
                case WM_LBUTTONUP:
                    ShowPanelRequested?.Invoke();
                    handled = true;
                    break;

                case WM_RBUTTONUP:
                    ShowMenu();
                    handled = true;
                    break;
            }

            return IntPtr.Zero;
        }

        if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            // explorer 重启后图标会消失，必须重新注册。
            Install();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenuW(menu, MF_STRING, CmdShowPanel, "显示面板");
            AppendMenuW(menu, MF_STRING, CmdRefresh, "立即刷新");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, CmdExit, "退出");

            GetCursorPos(out var point);

            // 不先 SetForegroundWindow，菜单会点不掉（经典 Win32 坑）。
            SetForegroundWindow(_source.Handle);

            var command = TrackPopupMenuEx(
                menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, _source.Handle, IntPtr.Zero);

            PostMessageW(_source.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case CmdShowPanel:
                    ShowPanelRequested?.Invoke();
                    break;

                case CmdRefresh:
                    RefreshRequested?.Invoke();
                    break;

                case CmdExit:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        var data = CreateData();
        Shell_NotifyIconW(NIM_DELETE, ref data);

        if (_icon != IntPtr.Zero)
        {
            TrayIconRenderer.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, int itemId, string? item);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(
        IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
