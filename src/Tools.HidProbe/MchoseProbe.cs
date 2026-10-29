using System.Text;
using GearTray.Core;
using GearTray.Drivers.McHose;
using GearTray.Transport.Hid;

namespace GearTray.Tools.HidProbe;

/// <summary>
/// MCHOSE 驱动的人工验证入口：打印草稿报文与解码结果。
/// 协议逻辑本身在 <see cref="McHoseProtocol"/> 里，这里只负责显示 —— 避免探测工具和驱动
/// 各写一份解析、结果不一致。
/// </summary>
internal static class MchoseProbe
{
    public static void QueryBattery(IReadOnlyList<HidDeviceInfo> devices)
    {
        var targets = devices
            .Where(d => d.UsagePage == 0xFF01 && d.FeatureReportLength >= McHoseProtocol.ControlBufferLength)
            .ToList();

        if (targets.Count == 0)
        {
            Console.WriteLine("no MCHOSE control interface (usage page 0xFF01) found for this device.");
            return;
        }

        foreach (var device in targets)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {device.VidPid} page=0x{device.UsagePage:X4} feat={device.FeatureReportLength}");

            using var connection = HidConnection.Open(device.Path);
            if (connection is null)
            {
                Console.WriteLine("  open failed");
                continue;
            }

            for (var run = 1; run <= 3; run++)
            {
                var ok = McHoseProtocol.TryRead(connection, attempts: 5, out var reading, out var error);

                if (!ok)
                {
                    Console.WriteLine($"  run {run}: failed ({error})");
                    continue;
                }

                var state = reading.Charging ? "charging" : "discharging";
                Console.WriteLine($"  run {run}: {reading.Percent}% {state}");
                Console.WriteLine($"    decoded (payload ^ 0xFF): {ToHex(reading.Decoded)}");
            }
        }
    }

    /// <summary>
    /// 读 0x12/0x67 全量板载配置。同时打印解码后的原始字节 —— 反推未知字段
    /// （Win 键锁、灯效之类）只能靠对着字节 diff，不能靠猜下标。
    /// </summary>
    public static void DumpSettings(IReadOnlyList<HidDeviceInfo> devices)
    {
        var targets = devices
            .Where(d => d.UsagePage == 0xFF01 && d.FeatureReportLength >= McHoseProtocol.ConfigBufferLength)
            .ToList();

        if (targets.Count == 0)
        {
            Console.WriteLine("no MCHOSE control interface with a 65-byte feature report found.");
            return;
        }

        foreach (var device in targets)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {device.VidPid} page=0x{device.UsagePage:X4} feat={device.FeatureReportLength}");

            using var connection = HidConnection.Open(device.Path);
            if (connection is null)
            {
                Console.WriteLine("  open failed");
                continue;
            }

            var ok = McHoseProtocol.TryReadSettings(connection, attempts: 5, out var settings, out var error);
            if (!ok || settings is null)
            {
                Console.WriteLine($"  read settings failed ({error})");
                continue;
            }

            Console.WriteLine($"  profile={settings.ProfileIndex} stage={settings.ActiveStageIndex + 1}/{settings.StageCount} " +
                              $"dpi={settings.ActiveDpi} rate={settings.PollingRateIndex} debounce={settings.DebounceMs}ms " +
                              $"sleep={(settings.NeverSleep ? "never" : $"{settings.SleepMinutes}m")} " +
                              $"flags=0x{settings.SensorFlags:X2} rot={settings.RotationDegrees}");
            Console.WriteLine($"  stages: {string.Join(", ", settings.DpiStages)}");
            Console.WriteLine("  decoded payload (64B, index 0 = command echo):");
            HexDump(settings.Raw);

            // 同一台设备连读两次做对照：稳定字段应完全一致，可用来识别「随状态变化」的字节。
            var second = McHoseProtocol.TryReadSettings(connection, attempts: 5, out var again, out _);
            if (second && again is not null)
            {
                Console.WriteLine($"  re-read identical: {again.Raw.AsSpan().SequenceEqual(settings.Raw)}");
            }
        }
    }

    /// <summary>
    /// 写通道验证：把**当前实测值**原样写成 0x40 帧，再读回逐字节 diff。
    /// 值一个都不改，所以最坏结果是「没有效果」，不会把档位写成离谱值。
    /// 这一步要回答的是：写入布局到底对不对、写它会不会连带改坏 sensor/debounce。
    /// </summary>
    public static void VerifyDpiWrite(IReadOnlyList<HidDeviceInfo> devices)
    {
        var targets = devices
            .Where(d => d.UsagePage == 0xFF01 && d.FeatureReportLength >= McHoseProtocol.ConfigBufferLength)
            .ToList();

        if (targets.Count == 0)
        {
            Console.WriteLine("no MCHOSE control interface with a 65-byte feature report found.");
            return;
        }

        foreach (var device in targets)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {device.VidPid} page=0x{device.UsagePage:X4}");

            using var connection = HidConnection.Open(device.Path);
            if (connection is null)
            {
                Console.WriteLine("  open failed");
                continue;
            }

            if (!McHoseProtocol.TryReadSettings(connection, 5, out var before, out var error) || before is null)
            {
                Console.WriteLine($"  baseline read failed ({error})");
                continue;
            }

            Console.WriteLine($"  before: stage={before.ActiveStageIndex + 1}/{before.StageCount} dpi={before.ActiveDpi} " +
                              $"debounce={before.DebounceMs} sleep={before.SleepMinutes} flags=0x{before.SensorFlags:X2}");

            var payload = McHoseProtocol.BuildDpiPayload(before.DpiStages, before.ActiveStageIndex, before.StageCount);
            Console.WriteLine($"  tx payload (semantic, before inversion): {ToHex(payload)}");

            if (!McHoseProtocol.TryWriteDpi(connection, payload, out var writeError))
            {
                Console.WriteLine($"  write rejected (win32 {writeError})");
                continue;
            }

            Console.WriteLine("  write accepted");

            // 固件写 flash 需要时间，立刻读会拿到旧值。
            Thread.Sleep(300);

            if (!McHoseProtocol.TryReadSettings(connection, 5, out var after, out error) || after is null)
            {
                Console.WriteLine($"  re-read failed ({error})");
                continue;
            }

            Console.WriteLine($"  after : stage={after.ActiveStageIndex + 1}/{after.StageCount} dpi={after.ActiveDpi} " +
                              $"debounce={after.DebounceMs} sleep={after.SleepMinutes} flags=0x{after.SensorFlags:X2}");

            var diffs = new List<string>();
            for (var i = 0; i < Math.Min(before.Raw.Length, after.Raw.Length); i++)
            {
                if (before.Raw[i] != after.Raw[i])
                {
                    diffs.Add($"[{i:X2}] {before.Raw[i]:X2}->{after.Raw[i]:X2}");
                }
            }

            Console.WriteLine(diffs.Count == 0
                ? "  byte-level diff: none (写入布局与读回自洽)"
                : $"  byte-level diff: {string.Join(", ", diffs)}");

            Console.WriteLine($"  stages match: {before.DpiStages.SequenceEqual(after.DpiStages)}");
        }
    }

    /// <summary>
    /// 切换当前生效的 DPI 档位。只改 [1][2] 两个字节，档位数值一个不动 ——
    /// 这是能证明「写入真的生效」的最小改动，且可以原样切回去。
    /// </summary>
    public static void SetActiveStage(IReadOnlyList<HidDeviceInfo> devices, int target)
    {
        var targets = devices
            .Where(d => d.UsagePage == 0xFF01 && d.FeatureReportLength >= McHoseProtocol.ConfigBufferLength)
            .ToList();

        if (targets.Count == 0)
        {
            Console.WriteLine("no MCHOSE control interface with a 65-byte feature report found.");
            return;
        }

        foreach (var device in targets)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {device.VidPid} page=0x{device.UsagePage:X4}");

            using var connection = HidConnection.Open(device.Path);
            if (connection is null)
            {
                Console.WriteLine("  open failed");
                continue;
            }

            if (!McHoseProtocol.TryReadSettings(connection, 5, out var before, out var error) || before is null)
            {
                Console.WriteLine($"  baseline read failed ({error})");
                continue;
            }

            if (target < 0 || target >= before.StageCount)
            {
                Console.WriteLine($"  stage {target} out of range (device reports {before.StageCount} stages)");
                continue;
            }

            if (target == before.ActiveStageIndex)
            {
                Console.WriteLine($"  already on stage {target + 1} ({before.ActiveDpi} dpi) — nothing to do");
                continue;
            }

            Console.WriteLine($"  before: stage={before.ActiveStageIndex + 1} dpi={before.ActiveDpi}");

            var payload = McHoseProtocol.BuildDpiPayload(before.DpiStages, target, before.StageCount);
            if (!McHoseProtocol.TryWriteDpi(connection, payload, out var writeError))
            {
                Console.WriteLine($"  write rejected (win32 {writeError})");
                continue;
            }

            Thread.Sleep(300);

            if (!McHoseProtocol.TryReadSettings(connection, 5, out var after, out error) || after is null)
            {
                Console.WriteLine($"  re-read failed ({error})");
                continue;
            }

            Console.WriteLine($"  after : stage={after.ActiveStageIndex + 1} dpi={after.ActiveDpi}");
            Console.WriteLine(after.ActiveStageIndex == target && after.ActiveDpi == before.DpiStages[target]
                ? "  VERIFIED: active stage changed and persisted"
                : "  MISMATCH: write did not take effect");
        }
    }

    /// <summary>
    /// 走正式驱动链路验证 DPI 能力：McHoseDriver → MatchScore → OpenAsync → IDpiControl。
    /// 直接调协议层只能证明报文对，走驱动才能证明「宿主拿到快照」这条路也是通的。
    /// </summary>
    public static async Task DriverDpiTestAsync(IReadOnlyList<HidDeviceInfo> devices)
    {
        var device = devices.FirstOrDefault(d =>
            d.UsagePage == 0xFF01 && d.FeatureReportLength >= McHoseProtocol.ConfigBufferLength);

        if (device is null)
        {
            Console.WriteLine("no MCHOSE control interface found.");
            return;
        }

        var identity = new GearTray.Core.DeviceIdentity(
            device.VendorId,
            device.ProductId,
            device.UsagePage,
            device.Usage,
            device.DisplayName,
            device.SerialNumber,
            device.Path);

        var driver = new McHoseDriver();
        Console.WriteLine();
        Console.WriteLine($"=== driver={driver.Manifest.Id} matchScore={driver.MatchScore(identity)}");

        await using var session = await driver.OpenAsync(identity, CancellationToken.None);
        if (session is not IDpiControl dpi)
        {
            Console.WriteLine("  session does not expose IDpiControl — UI 上不会出现 DPI 控件");
            return;
        }

        var profile = await dpi.ReadDpiAsync(CancellationToken.None);
        if (profile is null)
        {
            Console.WriteLine("  ReadDpiAsync returned null");
            return;
        }

        Console.WriteLine($"  profile: stages=[{string.Join(", ", profile.Stages)}] active={profile.ActiveIndex + 1} dpi={profile.ActiveDpi}");
        Console.WriteLine($"  capabilities: {string.Join(", ", session.Capabilities)}");

        // 切到相邻档再切回来：两次真实写入，能证明「改档位」这条路通了，且不改变最终状态。
        var original = profile.ActiveIndex;
        var probe = original == 0 ? 1 : 0;

        var forward = await dpi.SetActiveStageAsync(probe, CancellationToken.None);
        var mid = await dpi.ReadDpiAsync(CancellationToken.None);
        var restore = await dpi.SetActiveStageAsync(original, CancellationToken.None);
        var final = await dpi.ReadDpiAsync(CancellationToken.None);

        Console.WriteLine($"  switch {original + 1} -> {probe + 1}: ok={forward} readback={mid?.ActiveIndex + 1}");
        Console.WriteLine($"  restore {probe + 1} -> {original + 1}: ok={restore} readback={final?.ActiveIndex + 1}");
        Console.WriteLine(forward && restore && final?.ActiveIndex == original
            ? "  VERIFIED: IDpiControl round-trip"
            : "  FAILED: IDpiControl round-trip");
    }

    private static void HexDump(byte[] data)
    {
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var count = Math.Min(16, data.Length - offset);
            var builder = new StringBuilder();
            builder.Append($"    {offset:X4}  ");

            for (var i = 0; i < count; i++)
            {
                builder.Append(data[offset + i].ToString("X2")).Append(' ');
            }

            builder.Append(new string(' ', (16 - count) * 3));
            builder.Append(" |");
            for (var i = 0; i < count; i++)
            {
                var b = data[offset + i];
                builder.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            builder.Append('|');
            Console.WriteLine(builder.ToString());
        }
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
