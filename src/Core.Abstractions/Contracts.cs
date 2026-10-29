namespace GearTray.Core;

/// <summary>设备被建模为「身份 + 能力集合」，UI 按能力投影控件，不按设备名判断。</summary>
public enum DeviceCapability
{
    Battery,
    DpiProfile,
    WinKeyLock,
    PollingRate,
    ProfileSwitch,
    FirmwareInfo,
}

public enum DeviceHealth
{
    Online,
    Asleep,
    Stale,
    Faulted,
    Disconnected,
}

public enum BatteryState
{
    Unknown,
    Discharging,
    Charging,
    Full,
}

public enum DriverKind
{
    Descriptor,
    Native,
    Generic,
}

/// <summary>
/// 电量必须能表达「不确定」：鼠标休眠收不到回复是正常现象，
/// 应保留上次值并标记 IsStale，而不是把界面清成「读取失败」。
/// </summary>
public readonly record struct BatteryLevel(int? Percent, BatteryState State, DateTimeOffset SampledAt, bool IsStale)
{
    public static readonly BatteryLevel Unknown = new(null, BatteryState.Unknown, DateTimeOffset.MinValue, true);

    public static BatteryLevel Of(int percent, BatteryState state = BatteryState.Discharging) =>
        new(percent, state, DateTimeOffset.Now, false);

    public static BatteryLevel StaleOf(BatteryLevel previous) => previous with { IsStale = true };

    public string Display => Percent is { } p ? $"{p}%" : "—";

    public override string ToString() => IsStale && Percent is { } p ? $"{p}% (stale)" : Display;
}

public sealed record DeviceIdentity(
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    string? ProductName,
    string? SerialNumber,
    string DevicePath)
{
    public string VidPid => $"VID_{VendorId:X4}&PID_{ProductId:X4}";

    public string DisplayName =>
        string.IsNullOrWhiteSpace(ProductName) ? VidPid : ProductName!;

    public string DeviceKey => DevicePath;
}

public sealed record DriverManifest(
    string Id,
    string DisplayName,
    Version Version,
    DriverKind Kind,
    string? Description = null);

/// <summary>
/// 当前 DPI 档位快照。Stages 只包含设备报告的有效档位（不超过硬件档位数）。
/// </summary>
public sealed record DpiProfile(IReadOnlyList<int> Stages, int ActiveIndex)
{
    public static readonly DpiProfile Empty = new(Array.Empty<int>(), 0);

    public int ActiveDpi =>
        Stages.Count == 0 ? 0 : Stages[Math.Clamp(ActiveIndex, 0, Stages.Count - 1)];

    public string Display => ActiveDpi > 0 ? $"{ActiveDpi} DPI" : "—";
}

/// <summary>
/// 可选能力：能改 DPI 的设备实现这个接口，不实现的设备 UI 就不画 DPI 控件。
/// 能力探测靠 <c>session is IDpiControl</c>，不靠 DeviceCapability 枚举猜。
/// </summary>
public interface IDpiControl
{
    Task<DpiProfile?> ReadDpiAsync(CancellationToken cancellationToken);

    /// <summary>切换生效档位。返回是否已读回确认。</summary>
    Task<bool> SetActiveStageAsync(int stageIndex, CancellationToken cancellationToken);

    /// <summary>改某一档的 DPI 数值。返回是否已读回确认。</summary>
    Task<bool> SetStageValueAsync(int stageIndex, int dpi, CancellationToken cancellationToken);
}

/// <summary>
/// Win 键锁定的实现位置。必须如实暴露：Firmware 是写进鼠标固件（换机器仍有效），
/// Software 只是本程序用全局键盘钩子拦下，退出即失效。
/// </summary>
public enum WinKeyLockMode
{
    Firmware,
    Software,
}

public interface IWinKeyLockControl
{
    WinKeyLockMode Mode { get; }

    bool IsLocked { get; }

    /// <summary>false 表示钩子/固件通道不可用，UI 必须禁用开关而不是假装已生效。</summary>
    bool IsAvailable { get; }

    string? LastError { get; }

    bool SetLocked(bool locked);

