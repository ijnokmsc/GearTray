using System.IO;
using System.Runtime.InteropServices;

namespace GearTray.Host;

/// <summary>
/// 给 WinExe 挂一个控制台，让诊断开关的输出可见。
/// WPF 项目是 WinExe 子系统，进程启动时没有控制台，
/// 直接 Console.WriteLine 会被丢掉（不是报错，是静默丢弃）。
/// </summary>
internal static class ConsoleHost
{
    public static void Attach()
    {
        // 从已存在的终端启动时复用父进程控制台，否则新开一个。
        if (!AttachConsole(AttachParentProcess))
        {
            AllocConsole();
        }

        var stdout = Console.OpenStandardOutput();
        var writer = new StreamWriter(stdout) { AutoFlush = true };
        Console.SetOut(writer);
        Console.SetError(writer);
    }

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}
