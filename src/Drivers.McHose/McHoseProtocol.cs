using GearTray.Transport.Hid;

namespace GearTray.Drivers.McHose;

/// <summary>一次成功的电量读取。Decoded 保留原始解码结果，诊断与后续扩展都要用。</summary>
public readonly record struct McHoseBattery(int Percent, bool Charging, byte[] Decoded);

/// <summary>
/// MCHOSE 经典 Feature Report 协议（A7 / A7 V2 Ultra / Pro / AX5 / L7 / M7 同族）。
///
/// 协议要点：
///   1. 走 Feature Report，不是 Output Report；
///   2. Report 0x11 = 20 字节控制通道（含 Report ID 的缓冲区是 21 字节），
///      Report 0x12 = 64 字节配置通道（缓冲区 65 字节）；
///   3. **载荷整体 XOR 0xFF**，补位也是 0xFF（等价于「补零后取反」）。
///
/// 电量查询：发 <c>0x11 [0x06]</c>；读回后「去 Report ID → 取反 → 跳过前 2 字节」，
/// 再取 data[8] 为电量、data[9] 为充电标记 —— 换算成取反数组下标即 [10] / [11]。
///
/// 2.4G 不是同步链路：dongle 需要 1~3 个 RF 周期才能把无线端的数据取回来，
/// 首次读回常常是全零。所以「发一次就判失败」是错的，必须重试。
/// </summary>
public static class McHoseProtocol
{
    public const byte ControlReportId = 0x11;

    /// <summary>缓冲区长度 = 1 字节 Report ID + 20 字节载荷。传 20 会被驱动拒绝（ERROR_INVALID_PARAMETER=87）。</summary>
    public const int ControlBufferLength = 21;

    /// <summary>配置通道：64 字节载荷，缓冲区含 Report ID 共 65 字节。</summary>
    public const byte ConfigReportId = 0x12;

    public const int ConfigBufferLength = 65;

    /// <summary>一次 0x12 读回来的解码后载荷长度（已去 ID）。</summary>
    public const int ConfigPayloadLength = 64;

    private const byte BatteryCommand = 0x06;

    /// <summary>读全部板载配置。</summary>
    private const byte ReadSettingsCommand = 0x67;

    /// <summary>写 DPI 档位表。</summary>
    private const byte DpiCommand = 0x40;

    /// <summary>取反后数组中电量 / 充电标记的下标。</summary>
    private const int BatteryIndex = 10;
    private const int ChargingIndex = 11;

    /// <summary>参考实现实测的有效等待时间；短于它会读到上一次的缓存值。</summary>
    public const int ReadDelayMs = 80;