    /// <summary>
    /// 主动拉一次真实状态。键盘上按 FN+Win 也能切锁，光靠缓存会和实际不符。
    /// 软件钩子（状态本来就在进程内）不需要实现。
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>UI 用的 Win 锁投影：只读 + 可用性，避免面板直接摸会话。</summary>
public sealed record WinKeyLockState(bool IsLocked, bool IsAvailable, WinKeyLockMode Mode, string? LastError = null)
{
    public static readonly WinKeyLockState Unavailable = new(false, false, WinKeyLockMode.Firmware);
}

public enum DeviceSettingKind
{
    /// <summary>两项，UI 画开关。</summary>
    Toggle,

    /// <summary>多选一，UI 画下拉或分段按钮。</summary>
    Choice,

    /// <summary>Value 编码为 0xRRGGBB。</summary>
    Color,
}

public sealed record DeviceSettingOption(int Value, string Label);

/// <summary>
/// 一个可调固件项。Value 一律是**设备侧原始值**，不做 UI 语义映射 ——
/// 显示层按 Options 找标签，找不到就显示数值，绝不像 VHUB 那样把速度档反着翻两次。
///
/// <see cref="DeviceSettingKind.Toggle"/> 的约定：<c>Options[0]</c> 必须是「开」。
/// 有些固件位是 0=开（灯效开关、六键无冲），把它们的第一项写成 0 即可，
/// 不要另设「反相」标志 —— 那个标志会和选项表互相打脸。
/// </summary>
public sealed record DeviceSetting(
    string Key,
    string Group,
    string Label,
    DeviceSettingKind Kind,
    int Value,
    IReadOnlyList<DeviceSettingOption> Options,
    string? Hint = null)
{
    public string DisplayValue => Kind switch
    {
        DeviceSettingKind.Toggle => IsOn ? "开" : "关",
        DeviceSettingKind.Color => $"#{Value:X6}",
        _ => Options.FirstOrDefault(o => o.Value == Value)?.Label ?? Value.ToString(),
    };

    public bool IsOn => Options.Count > 0 && Value == Options[0].Value;
}

/// <summary>
/// 可选能力：能改固件设置项的会话实现这个接口。
/// 与 <see cref="IDpiControl"/> 同理，能力探测靠 <c>session is IDeviceSettings</c>。
///
/// 约定：写入必须「读整块 → 只改目标字段 → 整块写回 → 回读校验 → 失败则回滚」，
/// 因为这类设备没有「只改一个字段」的命令，单写一位会把相邻配置一起清掉。
/// </summary>
public interface IDeviceSettings
{
    IReadOnlyList<DeviceSetting> Settings { get; }

    bool IsAvailable { get; }

    string? LastError { get; }

    /// <summary>主动拉一次真实值：FN 组合键也能改这些项，缓存会过期。</summary>
    Task RefreshAsync(CancellationToken cancellationToken);

    /// <summary>写一项。返回是否已回读确认；false 时设备侧保持原值。</summary>
    bool SetSetting(string key, int value);
}

/// <summary>
/// 一次设备会话。所有方法都是异步的：宿主内部切线程，
/// 从接口设计上堵死「在 UI 线程同步等 HID」这条路。
/// </summary>
public interface IDeviceSession : IAsyncDisposable
{
    DeviceIdentity Identity { get; }

    IReadOnlySet<DeviceCapability> Capabilities { get; }

    DeviceHealth Health { get; }

    Task<BatteryLevel?> ReadBatteryAsync(CancellationToken cancellationToken);
}

public interface IDeviceDriver
{
    DriverManifest Manifest { get; }

    /// <summary>0 = 不接管。评分竞标，不靠注册顺序。</summary>
    int MatchScore(DeviceIdentity identity);

    Task<IDeviceSession> OpenAsync(DeviceIdentity identity, CancellationToken cancellationToken);
}

/// <summary>UI 只消费不可变快照，永远不直接持有会话。</summary>
public sealed record DeviceSnapshot(
    DeviceIdentity Identity,
    IReadOnlySet<DeviceCapability> Capabilities,
    DeviceHealth Health,
    BatteryLevel Battery,
    DpiProfile? Dpi,
    DateTimeOffset UpdatedAt,
    WinKeyLockState? WinKeyLock = null,
    IReadOnlyList<DeviceSetting>? Settings = null)
{
    public static DeviceSnapshot Empty(DeviceIdentity identity) =>
        new(identity, new HashSet<DeviceCapability>(), DeviceHealth.Disconnected, BatteryLevel.Unknown, null, DateTimeOffset.Now);
}
