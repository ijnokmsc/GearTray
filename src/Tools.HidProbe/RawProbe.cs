using System.Text;
using GearTray.Transport.Hid;

namespace GearTray.Tools.HidProbe;

/// <summary>
/// 通用原始帧探测：不绑定任何厂商，靠 --page 选接口、--raw 给字节。
///
/// 存在的理由：VgnProbe / MchoseProbe 都写死了自己的 usage page，
/// 而新设备（比如 V9 Turbo+ 耳机）有 4 条厂商通道，必须先摸清哪条能应答。
/// 每接一台新设备就复制一份探测代码是不可接受的。
/// </summary>
internal static class RawProbe
{
    public static void Run(IReadOnlyList<HidDeviceInfo> devices, ushort page, byte[] frame, int timeoutMs, bool repeat)
    {
        var target = devices.FirstOrDefault(d => d.UsagePage == page && d.InputReportLength > 0);
        if (target is null)
        {
            Console.WriteLine($"no interface with page 0x{page:X4} and an input report.");
            return;
        }

        using var connection = HidConnection.Open(target.Path);
        if (connection is null)
        {
            Console.WriteLine($"open failed: 0x{page:X4}");
            return;
        }

        using var channel = HidInputChannel.Open(target.Path, target.InputReportLength);
        if (channel is null)
        {
            Console.WriteLine($"input channel unavailable: 0x{page:X4}");
            return;
        }

        Console.WriteLine($"=== page=0x{target.UsagePage:X4} in={target.InputReportLength} out={target.OutputReportLength} feat={target.FeatureReportLength}");

        var runs = repeat ? 3 : 1;

        for (var run = 1; run <= runs; run++)
        {
            channel.Drain();

            // 补齐到接口声明的 output 长度。首字节由调用方给出（它就是 Report ID），
            // 这里绝不替调用方猜 —— 猜错 Report ID 会直接 win32 87。
            var buffer = new byte[Math.Max(target.OutputReportLength, frame.Length)];
            frame.CopyTo(buffer, 0);

            Console.WriteLine();
            Console.WriteLine($"--- run {run} tx {ToHex(buffer)}");

            if (!connection.TryWriteOutput(buffer, out var error))
            {
                Console.WriteLine($"  write rejected (win32 {error})");
                continue;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var seen = 0;

            while (DateTime.UtcNow < deadline)
            {
                var report = channel.Read(TimeSpan.FromMilliseconds(150));
                if (report is null)
                {
                    continue;
                }

                seen++;
                Dump(report, $"rx #{seen}");

                // 完全一致就是 HID 栈回显，继续等真应答。
                if (report.Length >= buffer.Length && report.AsSpan(0, buffer.Length).SequenceEqual(buffer))
                {
                    Console.WriteLine("    (identical echo)");
                }
            }

            if (seen == 0)
            {
                Console.WriteLine("  no input traffic at all");
            }
        }
    }

    /// <summary>只发不收：用来验证某个接口是否会在没有命令时自行上报（部分 dongle 会）。</summary>
    public static void Listen(IReadOnlyList<HidDeviceInfo> devices, ushort page, int seconds)
    {
        var target = devices.FirstOrDefault(d => d.UsagePage == page && d.InputReportLength > 0);
        if (target is null)
        {
            Console.WriteLine($"no interface with page 0x{page:X4} and an input report.");
            return;
        }

        using var channel = HidInputChannel.Open(target.Path, target.InputReportLength);
        if (channel is null)
        {
            Console.WriteLine("input channel unavailable");
            return;
        }

        Console.WriteLine($"=== listening 0x{page:X4} for {seconds}s (zero writes)");
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var count = 0;

        while (DateTime.UtcNow < deadline)
        {
            var report = channel.Read(TimeSpan.FromMilliseconds(200));
            if (report is null)
            {
                continue;
            }

            count++;
            Dump(report, $"rx #{count}");
        }

        Console.WriteLine($"total {count} report(s)");
    }

    private static void Dump(byte[] data, string label)
    {
        Console.WriteLine($"  {label} len={data.Length}");
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var count = Math.Min(16, data.Length - offset);
            var builder = new StringBuilder($"    {offset:X4}  ");
            for (var i = 0; i < count; i++)
            {
                builder.Append($"{data[offset + i]:X2} ");
            }

            builder.Append("  |");
            for (var i = 0; i < count; i++)
            {
                var value = data[offset + i];
                builder.Append(value is >= 0x20 and < 0x7F ? (char)value : '.');
            }

            builder.Append('|');
            Console.WriteLine(builder.ToString());
        }
    }

    private static string ToHex(byte[] data)
    {
        var builder = new StringBuilder();
        foreach (var b in data)
        {
            builder.Append($"{b:X2} ");
        }

        return builder.ToString().TrimEnd();
    }
}
