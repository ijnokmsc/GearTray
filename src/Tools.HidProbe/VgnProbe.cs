using System.Text;
using GearTray.Transport.Hid;

namespace GearTray.Tools.HidProbe;

/// <summary>
/// VGN 键盘通道探测。
///
/// 之前的结论「键盘既不主动上报、Feature 也全空」是错的：那次只试了 Report ID 0..3。
/// 而拓扑里接口 1（page 0x0001）声明了 **65 字节 Feature Report** —— 说明存在 ID ≥ 4 的
/// Feature 报文。厂商通道很可能就在那里，和 MCHOSE 一样走 Feature 而不是 Output。
///
/// 读 Feature 是无副作用的，所以先全量扫一遍再谈写。
/// </summary>
internal static class VgnProbe
{
    private static readonly byte[] KnownGoodInit = [0x04, 0x8c, 0x00, 0x0b, 0x30, 0x50, 0x01];

    public static void ScanFeature(IReadOnlyList<HidDeviceInfo> devices)
    {
        var targets = devices.Where(d => d.FeatureReportLength >= 2).ToList();
        if (targets.Count == 0)
        {
            Console.WriteLine("no interface declares a feature report.");
            return;
        }

        foreach (var device in targets)
        {
            Console.WriteLine();
            Console.WriteLine($"=== page=0x{device.UsagePage:X4} feat={device.FeatureReportLength} in={device.InputReportLength} out={device.OutputReportLength}");

            using var connection = HidConnection.Open(device.Path);
            if (connection is null)
            {
                Console.WriteLine("  open failed");
                continue;
            }

            var hits = 0;
            for (byte id = 0; id <= 0x20; id++)
            {
                var data = connection.GetFeature(id, device.FeatureReportLength);
                if (data is null || data.Length == 0)
                {
                    continue;
                }

                // 去掉首字节（Report ID）后判断是否全 0x00 / 全 0xFF —— 那是「驱动返回了但设备没内容」。
                var payload = data.AsSpan(1);
                var allZero = true;
                var allOnes = true;
                foreach (var b in payload)
                {
                    if (b != 0x00)
                    {
                        allZero = false;
                    }

                    if (b != 0xFF)
                    {
                        allOnes = false;
                    }
                }

                var tag = allZero ? " (all 00)" : allOnes ? " (all FF)" : string.Empty;
                Console.WriteLine($"  id 0x{id:X2}: {ToHex(data)}{tag}");
                hits++;
            }

            Console.WriteLine(hits == 0 ? "  no feature report responded" : $"  {hits} report id(s) responded");
        }
    }

    /// <summary>
    /// 只听：打开 0xFF1C 的输入通道，先发一条社区脚本里验证过的初始化帧，
    /// 再看键盘是否通过 input report 回应。没有回应就说明这条通道只写不读。
    /// </summary>
    public static void Listen(IReadOnlyList<HidDeviceInfo> devices, int seconds)
    {
        var target = devices.FirstOrDefault(d => d.UsagePage == 0xFF1C && d.InputReportLength > 0);
        if (target is null)
        {
            Console.WriteLine("no vendor interface (0xFF1C) with an input report.");
            return;
        }

        using var connection = HidConnection.Open(target.Path);
        if (connection is null)
        {
            Console.WriteLine("open failed");
            return;
        }

        using var channel = HidInputChannel.Open(target.Path, target.InputReportLength);
        if (channel is null)
        {
            Console.WriteLine("input channel unavailable");
            return;
        }

        Console.WriteLine($"=== page=0x{target.UsagePage:X4} in={target.InputReportLength} out={target.OutputReportLength}");
        Console.WriteLine($"  tx init frame: {ToHex(KnownGoodInit)}");

        if (!connection.TryWriteOutput(KnownGoodInit, out var error))
        {
            Console.WriteLine($"  write rejected (win32 {error})");
            return;
        }

        Console.WriteLine($"  listening {seconds}s ...");
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var count = 0;

        while (DateTime.UtcNow < deadline)
        {
            var report = channel.Read(TimeSpan.FromMilliseconds(250));
            if (report is null)
            {
                continue;
            }

            Console.WriteLine($"  rx  {ToHex(report)}");
            count++;
        }

        Console.WriteLine($"  frames received: {count}");
    }

