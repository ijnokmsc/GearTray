using GearTray.Core;
using GearTray.Transport.Hid;

namespace GearTray.Drivers.McHose;

/// <summary>
/// MCHOSE 音频设备驱动（V9 系列耳机）。
///
/// 与鼠标那套 Feature 协议（0x11 / XOR 0xFF）**毫无关系**，是另一条链路：
///
///   接口       0xFF90（in/out 各 64 字节，无 Feature）
///   Report ID  0x55
///   请求       Output Report：[0x55][0x65][0x01][0..]  共 64 字节
///   应答       Input  Report：[0x55][0x65][percent][status][0..]
///
/// 协议来源：M HUB 客户端远端渲染层的 <c>hidDeviceOptions</c> 模块（厂商自己的实现），
/// 已与本机 V9 Turbo+ 实测报文逐字节核对：发 55 65 01 回 55 65 64 02 → 100% 放电中。
///
/// 一个必须记住的偏移陷阱：官方代码里写的是 <c>reply[1]</c> 是电量、<c>reply[2]</c> 是状态，
/// 但那是 **WebHID 的 event.data**，按规范不含 Report ID。
/// 落到 Win32 的 ReadFile / 我们自己的通道上，报文是含 ID 的，所以电量在 <c>raw[2]</c>。
/// 照抄官方下标会把 100% 读成命令回显 0x65。
/// </summary>
public sealed class McHoseHeadsetDriver : IDeviceDriver
{
    private const ushort VendorId = 0x3837;

    /// <summary>已实测可用的接口。新增音频设备往这里加一行，不要改判定逻辑。</summary>
    private static readonly HashSet<(ushort ProductId, ushort UsagePage)> Verified =
    [
        (0x600A, 0xFF90), // V9 Turbo+
    ];

    public DriverManifest Manifest { get; } = new(
        Id: "mchose.headset-output",
        DisplayName: "MCHOSE 音频设备协议",
        Version: new Version(0, 1, 0),
        Kind: DriverKind.Native,
        Description: "V9 系列耳机：接口 0xFF90 / Report 0x55 / Output Report 查询电量");

    public int MatchScore(DeviceIdentity identity)
    {
        if (identity.VendorId != VendorId)
        {
            return 0;
        }

        if (Verified.Contains((identity.ProductId, identity.UsagePage)))
        {
            return 100;
        }

        // 同族未验证的音频设备：只有 0xFF90 这个接口给低分。
        // 注意不能对同 PID 的其它接口也给分 —— 同一只耳机挂了 6 个接口，
        // 全给分会退化成「一只耳机 6 张卡片」。
        return identity.UsagePage == 0xFF90 ? 60 : 0;
    }

    public Task<IDeviceSession> OpenAsync(DeviceIdentity identity, CancellationToken cancellationToken)
        => Task.FromResult<IDeviceSession>(new McHoseHeadsetSession(identity));
}

internal sealed class McHoseHeadsetSession : IDeviceSession
{
    private const byte ReportId = 0x55;
    private const byte BatteryCommand = 0x65;

    /// <summary>命令参数：0x01 = 读取。官方实现里同一命令字配 0x00 是另一组用途。</summary>
    private const byte BatteryReadArg = 0x01;

    /// <summary>读的是「去掉 Report ID 之后」的第 1、2 字节，换算到含 ID 的报文就是 2、3。</summary>
    private const int PercentIndex = 2;

    private const int StatusIndex = 3;

    private const int MaxAttempts = 3;

    private static readonly HashSet<DeviceCapability> SupportedCapabilities = [DeviceCapability.Battery];

    private readonly HidConnection? _connection;
    private readonly HidInputChannel? _channel;
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    private DeviceHealth _health = DeviceHealth.Online;
    private BatteryLevel _last = BatteryLevel.Unknown;

