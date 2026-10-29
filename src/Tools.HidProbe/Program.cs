using System.Text;
using System.Text.Json;
using GearTray.Core;
using GearTray.Drivers.Generic;
using GearTray.Drivers.McHose;
using GearTray.Runtime;
using GearTray.Transport.Hid;

namespace GearTray.Tools.HidProbe;

/// <summary>
/// M0 探测工具：把本机 HID 接口的真实拓扑打出来。
/// 它同时是「A7 V2 Ultra+ 到底有没有可写控制接口」这个前提的验证手段。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        var dumpFeature = args.Contains("--feature", StringComparer.OrdinalIgnoreCase);
        var listen = args.Contains("--listen", StringComparer.OrdinalIgnoreCase);
        var probe = args.Contains("--probe", StringComparer.OrdinalIgnoreCase);
        var sweep = args.Contains("--sweep", StringComparer.OrdinalIgnoreCase);
        var verifyWrite = args.Contains("--writable", StringComparer.OrdinalIgnoreCase);
        var filter = GetOption(args, "--vid");
        var productFilter = GetOption(args, "--pid");
        var includeEmpty = args.Contains("--all", StringComparer.OrdinalIgnoreCase);

        var seconds = int.TryParse(GetOption(args, "--secs"), out var secs) ? secs : 8;
        var timeoutMs = int.TryParse(GetOption(args, "--timeout"), out var timeout) ? timeout : 300;

        if (args.Contains("--poll", StringComparer.OrdinalIgnoreCase))
        {
            return PollOnce();
        }

        IReadOnlyList<HidDeviceInfo> devices;
        try
        {
            devices = HidEnumerator.Enumerate();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"enumeration failed: {ex.Message}");
            return 1;
        }

        if (filter is not null)
        {
            devices = devices
                .Where(d => d.VendorId.ToString("X4").Equals(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (productFilter is not null)
        {
            devices = devices
                .Where(d => d.ProductId.ToString("X4").Equals(productFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(devices, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            PrintTable(devices, includeEmpty);
        }

        if (dumpFeature)
        {
            DumpFeatures(devices);
        }

        var rawFeature = GetOption(args, "--raw-feature");
        if (rawFeature is not null)
        {
            // 最小路径：只开句柄 + 读一个 Feature Report，不碰描述符解析。
            // 用来把「打开」「解析描述符」「读报文」三步各自的稳定性分开验证。
            var reportId = Convert.ToByte(rawFeature, 16);
            Console.WriteLine($"raw feature read (id 0x{reportId:X2}):");

            foreach (var device in devices)
            {
                using var connection = HidConnection.Open(device.Path);
                if (connection is null)
                {
                    Console.WriteLine($"  {device.VidPid} 0x{device.UsagePage:X4}: open failed");
                    continue;
                }

                var data = connection.GetFeature(reportId);
                Console.WriteLine($"  {device.VidPid} 0x{device.UsagePage:X4} len={device.FeatureReportLength}: {(data is null ? "<none>" : ToHex(data))}");
            }
        }

        if (verifyWrite)
        {
            PrintWritable(devices);
        }

        if (args.Contains("--caps", StringComparer.OrdinalIgnoreCase))
        {
            PrintCaps(devices);
        }

        if (listen)
        {
            ProtocolProbe.Listen(devices, seconds);
        }

        if (probe)
        {
            ProtocolProbe.Query(devices, sweep, timeoutMs);
        }

        if (args.Contains("--mchose-battery", StringComparer.OrdinalIgnoreCase))
        {
            MchoseProbe.QueryBattery(devices);
        }

        if (args.Contains("--mchose-settings", StringComparer.OrdinalIgnoreCase))
        {
            MchoseProbe.DumpSettings(devices);
        }

        if (args.Contains("--mchose-dpi-write", StringComparer.OrdinalIgnoreCase))
        {
            MchoseProbe.VerifyDpiWrite(devices);
        }

        var stageOption = GetOption(args, "--mchose-dpi-stage");
        if (stageOption is not null && int.TryParse(stageOption, out var stage))
        {
            MchoseProbe.SetActiveStage(devices, stage);
        }

        if (args.Contains("--mchose-dpi-driver", StringComparer.OrdinalIgnoreCase))
        {
            MchoseProbe.DriverDpiTestAsync(devices).GetAwaiter().GetResult();
        }

        if (args.Contains("--vgn-feature", StringComparer.OrdinalIgnoreCase))
        {
            VgnProbe.ScanFeature(devices);
        }

        if (args.Contains("--vgn-listen", StringComparer.OrdinalIgnoreCase))
        {
            VgnProbe.Listen(devices, seconds);
        }

        if (args.Contains("--vgn-battery", StringComparer.OrdinalIgnoreCase))
        {
            VgnProbe.QueryBattery(devices);
        }

        if (args.Contains("--vgn-ws-battery", StringComparer.OrdinalIgnoreCase))
        {
            VgnWsProbe.QueryBattery(devices);
        }

        if (args.Contains("--vgn-ws-version", StringComparer.OrdinalIgnoreCase))
        {
            VgnWsProbe.ReadVersion(devices);
        }

        if (args.Contains("--vgn-ws-info", StringComparer.OrdinalIgnoreCase))
        {
            VgnWsProbe.ReadFunctionInformation(devices);
        }

        if (args.Contains("--vgn-ws-settings", StringComparer.OrdinalIgnoreCase))
        {
            VgnWsProbe.DumpSettings(devices);
        }

        var wsSet = GetOption(args, "--vgn-ws-set");
        if (wsSet is not null)
        {
            // 格式 key=value；value 支持 0x 前缀和 0xRRGGBB 颜色。
            var parts = wsSet.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                var value = parts[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToInt32(parts[1][2..], 16)
                    : int.Parse(parts[1]);

                VgnWsProbe.SetSetting(devices, parts[0], value);
            }
            else
            {
                Console.WriteLine("usage: --vgn-ws-set <key>=<value>");
            }
        }

        var wsLock = GetOption(args, "--vgn-ws-lock");
        if (wsLock is not null)
        {
            VgnWsProbe.SetWinKeyLock(devices, wsLock is "on" or "1" or "true");
        }

        var vgnCommand = GetOption(args, "--vgn-cmd");
        if (vgnCommand is not null)
        {
            var commands = vgnCommand
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToByte(part[2..], 16)
                    : byte.Parse(part))
                .ToArray();

            VgnProbe.ProbeCommand(devices, commands);
        }

        var vgnRaw = GetOption(args, "--vgn-raw");
        if (vgnRaw is not null)
        {
            var bytes = vgnRaw
                .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => Convert.ToByte(part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part, 16))
                .ToArray();

            VgnProbe.SendRaw(devices, bytes, timeoutMs);
        }

        var rawPage = GetOption(args, "--page");
        var rawFrame = GetOption(args, "--raw");
        if (rawPage is not null)
        {
            var page = rawPage.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt16(rawPage[2..], 16)
                : ushort.Parse(rawPage);

            if (rawFrame is null)
            {
                RawProbe.Listen(devices, page, seconds);
            }
            else
            {
                var bytes = rawFrame
                    .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(part => Convert.ToByte(part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part, 16))
                    .ToArray();

                RawProbe.Run(devices, page, bytes, timeoutMs, repeat: false);
            }
        }

        return 0;
    }

    /// <summary>
    /// 真写入验证。取代旧的「CreateFile 带 GENERIC_WRITE 成功即算可写」——
    /// 实测那条判据会把系统独占的鼠标/键盘集合也判成可写。
    /// </summary>
    private static void PrintWritable(IReadOnlyList<HidDeviceInfo> devices)
    {
        Console.WriteLine();
        Console.WriteLine("write verification (real WriteFile, vendor pages only):");
        Console.WriteLine(new string('-', 84));
        Console.WriteLine($"{"VID:PID",-11} {"PAGE",-8} {"OUT",-4} {"RESULT",-24} PRODUCT");
        Console.WriteLine(new string('-', 84));

        foreach (var device in devices.OrderBy(d => d.VidPid).ThenBy(d => d.UsagePage))
        {
            var result = HidConnection.VerifyWritable(device.Path, device);
            var product = device.DisplayName;
            if (product.Length > 30)
            {
                product = product[..30];
            }

            Console.WriteLine(
                $"{device.VidPid,-11} 0x{device.UsagePage:X4}  {device.OutputReportLength,-4} {result,-24} {product}");
        }

        Console.WriteLine(new string('-', 84));
        Console.WriteLine("Writable = WriteFile actually accepted a zero frame. InputRisk = keyboard/mouse collection, never written.");
        Console.WriteLine();
    }

    /// <summary>跑一遍完整管线（枚举 → 建会话 → 轮询），用于验证 M1 数据链路。</summary>
    private static int PollOnce()
    {
        var registry = new DeviceRegistry(new IDeviceDriver[]
        {
            new McHoseDriver(),
            new McHoseHeadsetDriver(),
            new GenericHidDriver(),
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            registry.RefreshAsync(cts.Token).GetAwaiter().GetResult();
            registry.PollAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"poll failed: {ex.Message}");
            return 1;
        }

        var snapshots = registry.Current;

        Console.WriteLine();
        Console.WriteLine($"devices: {snapshots.Count}");
        Console.WriteLine(new string('-', 104));
        Console.WriteLine($"{"DEVICE",-30} {"BATTERY",-8} {"HEALTH",-12} {"PAGE",-7} VID:PID");
        Console.WriteLine(new string('-', 104));

        foreach (var s in snapshots)
        {
            var name = s.Identity.DisplayName;
            if (name.Length > 29)
            {
                name = name[..29];
            }

            Console.WriteLine(
                $"{name,-30} {s.Battery.Display,-8} {s.Health,-12} 0x{s.Identity.UsagePage:X4}  {s.Identity.VidPid}");
        }

        Console.WriteLine(new string('-', 104));

        var recognized = snapshots.Count(s => s.Battery.Percent.HasValue);
        Console.WriteLine($"battery resolved: {recognized}/{snapshots.Count}");
        Console.WriteLine("(unresolved devices have no generic HID battery field yet — their private protocol");
        Console.WriteLine(" driver is still missing. MCHOSE A7 family is ported; VGN keyboard is not.)");

        registry.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    private static string? GetOption(string[] args, string name)    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void PrintTable(IReadOnlyList<HidDeviceInfo> devices, bool includeEmpty)
    {
        var rows = includeEmpty
            ? devices
            : devices.Where(d => d.VendorId != 0 || d.ProductId != 0).ToList();

        Console.WriteLine();
        Console.WriteLine($"HID interfaces: {rows.Count} (total {devices.Count})");
        Console.WriteLine(new string('-', 118));
        Console.WriteLine($"{"#",-3} {"VID:PID",-11} {"PAGE",-8} {"IN",-4} {"OUT",-4} {"FEAT",-5} {"W",-2} {"PRODUCT",-32} PATH");
        Console.WriteLine(new string('-', 118));

        var index = 0;
        foreach (var d in rows)
        {
            index++;
            var product = d.ProductName ?? "(no name)";
            if (product.Length > 31)
            {
                product = product[..31];
            }

            var path = d.Path.Length > 44 ? "…" + d.Path[^43..] : d.Path;

            Console.WriteLine(
                $"{index,-3} {d.VidPid,-11} 0x{d.UsagePage:X4}  {d.InputReportLength,-4} {d.OutputReportLength,-4} {d.FeatureReportLength,-5} " +
                $"{(d.CanWrite ? "Y" : "-"),-2} {product,-32} {path}");
        }

        Console.WriteLine(new string('-', 118));
        Console.WriteLine("W = writable (open with GENERIC_WRITE succeeded). Only these can accept DPI / lock commands.");
        Console.WriteLine();

        var writableVendors = rows.Where(d => d.CanWrite).GroupBy(d => d.VidPid);
        foreach (var group in writableVendors)
        {
            Console.WriteLine($"writable: {group.Key} × {group.Count()}");
        }

        if (writableVendors.Any())
        {
            Console.WriteLine();
        }
    }

    private static void PrintCaps(IReadOnlyList<HidDeviceInfo> devices)
    {
        Console.WriteLine();
        Console.WriteLine($"report descriptor caps (HIDP_VALUE_CAPS size = {HidCaps.ValueCapsStructSize} bytes, expected 60):");
        Console.WriteLine(new string('-', 100));

        foreach (var device in devices.Where(d => d.UsagePage >= 0xFF00))
        {
            Console.WriteLine($"{device.VidPid} page=0x{device.UsagePage:X4}");

            using var connection = HidConnection.Open(device.Path);
            if (connection is null)
            {
                Console.WriteLine("  open failed");
                continue;
            }

            var caps = connection.Caps;
            if (caps.Count == 0)
            {
                Console.WriteLine("  (no value caps)");
            }

            foreach (var cap in caps)
            {
                Console.WriteLine($"  {cap}");
            }
        }

        Console.WriteLine();
    }

    private static void DumpFeatures(IReadOnlyList<HidDeviceInfo> devices)
    {
        Console.WriteLine("Feature report dump:");
        Console.WriteLine(new string('-', 118));

        foreach (var device in devices.Where(d => d.FeatureReportLength >= 2))
        {
            using var connection = HidConnection.Open(device.Path);
            if (connection is null)
            {
                Console.WriteLine($"{device.VidPid} {device.DisplayName}: open failed");
                continue;
            }

            // 用描述符声明的 ID，不是 0..3。实测 A7 的 Feature 报表全挂在 0x2A 上。
            var ids = connection.FeatureReportIds.Count > 0
                ? connection.FeatureReportIds.Where(i => i != 0 || connection.FeatureReportIds.Count == 1).ToArray()
                : [0, 1, 2, 3];

            Console.WriteLine($"{device.VidPid} {device.DisplayName} (feat={device.FeatureReportLength}, ids={string.Join(",", ids.Select(i => $"0x{i:X2}"))})");

            foreach (var reportId in ids)
            {
                var data = connection.GetFeature(reportId);
                if (data is null || data.Length == 0)
                {
                    Console.WriteLine($"  id 0x{reportId:X2}: <none>");
                    continue;
                }

                Console.WriteLine($"  id 0x{reportId:X2}: {ToHex(data)}");
            }
        }

        Console.WriteLine();
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
