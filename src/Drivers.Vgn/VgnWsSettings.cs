using GearTray.Core;

namespace GearTray.Drivers.Vgn;

/// <summary>功能信息块里一个字段的编解码方式。</summary>
public enum FieldEncoding
{
    Byte,
    UInt16LE,
    Rgb,
}

/// <summary>
/// 功能信息块里一个字段：块内下标 + 编解码 + 报告给 UI 的选项。
///
/// 这里刻意只描述「怎么读写」，不掺设备判断 —— 探测器、驱动、面板都从这一份表取，
/// 才不会出现「文档写 21、代码写 22」这种漂移。
/// </summary>
public sealed record FieldSpec(
    string Key,
    string Group,
    string Label,
    DeviceSettingKind Kind,
    int Index,
    FieldEncoding Encoding,
    IReadOnlyList<DeviceSettingOption> Options,
    string? Hint = null)
{
    public int Read(byte[] block) => Encoding switch
    {
        FieldEncoding.UInt16LE => block[Index] | (block[Index + 1] << 8),
        FieldEncoding.Rgb => (block[Index] << 16) | (block[Index + 1] << 8) | block[Index + 2],
        _ => block[Index],
    };

    public void Write(byte[] block, int value)
    {
        switch (Encoding)
        {
            case FieldEncoding.UInt16LE:
                block[Index] = (byte)(value & 0xFF);
                block[Index + 1] = (byte)((value >> 8) & 0xFF);
                break;
            case FieldEncoding.Rgb:
                block[Index] = (byte)((value >> 16) & 0xFF);
                block[Index + 1] = (byte)((value >> 8) & 0xFF);
                block[Index + 2] = (byte)(value & 0xFF);
                break;
            default:
                block[Index] = (byte)value;
                break;
        }
    }

    /// <summary>
    /// 回读校验。开关类只比「零 / 非零」而不是精确值：
    /// 实测把六键无冲写成 1，设备回读是 <c>0xFF</c> —— 精确比对会把一次成功的写入判成失败并触发回滚。
    /// 这不是妥协，是这台固件的行为，必须按它认。
    /// </summary>
    public bool MatchesReadBack(int actual, int expected) =>
        Kind == DeviceSettingKind.Toggle ? (actual == 0) == (expected == 0) : actual == expected;

    public DeviceSetting ToSetting(byte[] block) =>
        new(Key, Group, Label, Kind, Read(block), Options);
}

/// <summary>
/// VGN 键盘（weisheng 方案）功能信息块的字段表。
/// 下标来源：VHUB <c>WsServe</c> 的 set* 方法，逐个核对过，没有靠试探猜的。
///
/// Toggle 的 <c>Options[0]</c> 恒为「开」：设备用 0 表示开的字段（灯效开关、六键无冲、低延迟）
/// 直接把 0 写在第一项，语义就不会反。
/// </summary>
public static class VgnWsSettings
{
    public const string GroupLight = "灯效";

    public const string GroupPerformance = "性能";

    /// <summary>1=开 0=关（多数字段）。</summary>
    private static readonly IReadOnlyList<DeviceSettingOption> OnOffOptions =
    [
        new(1, "开"),
        new(0, "关"),
    ];

    /// <summary>0=开 1=关（灯效开关、六键无冲这类反直觉的固件位）。</summary>
    private static readonly IReadOnlyList<DeviceSettingOption> ZeroOnOffOptions =
    [
        new(0, "开"),
        new(1, "关"),
    ];

    /// <summary>Win 锁不进 <see cref="Specs"/>（它有强类型接口），但写入流程和字段表一致，单独放一条 spec。</summary>
    public static readonly FieldSpec WinKeyLock = new(
        "winlock", "系统", "Win 键锁", DeviceSettingKind.Toggle,
        VgnWsProtocol.WinKeyLockIndex, FieldEncoding.Byte, OnOffOptions);

