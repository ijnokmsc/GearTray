using System.Diagnostics;
using System.Text;
using GearTray.Transport.Hid;

namespace GearTray.Tools.HidProbe;

/// <summary>
/// M3 协议探测：先只听（零写入），再按候选帧发只读查询。
///
/// 铁律：任何会改变设备状态的写入（DPI、键锁、灯效）都不属于探测阶段。
/// 这里只发「查询电量」语义的帧，且不发未知 Report ID 的随机载荷。
/// </summary>
internal static class ProtocolProbe
{
    /// <summary>
    /// Nordic 方案（ATK / VXE 同族，MCHOSE 大概率沿用）的电量查询帧。
    /// 来自已公开的逆向结果：Report ID 0x08 + 7D 72 02 00 01 07 01，应答 resp[1]=0x72、resp[5]=0x07、电量 resp[7]。
    /// </summary>
    private static readonly byte[] NordicBatteryQuery = [0x7D, 0x72, 0x02, 0x00, 0x01, 0x07, 0x01];

    /// <summary>优先试的 Report ID：0x08 是已公开值，其余按 Nordic 固件常见分布排列。</summary>
    private static readonly byte[] LikelyReportIds = [0x08, 0x05, 0x06, 0x0A, 0x04, 0x0C, 0x00];