    /// <summary>
    /// 读电量，内部最多重试 <paramref name="attempts"/> 次。
    /// 失败不抛异常：设备休眠是正常状态，由调用方决定标 Stale 还是 Asleep。
    /// </summary>
    public static bool TryRead(
        HidConnection connection,
        int attempts,
        out McHoseBattery reading,
        out string? lastError)
    {
        reading = default;
        lastError = null;

        var query = new byte[ControlBufferLength];
        Array.Fill(query, (byte)0xFF);
        query[0] = ControlReportId;
        query[1] = (byte)(BatteryCommand ^ 0xFF);

        for (var attempt = 0; attempt < Math.Max(attempts, 1); attempt++)
        {
            if (!connection.TrySetFeature(query, out var error))
            {
                lastError = Describe(error);
                continue;
            }

            Thread.Sleep(ReadDelayMs);

            var response = connection.GetFeature(ControlReportId, ControlBufferLength);
            if (response is null)
            {
                lastError = "no feature response";
                continue;
            }

            if (!TryDecode(response, out reading, out var reason))
            {
                lastError = reason;
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// 解码 21 字节原始应答。全零应答（去 ID 后）表示 dongle 还没取回数据，不能当 0% 电量。
    /// </summary>
    public static bool TryDecode(byte[] response, out McHoseBattery reading, out string? reason)
    {
        reading = default;
        reason = null;

        if (response.Length < ControlBufferLength)
        {
            reason = $"short response ({response.Length})";
            return false;
        }

        // 去 Report ID → 20 字节载荷 → 整体取反。
        var decoded = new byte[response.Length - 1];
        for (var i = 1; i < response.Length; i++)
        {
            decoded[i - 1] = (byte)(response[i] ^ 0xFF);
        }

        // 应答首字节是命令回显（0x06）。这里不做硬校验：单字符差异会让整条读取失败，
        // 而真正的判据是「电量值是否合理」—— 0 和 >100 都只可能来自无效应答。
        var level = decoded[BatteryIndex];

        // 老固件用 1..10 表示 10%..100%；新固件直接给 0..100。
        if (level is >= 1 and <= 10)
        {
            level *= 10;
        }

        // 0 不可能是真实电量（0% 早就关机了），只会是无效应答。
        if (level is 0 or > 100)
        {
            reason = $"implausible level (0x{decoded[BatteryIndex]:X2})";
            return false;
        }

        reading = new McHoseBattery(level, decoded[ChargingIndex] == 1, decoded);
        return true;
    }

    /// <summary>
    /// 读全部板载配置（Report 0x12 / 命令 0x67）。
    /// 返回的 <see cref="McHoseSettings"/> 里 DpiStages 固定 6 项，前 StageCount 项有效。
    /// </summary>
    public static bool TryReadSettings(
        HidConnection connection,
        int attempts,
        out McHoseSettings? settings,
        out string? lastError)
    {
        settings = null;
        lastError = null;

        var query = new byte[ConfigBufferLength];
        Array.Fill(query, (byte)0xFF);
        query[0] = ConfigReportId;
        query[1] = (byte)(ReadSettingsCommand ^ 0xFF);

        for (var attempt = 0; attempt < Math.Max(attempts, 1); attempt++)
        {
            if (!connection.TrySetFeature(query, out var error))
            {
                lastError = Describe(error);
                continue;
            }

            // 0x12 的应答比 0x11 慢：固件要把整块板载配置读出来，80ms 常常不够。
            Thread.Sleep(ReadDelayMs);

            var response = connection.GetFeature(ConfigReportId, ConfigBufferLength);
            if (response is null)
            {
                lastError = "no feature response";
                continue;
            }

            if (!TryDecodeSettings(response, out settings, out var reason))
            {
                lastError = reason;
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>解码 65 字节原始应答为板载配置。</summary>
    public static bool TryDecodeSettings(byte[] response, out McHoseSettings? settings, out string? reason)
    {
        settings = null;
        reason = null;

        if (response.Length < ConfigBufferLength)
        {
            reason = $"short response ({response.Length})";
            return false;
        }

        var raw = InvertPayload(response);

        // 空应答：整块未填充（取反后全 0x00）。绝不能当成「所有档位都是 0 DPI」。
        if (raw.All(b => b == 0x00))
        {
            reason = "empty response";
            return false;
        }

        // raw[0] 是命令回显（0x67）。校验它才能确定这块数据确实属于本次查询，
        // 而不是上一轮残留 —— 2.4G 链路下这是真实存在的串包风险。
        if (raw[0] != ReadSettingsCommand)
        {
            reason = $"command echo mismatch (0x{raw[0]:X2})";
            return false;
        }

        // 跳过回显字节，之后的 data[0] 才是 profileIndex。
        var data = raw.AsSpan(1);

        var byte1 = data[1];
        var byte2 = data[2];
        var gDpi = byte1 & 0x0F;
        var gRate = (byte1 >> 4) & 0x0F;
        var usbDpi = byte2 & 0x0F;
        var usbRate = (byte2 >> 4) & 0x0F;

        var stages = new int[6];
        for (var i = 0; i < 6; i++)
        {
            stages[i] = data[4 + (i * 2)] | (data[4 + (i * 2) + 1] << 8);
        }

        var stageCount = data[16];
        if (stageCount is < 1 or > 6)
        {
            stageCount = 1;
        }

        // 判据：生效档位的 DPI 必须落在传感器量程内。空应答/残包在这一步被挡掉。
        var activeDpi = (gDpi <= 5) ? gDpi : usbDpi;
        if (activeDpi > 5)
        {
            activeDpi = 0;
        }

        if (activeDpi >= stageCount)
        {
            activeDpi = stageCount - 1;
        }

        if (stages[activeDpi] is < 50 or > 42000)
        {
            reason = $"implausible dpi (stage{activeDpi + 1}={stages[activeDpi]})";
            return false;
        }

        // 无线优先用 freeRate，有线优先 usbRate；两者相等时用谁都一样。
        var rateIndex = (usbRate <= 5 && gRate <= 5 && usbRate != gRate) ? Math.Max(usbRate, gRate) : Math.Min(usbRate, gRate);
        if (rateIndex > 5)
        {
            rateIndex = 0;
        }

        var sleepRaw = data[19];
        var rotation = data.Length > 49 ? (sbyte)data[49] : (sbyte)0;

        settings = new McHoseSettings(
            ProfileIndex: data[0],
            ActiveStageIndex: activeDpi,
            DpiStages: stages,
            StageCount: stageCount,
            PollingRateIndex: rateIndex,
            DebounceMs: data[18] is >= 1 and <= 20 ? data[18] : 8,
            SleepMinutes: sleepRaw == 0 ? 3 : sleepRaw,
            NeverSleep: sleepRaw == 0,
            SensorFlags: data[17],
            RotationDegrees: rotation is >= -30 and <= 30 ? rotation : 0,
            Raw: raw);

        return true;
    }

    /// <summary>去 Report ID 并整体取反，得到协议语义上的载荷。</summary>
    private static byte[] InvertPayload(byte[] response)
    {
        var decoded = new byte[response.Length - 1];
        for (var i = 1; i < response.Length; i++)
        {
            decoded[i - 1] = (byte)(response[i] ^ 0xFF);
        }

        return decoded;
    }

    /// <summary>
    /// 向 0x12 通道写入 64 字节语义载荷（内部取反 + 补 0xFF）。
    /// payload[0] 必须是命令字：写 DPI 是 0x40。
    /// </summary>
    public static bool TryWriteConfig(HidConnection connection, byte[] payload, byte expectedCommand, out int error)
    {
        error = 0;

        if (payload.Length == 0 || payload[0] != expectedCommand)
        {
            error = -1;
            return false;
        }

        var buffer = new byte[ConfigBufferLength];
        Array.Fill(buffer, (byte)0xFF);
        buffer[0] = ConfigReportId;

        for (var i = 0; i < Math.Min(payload.Length, ConfigPayloadLength); i++)
        {
            buffer[1 + i] = (byte)(payload[i] ^ 0xFF);
        }

        return connection.TrySetFeature(buffer, out error);
    }

    /// <summary>写 DPI 档位表。payload[0] = 0x40、[1][2] = 当前档位索引、[4..15] = 6 档 16 位小端。</summary>
    public static bool TryWriteDpi(HidConnection connection, byte[] payload, out int error) =>
        TryWriteConfig(connection, payload, DpiCommand, out error);

    /// <summary>
    /// 构造 0x40 写入载荷。布局来自可工作的参考实现，与 0x67 的**读回布局不一致**
    /// （读回里 X 轴表在 raw[5..16]、sensor/debounce/sleep 在 raw[18..20]；
    ///  而写入模板把 X 轴表放 [4..15]、[17] 固定 0xFF、[18..29] 放 Y 轴表）。
    /// 所以绝不能用「读回数据原样回填」的方式构造写入帧。
    /// </summary>
    public static byte[] BuildDpiPayload(IReadOnlyList<int> stages, int activeIndex, int stageCount)
    {
        var payload = new byte[ConfigPayloadLength];
        payload[0] = DpiCommand;
        payload[1] = (byte)activeIndex;
        payload[2] = (byte)activeIndex;
        payload[3] = 0x00;

        for (var i = 0; i < 6; i++)
        {
            var value = i < stages.Count ? stages[i] : 800;
            payload[4 + (i * 2)] = (byte)(value & 0xFF);
            payload[5 + (i * 2)] = (byte)((value >> 8) & 0xFF);
        }

        payload[16] = (byte)stageCount;

        // 0xFF = 统一 X/Y。拆轴时为其它值。
        payload[17] = 0xFF;

        // Y 轴镜像：与 X 相同即统一灵敏度。
        for (var i = 0; i < 6; i++)
        {
            var value = i < stages.Count ? stages[i] : 800;
            payload[18 + (i * 2)] = (byte)(value & 0xFF);
            payload[19 + (i * 2)] = (byte)((value >> 8) & 0xFF);
        }

        return payload;
    }

    private static string Describe(int error) => error switch
    {
        5 => "access denied",
        87 => "invalid parameter",
        31 => "general failure",
        1167 => "device not connected",
        _ => $"win32 {error}",
    };
}

/// <summary>
/// 0x12 / 0x67 读出来的板载配置快照。
///
/// 下标口径：**已去掉 Report ID、已取反、已跳掉命令回显字节**，即代码里的 <c>data[]</c>。
/// 这和公开文档里的 data[i] 是同一套坐标（那边是 Skip(2) 之后）。
/// </summary>
public sealed record McHoseSettings(
    int ProfileIndex,
    int ActiveStageIndex,
    IReadOnlyList<int> DpiStages,
    int StageCount,
    int PollingRateIndex,
    int DebounceMs,
    int SleepMinutes,
    bool NeverSleep,
    byte SensorFlags,
    int RotationDegrees,
    byte[] Raw)
{
    /// <summary>当前生效档位的 DPI 值。索引越界时回退到第一档。</summary>
    public int ActiveDpi =>
        DpiStages.Count == 0 ? 0 : DpiStages[Math.Clamp(ActiveStageIndex, 0, DpiStages.Count - 1)];
}