    public McHoseHeadsetSession(DeviceIdentity identity)
    {
        Identity = identity;

        try
        {
            _connection = HidConnection.Open(identity.DevicePath);
            _channel = _connection is null
                ? null
                : HidInputChannel.Open(identity.DevicePath, Math.Max(64, _connection.Info.InputReportLength));
        }
        catch (Exception)
        {
            _connection = null;
            _channel = null;
        }

        // 写句柄和读句柄必须分开：同一句柄上「阻塞读 + 并发写」会让进程静默消失。
        if (_connection is null || _channel is null || !_connection.CanWrite)
        {
            _health = DeviceHealth.Faulted;
            _connection?.Dispose();
            _connection = null;
            _channel?.Dispose();
            _channel = null;
        }
    }

    public DeviceIdentity Identity { get; }

    public IReadOnlySet<DeviceCapability> Capabilities =>
        _health == DeviceHealth.Faulted ? new HashSet<DeviceCapability>() : SupportedCapabilities;

    public DeviceHealth Health => _health;

    public Task<BatteryLevel?> ReadBatteryAsync(CancellationToken cancellationToken)
    {
        if (_connection is null || _channel is null || _health == DeviceHealth.Faulted)
        {
            return Task.FromResult<BatteryLevel?>(null);
        }

        // 协议里有等待应答的阻塞读，必须离开调用线程，否则 UI 线程 await 时会冻结面板。
        return Task.Run<BatteryLevel?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _commandGate.Wait(cancellationToken);

            try
            {
                if (!TryQuery(out var reading))
                {
                    // 读不到就是读不到：耳机休眠或 RF 抖一下都属正常。
                    // 保留上次值并标 Stale，由 UI 决定文案，绝不把界面清成「读取失败」。
                    _health = DeviceHealth.Asleep;
                    return _last.Percent is null ? null : BatteryLevel.StaleOf(_last);
                }

                _health = reading.Sleeping ? DeviceHealth.Asleep : DeviceHealth.Online;

                if (reading.Sleeping)
                {
                    // 睡眠时设备不回有效电量，别把 0 当成 0% 显示出去。
                    return _last.Percent is null ? null : BatteryLevel.StaleOf(_last);
                }

                _last = BatteryLevel.Of(reading.Percent, reading.State);
                return _last;
            }
            finally
            {
                _commandGate.Release();
            }
        }, cancellationToken);
    }

    private bool TryQuery(out Reading reading)
    {
        reading = default;

        var frame = new byte[Math.Max(64, _connection!.Info.OutputReportLength)];
        frame[0] = ReportId;
        frame[1] = BatteryCommand;
        frame[2] = BatteryReadArg;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // 先排掉上一次残留的帧，否则可能把旧应答当成这次的。
            _channel!.Drain();

            if (!_connection.TryWriteOutput(frame, out _))
            {
                return false;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(800);
            while (DateTime.UtcNow < deadline)
            {
                var report = _channel.Read(TimeSpan.FromMilliseconds(200));
                if (report is null || report.Length <= StatusIndex)
                {
                    continue;
                }

                // 只认这条命令的应答。设备上可能同时有其它 Input Report 在流。
                if (report[0] != ReportId || report[1] != BatteryCommand)
                {
                    continue;
                }

                var percent = report[PercentIndex];
                var status = report[StatusIndex];

                // 26 = 0x1A，官方实现里用它表示设备进入睡眠。
                if (status == 26)
                {
                    reading = new Reading(0, BatteryState.Unknown, Sleeping: true);
                    return true;
                }

                if (percent > 100)
                {
                    // 超出 0..100 说明这不是一份有效应答，不要拿去显示。
                    continue;
                }

                var state = status switch
                {
                    3 => BatteryState.Charging,
                    4 => BatteryState.Full,
                    _ => BatteryState.Discharging,
                };

                reading = new Reading(percent, state, Sleeping: false);
                return true;
            }
        }

        return false;
    }

    private readonly record struct Reading(int Percent, BatteryState State, bool Sleeping);

    public ValueTask DisposeAsync()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        _commandGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