    /// <summary>
    /// 只听模式：打开每个接口的输入通道，把收到的报文打出来。零写入，绝对安全。
    /// 很多 dongle 会周期性主动上报电量，这一步常常能直接看到答案。
    /// </summary>
    public static void Listen(IReadOnlyList<HidDeviceInfo> devices, int seconds)
    {
        var channels = new List<(HidDeviceInfo Device, HidInputChannel Channel)>();

        foreach (var device in devices.Where(d => d.InputReportLength > 0))
        {
            var channel = HidInputChannel.Open(device.Path, device.InputReportLength);
            if (channel is null)
            {
                Console.WriteLine($"  [open failed] {device.VidPid} page=0x{device.UsagePage:X4}");
                continue;
            }

            channels.Add((device, channel));
            Console.WriteLine($"  [listening]   {device.VidPid} page=0x{device.UsagePage:X4} in={device.InputReportLength}");
        }

        if (channels.Count == 0)
        {
            Console.WriteLine("no readable interfaces.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"capturing for {seconds}s (zero writes)…");
        Console.WriteLine();

        var deadline = Stopwatch.StartNew();
        var counts = new Dictionary<string, int>();
        var distinct = new Dictionary<string, HashSet<string>>();

        while (deadline.Elapsed.TotalSeconds < seconds)
        {
            var any = false;

            foreach (var (device, channel) in channels)
            {
                var report = channel.Read(TimeSpan.FromMilliseconds(20));
                if (report is null)
                {
                    continue;
                }

                any = true;
                var key = $"0x{device.UsagePage:X4}/{device.InputReportLength}";
                counts[key] = counts.GetValueOrDefault(key) + 1;

                if (!distinct.TryGetValue(key, out var seen))
                {
                    seen = [];
                    distinct[key] = seen;
                }

                // 只打印每种接口的前 6 个不同报文，避免 1000Hz 刷屏。
                if (seen.Count < 6 && seen.Add(ToHex(report)))
                {
                    Console.WriteLine($"  {deadline.ElapsedMilliseconds,6}ms  {key,-12} {ToHex(report)}");
                }
            }

            if (!any)
            {
                Thread.Sleep(10);
            }
        }

        Console.WriteLine();
        Console.WriteLine("summary:");
        foreach (var (key, count) in counts.OrderBy(k => k.Key))
        {
            var variants = distinct.TryGetValue(key, out var set) ? set.Count : 0;
            Console.WriteLine($"  page/len {key,-12} reports={count,-6} distinct={variants}");
        }

        foreach (var (_, channel) in channels)
        {
            channel.Dispose();
        }
    }

    /// <summary>
    /// 查询模式：对每个可写的厂商接口，按候选 Report ID 发同一帧只读查询，等应答。
    /// </summary>
    public static void Query(IReadOnlyList<HidDeviceInfo> devices, bool sweep, int timeoutMs)
    {
        var targets = devices
            .Where(d => d.UsagePage >= 0xFF00 && d.OutputReportLength >= 2)
            .ToList();

        if (targets.Count == 0)
        {
            Console.WriteLine("no writable vendor interface found for this device.");
            return;
        }

        foreach (var device in targets)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {device.VidPid} page=0x{device.UsagePage:X4} out={device.OutputReportLength} in={device.InputReportLength}");

            using var connection = HidConnection.Open(device.Path);
            if (connection is null)
            {
                Console.WriteLine("  open failed");
                continue;
            }

            var declared = connection.OutputReportIds;
            Console.WriteLine($"  declared output ids: {(declared.Count == 0 ? "(none)" : string.Join(", ", declared.Select(i => $"0x{i:X2}")))}");
            Console.WriteLine($"  declared input ids:  {(connection.InputReportIds.Count == 0 ? "(none)" : string.Join(", ", connection.InputReportIds.Select(i => $"0x{i:X2}")))}");

            // 优先用描述符声明的 ID；一个都没声明时才退回候选表/盲扫。
            var reportIds = declared.Count > 0
                ? declared.ToArray()
                : sweep
                    ? Enumerable.Range(1, 0x20).Select(i => (byte)i).ToArray()
                    : LikelyReportIds;

            using var listener = HidInputChannel.Open(device.Path, device.InputReportLength);
            if (listener is null)
            {
                Console.WriteLine("  input channel unavailable — 无法配对读取应答");
                continue;
            }

            // 先静默 600ms，把设备主动上报的帧标出来，避免把它误判成命令应答。
            listener.Drain();
            var baseline = new HashSet<string>();
            var warmup = Stopwatch.StartNew();
            while (warmup.ElapsedMilliseconds < 600)
            {
                var idle = listener.Read(TimeSpan.FromMilliseconds(50));
                if (idle is not null)
                {
                    baseline.Add(ToHex(idle));
                }
            }

            if (baseline.Count > 0)
            {
                Console.WriteLine($"  unsolicited before any write ({baseline.Count}):");
                foreach (var frame in baseline.Take(4))
                {
                    Console.WriteLine($"    {frame}");
                }
            }

            foreach (var reportId in reportIds)
            {
                var payload = new byte[1 + NordicBatteryQuery.Length];
                payload[0] = reportId;
                NordicBatteryQuery.CopyTo(payload, 1);

                listener.Drain();

                if (!connection.TryWriteOutput(payload))
                {
                    Console.WriteLine($"  id 0x{reportId:X2}: write rejected");
                    continue;
                }

                var watch = Stopwatch.StartNew();
                var answer = listener.Read(TimeSpan.FromMilliseconds(timeoutMs));
                if (answer is null)
                {
                    Console.WriteLine($"  id 0x{reportId:X2}: no response ({timeoutMs}ms)");
                    continue;
                }

                var elapsed = watch.ElapsedMilliseconds;
                var text = ToHex(answer);
                var tag = baseline.Contains(text) ? "ECHO-OF-IDLE" : "RESPONSE";
                Console.WriteLine($"  id 0x{reportId:X2}: {text}  [{tag} {elapsed}ms]");

                var guess = InterpretNordicBattery(answer);
                if (guess is not null)
                {
                    Console.WriteLine($"        -> battery candidate: {guess}%");
                }
            }
        }
    }

    /// <summary>
    /// 按已公开的 Nordic 帧格式解析：resp[1]==0x72 且 resp[5]==0x07 → 电量在 resp[7]。
    /// 返回 null 表示不符合该格式，不要硬猜。
    /// </summary>
    private static int? InterpretNordicBattery(byte[] response)
    {
        if (response.Length < 8 || response[1] != 0x72 || response[5] != 0x07)
        {
            return null;
        }

        var level = response[7];
        return level is > 0 and <= 100 ? level : null;
    }

    private static string ToHex(byte[] data)
    {
        var builder = new StringBuilder(data.Length * 3);
        foreach (var b in data)
        {
            builder.Append(b.ToString("X2")).Append(' ');
        }

        return builder.ToString().TrimEnd();
    }
}
