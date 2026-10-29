using GearTray.Transport.Hid;

namespace GearTray.Drivers.Vgn;

/// <summary>
/// VGN「weisheng / 微盛」方案键盘协议（N75 PRO 2.4G 等）。
///
/// 来源：VGN VHUB 桌面端 <c>WsServe</c>（app.asar → dist/renderer/assets/home-*.js）。
/// 注意这是**第三套**协议，别和另外两套混用：
///   1. SignalRGB 灯效      [04][校验和][0B/12][参数]         —— 第三方灯效同步
///   2. 网页驱动配置        [04][0x55][cmd][参数]             —— 旧版，本机设备只回 0xFF
///   3. **VHUB 桌面端**     [04][00][00][cmd][len][offset][00][00][data…]  ← 本文实现
///
/// 帧格式（Output Report ID = 0x04，缓冲 64 字节）：
///   请求 [04][00][00][cmd][len][offset][00][00][data…]
///   应答 头部原样回显，数据从下标 8 起
///
/// 「去掉 Report ID 后的 payload」里 payload[2]=cmd、payload[3]=len、payload[4]=offset、payload[7..]=data；
/// 落到含 Report ID 的原始 buffer 上就是 [3]/[4]/[5]/[8..] —— 这个 +1 差一位是本项目已经栽过两次的坑。
/// </summary>
public static class VgnWsProtocol
{
    public const byte ReportId = 0x04;

    /// <summary>含 Report ID 的头部长度：ReportId + 7 字节协议头。</summary>
    public const int HeaderLength = 8;

    public const byte CmdGetBattery = 26;

    public const byte CmdGetVersion = 3;

    public const byte CmdGetFunctionInformation = 5;

    public const byte CmdSetFunctionInformation = 6;

    /// <summary>功能信息块共 48 字节，分两块各 24 字节读。</summary>
    public const int FunctionInformationSize = 48;

    // ── 功能信息块字段下标（来源：VHUB home-*.js 里 WsServe 的 set* 方法，逐个核对过）──
    // 注意这些是「48 字节块」的下标，不是报文下标；报文里要 +8。

    /// <summary>灯效模式。1..18 为内置动态，19=自定义，20=关闭，254=音乐律动。</summary>
    public const int LightModeIndex = 1;

    /// <summary>亮度。0 最亮，4 最暗。</summary>
    public const int LightLuminanceIndex = 2;

    /// <summary>灯效速度。0 最快，4 最慢（VHUB 的 UI 档位是反的，这里存固件值）。</summary>
    public const int LightSpeedIndex = 3;

    /// <summary>灯效方向。0 左→右，1 右→左。</summary>
    public const int LightDirectionIndex = 4;

    /// <summary>混色（chroma）。0 关 1 开。</summary>
    public const int LightChromaIndex = 5;

    /// <summary>静态色 RGB，占 6/7/8 三字节。</summary>
    public const int LightColorIndex = 6;

    /// <summary>灯效开关。0 开 1 关（和直觉相反，别写成 1=开）。</summary>
    public const int LightSwitchIndex = 28;

    /// <summary>休眠时间，两字节小端，取值见 <see cref="DormancyOptions"/>。</summary>
    public const int DormancyIndex = 13;

    /// <summary>方向键交换（WASD ↔ 方向键）。0 关 1 开。</summary>
    public const int DirectionKeyIndex = 19;

    /// <summary>六键无冲。0 开 1 关（同样反直觉）。</summary>
    public const int SixKeyIndex = 20;

    /// <summary>Win 键锁在功能信息块里的下标（VHUB：0 关 1 开）。</summary>
    public const int WinKeyLockIndex = 21;

    /// <summary>轮询率。0=1000Hz, 1=500Hz, 2=250Hz, 3=125Hz。</summary>
    public const int ReportingRateIndex = 22;

    /// <summary>Mac/OS 模式。0 关 1 开（VXE V87 例外，它占用灯效开关那一位）。</summary>
    public const int OsModeIndex = 27;

    /// <summary>按键超低延迟。0 关 1 开。</summary>
    public const int UltraLowLatencyIndex = 33;

