using System.Text;
using GearTray.Drivers.Vgn;
using GearTray.Transport.Hid;

namespace GearTray.Tools.HidProbe;

/// <summary>
/// VGN「weisheng / 微盛」方案键盘（N75 PRO 2.4G 等）的现代协议实现。
///
/// 协议源：VGN VHUB 桌面端 `WsServe`（app.asar → dist/renderer/assets/home-*.js）。
/// 与网页驱动里的 `[0x55][cmd]` 是**两套不同的协议**，那套在本机的 N75 PRO 上只会回 0xFF。
///
/// 帧结构（Output Report ID = 0x04，缓冲 64 字节，尾部补零）：
///   请求 [00][00][cmd][len][offset][00][00]                  —— 7 字节头
///   应答 [00][00][cmd][len][offset][00][00][data…]           —— 头部原样回显，数据从下标 8 起
///
/// 所以「去掉 Report ID 后的 payload」里：payload[2]=cmd、payload[3]=len、payload[4]=offset、payload[7..]=data。
/// </summary>
internal sealed class VgnWsProbe
{
    private const byte ReportId = 0x04;

    private const int HeaderLength = 7;

    private const byte CmdGetBattery = 26;

    private const byte CmdGetVersion = 3;

    private const byte CmdGetFunctionInformation = 5;

    private const byte CmdSetFunctionInformation = 6;

    /// <summary>功能信息块在功能信息缓冲里的下标（VHUB：0 关 1 开）。</summary>
    private const int FunctionInfoLockWinIndex = 21;

    public static void QueryBattery(IReadOnlyList<HidDeviceInfo> devices)
    {
        using var client = Client.Open(devices);
        if (client is null)
        {
            return;
        }

        for (var run = 1; run <= 3; run++)
        {
            var data = client.Read(CmdGetBattery, length: 6, offset: 0, TimeSpan.FromMilliseconds(1200));
            if (data is null)
            {
                Console.WriteLine($"  run {run}: no reply");
                continue;
            }

            var level = data[0];
            var charging = data[1];
            var voltage = data.Length > 3 ? (data[2] | (data[3] << 8)) : 0;
            Console.WriteLine($"  run {run}: {level}%  charging={charging}  voltage={voltage}mV  raw={ToHex(data)}");
        }
    }

    public static void ReadVersion(IReadOnlyList<HidDeviceInfo> devices)
    {
        using var client = Client.Open(devices);
        if (client is null)
        {
            return;
        }

        var first = client.Read(CmdGetVersion, length: 24, offset: 0, TimeSpan.FromMilliseconds(1200));
        if (first is null)
        {
            Console.WriteLine("  no reply");
            return;
        }

        Console.WriteLine($"  chunk0: {ToHex(first)}");
        Console.WriteLine($"  ascii : {ToAscii(first)}");

        // VHUB: appVersion = data[5].ToString(16) + data[4].ToString(16).PadLeft(2,'0')
        if (first.Length > 5)
        {
            Console.WriteLine($"  version: {first[5]:X2}{first[4]:X2}");
        }
    }

    /// <summary>读功能信息两块（各 24 字节），解码 Win 锁等字段。</summary>
    public static void ReadFunctionInformation(IReadOnlyList<HidDeviceInfo> devices)
    {
        using var client = Client.Open(devices);
        if (client is null)
        {
            return;
        }

        var buffer = ReadFunctionBuffer(client);
        if (buffer is null)
        {
            return;
        }

        Console.WriteLine($"  buffer: {ToHex(buffer)}");
        Console.WriteLine();
        Console.WriteLine("  --- decoded (VHUB indices) ---");
        Console.WriteLine($"  [1]  light mode          = {buffer[1]}");
        Console.WriteLine($"  [2]  luminance           = {buffer[2]}");
        Console.WriteLine($"  [3]  speed raw           = {buffer[3]}");
        Console.WriteLine($"  [5]  chroma              = {buffer[5]}");
        Console.WriteLine($"  [6..8] static color      = {buffer[6]:X2}{buffer[7]:X2}{buffer[8]:X2}");
        Console.WriteLine($"  [19] direction key       = {buffer[19]}");
        Console.WriteLine($"  [20] six-key (0=on)      = {buffer[20]}");
        Console.WriteLine($"  [21] WIN LOCK            = {buffer[21]}  (0=off 1=on)");
        Console.WriteLine($"  [22] polling rate        = {buffer[22]}");
        Console.WriteLine($"  [27] os mode (mac)       = {buffer[27]}");
        Console.WriteLine($"  [28] light open (0=on)   = {buffer[28]}");
    }