    public static readonly IReadOnlyList<FieldSpec> Specs =
    [
        new("light.enabled", GroupLight, "灯效开关", DeviceSettingKind.Toggle,
            VgnWsProtocol.LightSwitchIndex, FieldEncoding.Byte, ZeroOnOffOptions,
            "设备 0=开 1=关"),

        new("light.mode", GroupLight, "灯效模式", DeviceSettingKind.Choice,
            VgnWsProtocol.LightModeIndex, FieldEncoding.Byte,
            VgnWsProtocol.LightModeOptions.Select(o => new DeviceSettingOption(o.Value, o.Label)).ToArray(),
            "部分模式视型号而定，写了没效果不代表失败"),

        new("light.brightness", GroupLight, "亮度", DeviceSettingKind.Choice,
            VgnWsProtocol.LightLuminanceIndex, FieldEncoding.Byte,
            [
                new(0, "最亮"), new(1, "亮"), new(2, "中"), new(3, "暗"), new(4, "最暗"),
            ]),

        new("light.speed", GroupLight, "灯速", DeviceSettingKind.Choice,
            VgnWsProtocol.LightSpeedIndex, FieldEncoding.Byte,
            [
                new(0, "最快"), new(1, "快"), new(2, "中"), new(3, "慢"), new(4, "最慢"),
            ]),

        new("light.direction", GroupLight, "灯效方向", DeviceSettingKind.Choice,
            VgnWsProtocol.LightDirectionIndex, FieldEncoding.Byte,
            [
                new(0, "左 → 右"), new(1, "右 → 左"),
            ]),

        new("light.color", GroupLight, "静态颜色", DeviceSettingKind.Color,
            VgnWsProtocol.LightColorIndex, FieldEncoding.Rgb, []),

        new("light.chroma", GroupLight, "混色", DeviceSettingKind.Toggle,
            VgnWsProtocol.LightChromaIndex, FieldEncoding.Byte, OnOffOptions),

        new("perf.reportingRate", GroupPerformance, "轮询率", DeviceSettingKind.Choice,
            VgnWsProtocol.ReportingRateIndex, FieldEncoding.Byte,
            VgnWsProtocol.ReportingRateOptions.Select(o => new DeviceSettingOption(o.Value, o.Label)).ToArray(),
            "2.4G 下切 1000Hz 可能掉线，切完没回包就插线恢复"),

        new("perf.sixKey", GroupPerformance, "六键无冲", DeviceSettingKind.Toggle,
            VgnWsProtocol.SixKeyIndex, FieldEncoding.Byte, ZeroOnOffOptions,
            "设备 0=开，关态回读为 0xFF"),

        new("perf.scanKeyDelay", GroupPerformance, "超低延迟", DeviceSettingKind.Toggle,
            VgnWsProtocol.UltraLowLatencyIndex, FieldEncoding.Byte, OnOffOptions),

        new("perf.lowKeyDelay", GroupPerformance, "低延迟", DeviceSettingKind.Toggle,
            VgnWsProtocol.LowLatencyIndex, FieldEncoding.Byte, OnOffOptions,
            "设备用 0x00/0xFF 表达"),

        new("perf.directionKey", GroupPerformance, "方向键交换", DeviceSettingKind.Toggle,
            VgnWsProtocol.DirectionKeyIndex, FieldEncoding.Byte, OnOffOptions,
            "把 WASD 与方向键对调"),

        // 休眠存的是秒（两字节小端），不是档位序号，所以选项值直接用秒。
        new("power.dormancy", "电源", "休眠时间", DeviceSettingKind.Choice,
            VgnWsProtocol.DormancyIndex, FieldEncoding.UInt16LE,
            VgnWsProtocol.DormancyOptions
                .Select(o => new DeviceSettingOption(o.Seconds < 0 ? 0xFFFF : o.Seconds, o.Label))
                .ToArray()),

        new("system.osMode", "系统", "Mac 模式", DeviceSettingKind.Toggle,
            VgnWsProtocol.OsModeIndex, FieldEncoding.Byte, OnOffOptions),
    ];

    public static FieldSpec? Find(string key) =>
        string.Equals(key, WinKeyLock.Key, StringComparison.OrdinalIgnoreCase)
            ? WinKeyLock
            : Specs.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
}