    /// <summary>
    /// ⚠️ 已失效，保留作对照。这是**网页驱动**那套 `[0x55][cmd]` 帧，本机 N75 PRO 只回 `04 55 03 FF`。
    /// 可用协议见 <see cref="VgnWsProbe"/>（VHUB 桌面端 `WsServe`）。
    ///
    /// 原以为的协议：
    ///   请求 [0x04 ReportId][0x55 魔数][命令][参数…] 补零到 64
    ///   应答 [0x04 ReportId][命令][标志] ...
    /// </summary>
    private const byte VendorReportId = 0x04;

    private const byte CommandMagic = 0x55;

    private const byte DeviceOnLineCommand = 0x03;

    private const byte BatteryLevelCommand = 0x04;

    public static void QueryBattery(IReadOnlyList<HidDeviceInfo> devices)
    {
        var target = devices.FirstOrDefault(d => d.UsagePage == 0xFF1C && d.InputReportLength > 0);
        if (target is null)
        {
            Console.WriteLine("no vendor interface (0xFF1C) with an input report.");
            return;
        }

        using var connection = HidConnection.Open(target.Path);
        if (connection is null)
        {
            Console.WriteLine("open failed");
            return;
        }

        using var channel = HidInputChannel.Open(target.Path, target.InputReportLength);
        if (channel is null)
        {
            Console.WriteLine("input channel unavailable");
            return;
        }

        Console.WriteLine($"=== page=0x{target.UsagePage:X4} in={target.InputReportLength} out={target.OutputReportLength}");

        for (var run = 1; run <= 3; run++)
        {
            Console.WriteLine();
            Console.WriteLine($"--- run {run} ---");

            if (!SendAndWait(connection, channel, DeviceOnLineCommand, TimeSpan.FromMilliseconds(1500), out var status))
            {
                Console.WriteLine("  DeviceOnLine: no reply");
                continue;
            }

            // 去 Report ID 后 [5] 是 online；原始 buffer 上是 [6]。
            var online = status![6] == 1;
            Console.WriteLine($"  DeviceOnLine: online={online} addr={status[9]:X2}{status[8]:X2}{status[7]:X2}");

            if (!online)
            {
                Console.WriteLine("  keyboard reports offline — 键可能处于休眠，按一下键再试");
                continue;
            }

            if (!SendAndWait(connection, channel, BatteryLevelCommand, TimeSpan.FromMilliseconds(1500), out var battery))
            {
                Console.WriteLine("  BatteryLevel: no reply");
                continue;
            }

            var level = battery![6];
            var charging = battery[7] == 1;
            var voltage = (battery[8] << 8) | battery[9];

            Console.WriteLine($"  BatteryLevel: {level}% charging={charging} voltage={voltage}mV");
            Console.WriteLine($"  raw: {ToHex(battery.AsSpan(0, 12))}");
        }
    }