    /// <summary>按键低延迟。0x00 关 / 0xFF 开。</summary>
    public const int LowLatencyIndex = 34;

    /// <summary>
    /// 休眠档位，对应 <see cref="DormancyIndex"/> 的两字节（小端）。
    /// 0xFFFF = 从不休眠，其余为秒。
    /// </summary>
    public static readonly (int Seconds, string Label)[] DormancyOptions =
    [
        (-1, "从不"),
        (60, "1 分钟"),
        (180, "3 分钟"),
        (300, "5 分钟"),
        (600, "10 分钟"),
        (1800, "30 分钟"),
    ];

    /// <summary>灯效模式中文名（VHUB 简体中文语言包 weiShengLightData，1 起）。</summary>
    public static readonly (int Value, string Label)[] LightModeOptions =
    [
        (1, "随波逐流"),
        (2, "彩云纷飞"),
        (3, "峰回路转"),
        (4, "光之审判"),
        (5, "呼吸"),
        (6, "常亮"),
        (7, "踏雪无痕"),
        (8, "泛起涟漪"),
        (9, "奔逸绝尘"),
        (10, "暗流涌动"),
        (11, "百花争艳"),
        (12, "流星赶月"),
        (13, "大鹏展翅"),
        (14, "川流不息"),
        (15, "落雨纷纷"),
        (16, "左右逢源"),
        (17, "众志成城"),
        (18, "速度激情"),
        (19, "自定义"),
        (20, "关闭"),
        (254, "音乐律动"),
    ];

    public static readonly (int Value, string Label)[] ReportingRateOptions =
    [
        (0, "1000 Hz"),
        (1, "500 Hz"),
        (2, "250 Hz"),
        (3, "125 Hz"),
    ];

    public static byte[] BuildFrame(int outputReportLength, byte command, byte length, byte offset, ReadOnlySpan<byte> data)
    {
        var frame = new byte[Math.Max(outputReportLength, HeaderLength)];
        frame[0] = ReportId;
        frame[1] = 0;
        frame[2] = 0;
        frame[3] = command;
        frame[4] = length;
        frame[5] = offset;
        frame[6] = 0;
        frame[7] = 0;

        var room = frame.Length - HeaderLength;
        data[..Math.Min(data.Length, room)].CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    /// <summary>
    /// 发一条命令并等配对应答。返回数据段（原始 buffer 下标 8 起，长度 = length）。
    ///
    /// 只认 cmd + offset 都对上的帧：无线接收器会把请求原样回显一遍，
    /// 回显没有数据段，光比 cmd 会把回显当成应答。
    /// </summary>
    public static bool TryCommand(
        HidConnection connection,
        HidInputChannel channel,
        byte command,
        byte length,
        byte offset,
        ReadOnlySpan<byte> payload,
        TimeSpan timeout,
        out byte[]? data)
    {
        data = null;
        channel.Drain();

        var frame = BuildFrame(connection.Info.OutputReportLength, command, length, offset, payload);
        if (!connection.TryWriteOutput(frame, out _))
        {
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var report = channel.Read(TimeSpan.FromMilliseconds(120));
            if (report is null || report.Length < HeaderLength)
            {
                continue;
            }

            if (report[3] != command || report[5] != offset)
            {
                continue;
            }

            var available = report.Length - HeaderLength;
            var take = Math.Max(0, Math.Min(length, available));
            data = report.AsSpan(HeaderLength, take).ToArray();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 读电量。应答 6 字节：<c>[0]=电量%</c>、<c>[1]=充电标志</c>。
    ///
    /// 和 MCHOSE / 网页驱动那两套不同，这里**不需要**先握手报在线：
    /// 接收器会把「键盘睡了」表达成不回包，而不是回一个失败标志。
    /// </summary>
    public static bool TryReadBattery(
        HidConnection connection,
        HidInputChannel channel,
        int attempts,
        out int percent,
        out bool charging)
    {
        percent = 0;
        charging = false;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (!TryCommand(connection, channel, CmdGetBattery, length: 6, offset: 0, default, TimeSpan.FromMilliseconds(600), out var data)
                || data is null
                || data.Length < 2)
            {
                continue;
            }

            percent = data[0];
            charging = data[1] != 0;
            return percent is > 0 and <= 100;
        }

        return false;
    }

    public static bool TryReadFunctionInformation(HidConnection connection, HidInputChannel channel, out byte[] buffer)
    {
        buffer = new byte[FunctionInformationSize];

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!TryCommand(connection, channel, CmdGetFunctionInformation, 24, 0, default, TimeSpan.FromMilliseconds(600), out var first)
                || first is null || first.Length < 24)
            {
                continue;
            }

            if (!TryCommand(connection, channel, CmdGetFunctionInformation, 24, 24, default, TimeSpan.FromMilliseconds(600), out var second)
                || second is null || second.Length < 24)
            {
                continue;
            }

            first.AsSpan(0, 24).CopyTo(buffer.AsSpan(0));
            second.AsSpan(0, 24).CopyTo(buffer.AsSpan(24));
            return true;
        }

        return false;
    }