    /// <summary>
    /// 切 Win 锁：读出整块 → 只改 [21] → 两块写回 → 再读一次确认。
    /// 写完立即回读，失败就原样写回旧值，避免把键盘留在半改状态。
    /// </summary>
    public static void SetWinKeyLock(IReadOnlyList<HidDeviceInfo> devices, bool locked)
    {
        using var client = Client.Open(devices);
        if (client is null)
        {
            return;
        }

        var original = ReadFunctionBuffer(client);
        if (original is null)
        {
            return;
        }

        Console.WriteLine($"  before: lockWin={original[FunctionInfoLockWinIndex]}");
        if (original[FunctionInfoLockWinIndex] == (locked ? 1 : 0))
        {
            Console.WriteLine("  already in requested state — nothing to write");
            return;
        }

        var target = (byte[])original.Clone();
        target[FunctionInfoLockWinIndex] = locked ? (byte)1 : (byte)0;

        if (!WriteFunctionBuffer(client, target))
        {
            Console.WriteLine("  write rejected");
            return;
        }

        Thread.Sleep(120);

        var after = ReadFunctionBuffer(client);
        if (after is null)
        {
            Console.WriteLine("  read-back failed");
            return;
        }

        Console.WriteLine($"  after : lockWin={after[FunctionInfoLockWinIndex]}");
        if (after[FunctionInfoLockWinIndex] != target[FunctionInfoLockWinIndex])
        {
            Console.WriteLine("  !! write did not take effect, restoring previous value");
            WriteFunctionBuffer(client, original);
        }
    }

    /// <summary>把功能信息块按字段表全部解出来。字段表与驱动共用，不在这里另抄一份下标。</summary>
    public static void DumpSettings(IReadOnlyList<HidDeviceInfo> devices)
    {
        using var client = Client.Open(devices);
        if (client is null)
        {
            return;
        }

        var buffer = ReadFunctionBuffer(client);
        if (buffer is null)
        {
            return;
        }

        Console.WriteLine($"  buffer: {ToHex(buffer)}");
        Console.WriteLine();

        foreach (var group in VgnWsSettings.Specs.GroupBy(s => s.Group))
        {
            Console.WriteLine($"  ── {group.Key} ──");
            foreach (var spec in group)
            {
                var setting = spec.ToSetting(buffer);
                var hint = spec.Hint is null ? string.Empty : $"   // {spec.Hint}";
                Console.WriteLine($"  {spec.Key,-22} [{spec.Index,2}] = {setting.Value,-5} {setting.DisplayValue}{hint}");
            }
        }

        var winLock = VgnWsSettings.WinKeyLock;
        Console.WriteLine("  ── 系统 ──");
        Console.WriteLine($"  {winLock.Key,-22} [{winLock.Index,2}] = {winLock.Read(buffer),-5} " +
                          $"{(winLock.Read(buffer) != 0 ? "锁定" : "未锁")}");
    }

    /// <summary>
    /// 写一项：读整块 → 只改目标字段 → 两块写回 → 回读校验 → 不一致就写回旧值。
    /// 顺便打印被改动的字节，用来抓「改 A 连带改了 B」这种固件副作用。
    /// </summary>
    public static void SetSetting(IReadOnlyList<HidDeviceInfo> devices, string key, int value)
    {
        var spec = VgnWsSettings.Find(key);
        if (spec is null)
        {
            Console.WriteLine($"unknown key '{key}'. available:");
            foreach (var item in VgnWsSettings.Specs.Select(s => s.Key).Append(VgnWsSettings.WinKeyLock.Key))
            {
                Console.WriteLine($"  {item}");
            }

            return;
        }

        using var client = Client.Open(devices);
        if (client is null)
        {
            return;
        }

        var original = ReadFunctionBuffer(client);
        if (original is null)
        {
            return;
        }

        Console.WriteLine($"  before: {spec.Key} = {spec.Read(original)}");

        var target = (byte[])original.Clone();
        spec.Write(target, value);

        if (target.AsSpan().SequenceEqual(original))
        {
            Console.WriteLine("  already in requested state — nothing to write");
            return;
        }

        if (!WriteFunctionBuffer(client, target))
        {
            Console.WriteLine("  write rejected");
            return;
        }

        Thread.Sleep(150);

        var after = ReadFunctionBuffer(client);
        if (after is null)
        {
            Console.WriteLine("  read-back failed, restoring");
            WriteFunctionBuffer(client, original);
            return;
        }

        var actual = spec.Read(after);
        Console.WriteLine($"  after : {spec.Key} = {actual}");

        // 连带改动：改 A 时固件可能顺手改 B（换灯效模式会动速度/方向），必须看得见。
        var collateral = Enumerable.Range(0, 48)
            .Where(i => original[i] != after[i] && (i < spec.Index || i >= spec.Index + ByteWidth(spec.Encoding)))
            .ToList();

        if (collateral.Count > 0)
        {
            Console.WriteLine("  collateral changes:");
            foreach (var i in collateral)
            {
                Console.WriteLine($"    [{i,2}] {original[i]:X2} -> {after[i]:X2}");
            }
        }

        if (!spec.MatchesReadBack(actual, value))
        {
            Console.WriteLine("  !! firmware did not accept the value, restoring previous block");
            WriteFunctionBuffer(client, original);
        }
    }

