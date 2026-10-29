using System.Runtime.InteropServices;

namespace GearTray.Host.Input;

/// <summary>
/// Win 键锁定的自动化验证。
///
/// 判据：用 SendInput 注入一次 VK_LWIN 按下，然后读 GetAsyncKeyState 的最高位。
///   - 未加锁：系统吃到这次按下，最高位为 1
///   - 已加锁：钩子把它吞了，系统从未认为 Win 键被按下，最高位为 0
/// 这个判据不依赖「开始菜单有没有弹出来」—— 那个受焦点、全屏、系统设置影响，
/// 在自动化环境里不稳定。
///
/// 低级键盘钩子会收到 SendInput 注入的按键（带 LLKHF_INJECTED 标志），所以可以这样测。
/// </summary>
internal static class WinKeyLockDiagnostics
{
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_ESCAPE = 0x1B;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static void Run()
    {
        Console.WriteLine("winkey lock diagnostics");
        Console.WriteLine(new string('-', 60));

        using var service = new WinKeyLockService();

        Console.WriteLine($"  mode        : {service.Mode}");
        Console.WriteLine($"  locked@start: {service.IsLocked}  (期望 False —— 钩子必须按需安装)");

        // 基线：不加锁时必须能观察到 Win 键按下，否则说明 SendInput 本身没生效，
        // 后面「加锁后看不到」就不能算证据。
        var baseline = PressAndProbe();
        Console.WriteLine($"  baseline    : win-down registered = {baseline}  (期望 True)");

        var enabled = service.SetLocked(true);
        Console.WriteLine($"  lock enable : {enabled}  (期望 True，此时才装钩子)");

        var locked = PressAndProbe();
        Console.WriteLine($"  locked      : win-down registered = {locked}  (期望 False)");

        service.SetLocked(false);
        var unlocked = PressAndProbe();
        Console.WriteLine($"  unlocked    : win-down registered = {unlocked}  (期望 True)");

        Console.WriteLine(new string('-', 60));

        if (!baseline)
        {
            Console.WriteLine("INCONCLUSIVE: 基线没观察到 Win 键，可能被策略/远程会话屏蔽，本机无法验证。");
            return;
        }

        Console.WriteLine(locked == false && unlocked
            ? "VERIFIED: 钩子确实吞掉了 Win 键，且解锁后恢复"
            : "FAILED: 锁定期间仍能观察到 Win 键");
    }

    /// <summary>按下 Win 键、读系统状态、再抬起。返回系统是否认为 Win 被按下了。</summary>
    private static bool PressAndProbe()
    {
        SendKey(VK_LWIN, up: false);
        Thread.Sleep(120);

        var down = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0;

        SendKey(VK_LWIN, up: true);
        Thread.Sleep(80);

        if (down)
        {
            // 未拦截时开始菜单可能已经弹出来了，发 Esc 收掉，避免干扰桌面。
            SendKey(VK_ESCAPE, up: false);
            SendKey(VK_ESCAPE, up: true);
            Thread.Sleep(120);
        }

        return down;
    }

    private static void SendKey(ushort vk, bool up)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = up ? KEYEVENTF_KEYUP : 0,
                },
            },
        };

        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    // 必须是最大成员的联合体大小，否则 SendInput 会按错误尺寸读越界。
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