    /// <summary>
    /// 写回整块功能信息。必须整块回写：设备没有「只改一个字段」的命令，
    /// 单发 21 号字节会把其余灯效/按键配置一起写成 0。
    /// </summary>
    public static bool TryWriteFunctionInformation(HidConnection connection, HidInputChannel channel, byte[] buffer)
    {
        if (buffer.Length < FunctionInformationSize)
        {
            return false;
        }

        if (!TryCommand(connection, channel, CmdSetFunctionInformation, 24, 0, buffer.AsSpan(0, 24), TimeSpan.FromMilliseconds(600), out _))
        {
            return false;
        }

        Thread.Sleep(60);

        return TryCommand(connection, channel, CmdSetFunctionInformation, 24, 24, buffer.AsSpan(24, 24), TimeSpan.FromMilliseconds(600), out _);
    }

    /// <summary>
    /// 改功能信息块里的一个字段：读整块 → 改 → 整块写回 → 回读校验 → 失败回滚。
    ///
    /// <paramref name="verify"/> 只校验目标字段，不做整块比对 ——
    /// 固件会顺手规范化别的字节（例如换灯效模式会连带改速度/方向），整块比会误判成失败并触发无谓回滚。
    /// </summary>
    public static bool TryMutateFunctionInformation(
        HidConnection connection,
        HidInputChannel channel,
        Action<byte[]> mutate,
        Func<byte[], bool> verify,
        out byte[]? updated,
        out string? error)
    {
        updated = null;

        if (!TryReadFunctionInformation(connection, channel, out var current))
        {
            error = "read function information failed";
            return false;
        }

        var backup = (byte[])current.Clone();
        mutate(current);

        if (!TryWriteFunctionInformation(connection, channel, current))
        {
            error = "write function information rejected";
            return false;
        }

        // 固件落盘有延迟，立刻读会拿到旧值。
        Thread.Sleep(150);

        if (!TryReadFunctionInformation(connection, channel, out var confirm))
        {
            TryWriteFunctionInformation(connection, channel, backup);
            error = "read-back after write failed";
            return false;
        }

        if (!verify(confirm))
        {
            // 回滚到写之前的整块，不把设备留在半改状态。
            TryWriteFunctionInformation(connection, channel, backup);
            error = "firmware did not accept the new value";
            return false;
        }

        updated = confirm;
        error = null;
        return true;
    }

    /// <summary>版本号在 <c>data[4..6]</c>，VHUB 的拼法是 <c>data[5].ToString(16) + data[4].ToString(16)</c>。</summary>
    public static bool TryReadVersion(HidConnection connection, HidInputChannel channel, out string version)
    {
        version = string.Empty;

        if (!TryCommand(connection, channel, CmdGetVersion, 24, 0, default, TimeSpan.FromMilliseconds(600), out var data)
            || data is null || data.Length < 6)
        {
            return false;
        }

        version = $"{data[5]:X2}{data[4]:X2}";
        return true;
    }
}