    /// <summary>
    /// 直接注入一条原始帧（十六进制），打印收到的全部 input report。
    ///
    /// 用来快速穷举「magic 有没有/是什么、要不要带 report id、payload 长什么样」，
    /// 不必每试一种就改一次代码。
    /// </summary>
    public static void SendRaw(IReadOnlyList<HidDeviceInfo> devices, byte[] bytes, int waitMs)
    {
        var target = devices.FirstOrDefault(d => d.UsagePage == 0xFF1C && d.InputReportLength > 0);
        if (target is null)
        {
            Console.WriteLine("no vendor interface (0xFF1C) with an input report.");
            return;
        }

        using var connection = HidConnection.Open(target.Path);
        if (connection is null)
        {
            Console.WriteLine("open failed");
            return;
        }

        using var channel = HidInputChannel.Open(target.Path, target.InputReportLength);
        if (channel is null)
        {
            Console.WriteLine("input channel unavailable");
            return;
        }

        channel.Drain();

        Console.WriteLine($"=== tx {ToHex(bytes)}");

        if (!connection.TryWriteOutput(bytes, out var error))
        {
            Console.WriteLine($"  write rejected (win32 {error})");
            return;
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
        var count = 0;

        while (DateTime.UtcNow < deadline)
        {
            var report = channel.Read(TimeSpan.FromMilliseconds(150));
            if (report is null)
            {
                continue;
            }

            Console.WriteLine($"  rx {ToHex(report.AsSpan(0, Math.Min(20, report.Length)))}");
            count++;
        }

        if (count == 0)
        {
            Console.WriteLine("  no report received");
        }
    }

    /// <summary>发一条命令并等它的应答。用 [1]==cmd 区分真应答与 HID 栈回显（回显的 [1] 是魔数 0x55）。</summary>
    private static bool SendAndWait(
        HidConnection connection,
        HidInputChannel channel,
        byte command,
        TimeSpan timeout,
        out byte[]? reply)
    {
        reply = null;
        channel.Drain();

        var frame = new byte[connection.Info.OutputReportLength];
        frame[0] = VendorReportId;
        frame[1] = CommandMagic;
        frame[2] = command;

        if (!connection.TryWriteOutput(frame, out var error))
        {
            Console.WriteLine($"  write rejected (win32 {error})");
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var report = channel.Read(TimeSpan.FromMilliseconds(200));
            if (report is null || report.Length < 3)
            {
                continue;
            }

            if (report[0] == VendorReportId && report[1] == command)
            {
                reply = report;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 通用命令探测：发一条 0x55 帧，dump 完整应答。
    ///
    /// 用来解决坐标歧义：GetDeviceStatus 的应答里版本号是 ASCII，
    /// 它在整体 buffer 里的绝对下标能反推「官方 SDK 拿到的 receivedData 到底从哪开始」，
    /// 从而确定电量该读 [0] 还是 [1]。
    /// </summary>
    public static void ProbeCommand(IReadOnlyList<HidDeviceInfo> devices, byte[] commands)
    {
        var target = devices.FirstOrDefault(d => d.UsagePage == 0xFF1C && d.InputReportLength > 0);
        if (target is null)
        {
            Console.WriteLine("no vendor interface (0xFF1C) with an input report.");
            return;
        }

        using var connection = HidConnection.Open(target.Path);
        if (connection is null)
        {
            Console.WriteLine("open failed");
            return;
        }

        using var channel = HidInputChannel.Open(target.Path, target.InputReportLength);
        if (channel is null)
        {
            Console.WriteLine("input channel unavailable");
            return;
        }

        Console.WriteLine($"=== page=0x{target.UsagePage:X4} in={target.InputReportLength} out={target.OutputReportLength}");

        foreach (var command in commands)
        {
            channel.Drain();

            var frame = new byte[target.OutputReportLength];
            frame[0] = VendorReportId;
            frame[1] = CommandMagic;
            frame[2] = command;

            Console.WriteLine();
            Console.WriteLine($"--- cmd 0x{command:X2} ({command}) ---");

            if (!connection.TryWriteOutput(frame, out var error))
            {
                Console.WriteLine($"  write rejected (win32 {error})");
                continue;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(1000);
            byte[]? reply = null;

            while (DateTime.UtcNow < deadline)
            {
                var report = channel.Read(TimeSpan.FromMilliseconds(200));
                if (report is null)
                {
                    continue;
                }

                // 只丢弃「完全一致」的回显：设备只要动了任何一个字节就是真应答。
                if (report.Length >= frame.Length && report.AsSpan(0, frame.Length).SequenceEqual(frame))
                {
                    continue;
                }

                reply = report;
                break;
            }

            if (reply is null)
            {
                Console.WriteLine("  no reply");
                continue;
            }

            HexDump(reply);
            Console.WriteLine($"  ascii: {ToAscii(reply)}");
        }
    }

    private static void HexDump(byte[] data)
    {
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var count = Math.Min(16, data.Length - offset);
            var builder = new StringBuilder();
            builder.Append($"  {offset:X4}  ");

            for (var i = 0; i < count; i++)
            {
                builder.Append(data[offset + i].ToString("X2")).Append(' ');
            }

            builder.Append(new string(' ', (16 - count) * 3)).Append('|');
            for (var i = 0; i < count; i++)
            {
                var b = data[offset + i];
                builder.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            builder.Append('|');
            Console.WriteLine(builder.ToString());
        }
    }

    private static string ToAscii(byte[] data)
    {
        var builder = new StringBuilder();
        foreach (var b in data)
        {
            builder.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        return builder.ToString();
    }

    private static string ToHex(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder(data.Length * 3);
        foreach (var b in data)
        {
            builder.Append(b.ToString("X2")).Append(' ');
        }

        return builder.ToString().TrimEnd();
    }
}