    private static int ByteWidth(FieldEncoding encoding) => encoding switch
    {
        FieldEncoding.UInt16LE => 2,
        FieldEncoding.Rgb => 3,
        _ => 1,
    };

    private static byte[]? ReadFunctionBuffer(Client client)
    {
        var first = client.Read(CmdGetFunctionInformation, length: 24, offset: 0, TimeSpan.FromMilliseconds(1200));
        if (first is null)
        {
            Console.WriteLine("  function info chunk0: no reply");
            return null;
        }

        Thread.Sleep(60);

        var second = client.Read(CmdGetFunctionInformation, length: 24, offset: 24, TimeSpan.FromMilliseconds(1200));
        if (second is null)
        {
            Console.WriteLine("  function info chunk24: no reply");
            return null;
        }

        var buffer = new byte[48];
        first.AsSpan(0, Math.Min(24, first.Length)).CopyTo(buffer.AsSpan(0));
        second.AsSpan(0, Math.Min(24, second.Length)).CopyTo(buffer.AsSpan(24));
        return buffer;
    }

    private static bool WriteFunctionBuffer(Client client, byte[] buffer)
    {
        var ok = client.Write(CmdSetFunctionInformation, length: 24, offset: 0, buffer.AsSpan(0, 24));
        Thread.Sleep(60);
        ok &= client.Write(CmdSetFunctionInformation, length: 24, offset: 24, buffer.AsSpan(24, 24));
        return ok;
    }

    /// <summary>一条连接的薄封装：负责帧构造、发送、按 cmd+offset 收应答。</summary>
    private sealed class Client : IDisposable
    {
        private readonly HidConnection _connection;

        private readonly HidInputChannel _channel;

        private Client(HidConnection connection, HidInputChannel channel)
        {
            _connection = connection;
            _channel = channel;
        }

        public static Client? Open(IReadOnlyList<HidDeviceInfo> devices)
        {
            var target = devices.FirstOrDefault(d => d.UsagePage == 0xFF1C && d.InputReportLength > 0);
            if (target is null)
            {
                Console.WriteLine("no vendor interface (0xFF1C) with an input report.");
                return null;
            }

            var connection = HidConnection.Open(target.Path);
            if (connection is null)
            {
                Console.WriteLine("open failed");
                return null;
            }

            var channel = HidInputChannel.Open(target.Path, target.InputReportLength);
            if (channel is null)
            {
                connection.Dispose();
                Console.WriteLine("input channel unavailable");
                return null;
            }

            Console.WriteLine($"=== page=0x{target.UsagePage:X4} in={target.InputReportLength} out={target.OutputReportLength} reportId=0x{ReportId:X2}");
            return new Client(connection, channel);
        }

        public bool Write(byte command, byte length, byte offset, ReadOnlySpan<byte> data)
        {
            _channel.Drain();

            var frame = new byte[_connection.Info.OutputReportLength];
            frame[0] = ReportId;
            frame[1] = 0;
            frame[2] = 0;
            frame[3] = command;
            frame[4] = length;
            frame[5] = offset;
            frame[6] = 0;
            frame[7] = 0;
            data[..Math.Min(data.Length, frame.Length - HeaderLength - 1)].CopyTo(frame.AsSpan(HeaderLength + 1));

            if (_connection.TryWriteOutput(frame, out var error))
            {
                return true;
            }

            Console.WriteLine($"  write rejected (win32 {error})");
            return false;
        }

        /// <summary>发命令并等应答；返回数据段（原始 buffer 的下标 8 起）。</summary>
        public byte[]? Read(byte command, byte length, byte offset, TimeSpan timeout, bool verbose = true)
        {
            if (!Write(command, length, offset, default))
            {
                return null;
            }

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var report = _channel.Read(TimeSpan.FromMilliseconds(150));
                if (report is null || report.Length < HeaderLength + 1)
                {
                    continue;
                }

                // 只认「真应答」：cmd 与 offset 都要对上，并且不是原样回显（回显没有数据段）。
                if (report[3] != command || report[5] != offset)
                {
                    continue;
                }

                var available = report.Length - HeaderLength - 1;
                var data = report.AsSpan(HeaderLength + 1, Math.Max(0, Math.Min(length, available))).ToArray();
                if (verbose)
                {
                    Console.WriteLine($"  rx  {ToHex(report.AsSpan(0, Math.Min(24, report.Length)))}");
                }

                return data;
            }

            return null;
        }

        public void Dispose()
        {
            _channel.Dispose();
            _connection.Dispose();
        }
    }

    private static string ToAscii(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder(data.Length);
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
