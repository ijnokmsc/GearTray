using System.Threading;
using GearTray.Core;

namespace GearTray.Host.Input;

/// <summary>
/// Windows 键锁定（软件态）。
///
/// 为什么不是固件态：MCHOSE 这套 Feature Report 协议里没有独立的 Win 锁命令 ——
/// 命令表只有电量 / 回报率 / DPI / 传感器 / 去抖 / 休眠 / 键位重映射。
/// Win 锁在厂商驱动里是通过「把 Win 键重映射成无功能」实现的，那是改键位表，
/// 写错会牵连其它按键。所以在拿到可靠的 remap 报文格式之前，这里老实用全局键盘钩子。
///
/// 必须如实标注：本实现只在本程序运行时生效，换一台机器、或者退出本程序，锁就没了。
/// 这和「写进鼠标固件」是两种东西，UI 上不能混为一谈。
///
/// 实现要点：
///   1. 低级键盘钩子必须在**装有消息循环的线程**上安装，且回调会在该线程执行。
///      放在 WPF UI 线程会被面板动画/布局卡住，超过 LowLevelHooksTimeout 后
///      系统会把钩子静默摘掉（不报错，只是失效），所以这里单开一条线程跑消息泵。
///   2. 回调委托必须用字段持有。只传局部变量/匿名方法，GC 一回收就是访问违例。
///   3. 拦截 VK_LWIN / VK_RWIN 的 down 与 up：只拦 down 会让应用收到孤立的 keyup。
///   4. **钩子按需安装**。全局低级钩子会让每一次按键都跨线程回调到本进程，
///      不开锁却常驻就是白付输入延迟。所以锁打开才装、关掉立即卸。
/// </summary>
internal sealed class WinKeyLockService : IWinKeyLockControl, IDisposable
{
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WinKeyLockNative.LowLevelKeyboardProc _proc;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook = IntPtr.Zero;
    private volatile bool _locked;
    private bool _disposed;

    public WinKeyLockService()
    {
        // 字段持有，防止回调被 GC 回收。
        _proc = HookProc;
    }

    public WinKeyLockMode Mode => WinKeyLockMode.Software;

    public bool IsLocked => _locked;

    /// <summary>
    /// 只要没有安装失败过就认为可用。这里不能写成「钩子已装」——
    /// 锁关着的时候钩子本来就不该在，那不叫不可用。
    /// </summary>
    public bool IsAvailable => LastError is null;

    public string? LastError { get; private set; }

    public bool SetLocked(bool locked)
    {
        if (_disposed)
        {
            return false;
        }

        if (locked == _locked && (!locked || _hook != IntPtr.Zero))
        {
            return true;
        }

        if (locked)
        {
            if (!EnsureHook())
            {
                return false;
            }

            _locked = true;
            return true;
        }

        // 先解除拦截，再拆钩子：即使拆失败也不该继续吞按键。
        _locked = false;
        StopHook();
        return true;
    }

    private bool EnsureHook()
    {
        if (_hook != IntPtr.Zero)
        {
            return true;
        }

        _ready.Reset();

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "WinKeyLock",
        };

        // 钩子回调跑在这条线程上，优先级给高一点，减少被别的线程挤掉导致的输入延迟。
        _thread.Priority = ThreadPriority.AboveNormal;
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(3)))
        {
            LastError = "keyboard hook thread did not start in time";
            return false;
        }

        return _hook != IntPtr.Zero;
    }

    private void StopHook()
    {
        if (_threadId != 0)
        {
            WinKeyLockNative.PostThreadMessageW(_threadId, WinKeyLockNative.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));

        _thread = null;
        _threadId = 0;

        if (_hook != IntPtr.Zero)
        {
            WinKeyLockNative.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private void Pump()
    {
        _threadId = WinKeyLockNative.GetCurrentThreadId();

        // 先建消息队列，否则 StopHook 里的 PostThreadMessage 会失败。
        WinKeyLockNative.PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);

        _hook = WinKeyLockNative.SetWindowsHookExW(
            WinKeyLockNative.WH_KEYBOARD_LL,
            _proc,
            WinKeyLockNative.GetModuleHandleW(null),
            0);

        if (_hook == IntPtr.Zero)
        {
            LastError = $"SetWindowsHookEx failed (win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})";
            _ready.Set();
            return;
        }

        _ready.Set();

        while (true)
        {
            var result = WinKeyLockNative.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
            if (result == 0 || result == -1 || msg.Message == WinKeyLockNative.WM_QUIT)
            {
                break;
            }

            WinKeyLockNative.TranslateMessage(ref msg);
            WinKeyLockNative.DispatchMessageW(ref msg);
        }

        if (_hook != IntPtr.Zero)
        {
            WinKeyLockNative.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _locked)
        {
            var message = wParam.ToInt32();
            if (message is WinKeyLockNative.WM_KEYDOWN or WinKeyLockNative.WM_SYSKEYDOWN
                or WinKeyLockNative.WM_KEYUP or WinKeyLockNative.WM_SYSKEYUP)
            {
                var info = System.Runtime.InteropServices.Marshal.PtrToStructure<WinKeyLockNative.KbdLlHookStruct>(lParam);
                if (info.VkCode is WinKeyLockNative.VK_LWIN or WinKeyLockNative.VK_RWIN)
                {
                    // 非 0 即「吞掉这个按键」，不再传给其它钩子和系统。
                    return 1;
                }
            }
        }

        return WinKeyLockNative.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _locked = false;
        StopHook();
        _ready.Dispose();
    }
}
