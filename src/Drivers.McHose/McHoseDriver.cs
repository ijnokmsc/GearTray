using GearTray.Core;
using GearTray.Transport.Hid;

namespace GearTray.Drivers.McHose;

/// <summary>
/// MCHOSE 专用驱动。
///
/// 匹配靠「VID + PID + UsagePage」三元组，绝不只看 VID —— 同机实测里 3837 下面还挂着
/// V9 Turbo+ 耳机（600A）、MagDock 充电底座（1012）、V9 Turbo（6008），
/// 只看 VID 会让鼠标驱动去接管耳机。
///
/// 只认 0xFF01 这个控制接口：0xFF0B 虽然也有 60 字节的 in/out/feature，
/// 但实测 SetFeature 全部返回 ERROR_INVALID_FUNCTION(1)，那不是一个可下发的通道。
/// </summary>
public sealed class McHoseDriver : IDeviceDriver
{
    private const ushort VendorId = 0x3837;

    /// <summary>已实测可用的接口。加新设备就往这张表里加一行，不要改判定逻辑。</summary>
    private static readonly HashSet<(ushort ProductId, ushort UsagePage)> Verified =
    [
        (0x100B, 0xFF01), // A7 V2 Ultra+
    ];

    public DriverManifest Manifest { get; } = new(
        Id: "mchose.classic-feature",
        DisplayName: "MCHOSE 经典 Feature 协议",
        Version: new Version(0, 1, 0),
        Kind: DriverKind.Native,
        Description: "A7 / A7 V2 Ultra / Pro 系列：Feature Report 0x11，载荷 XOR 0xFF");

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

        // 同族但未验证：只按控制接口特征给低分，保证不会比已验证条目更优先，
        // 但仍高于通用驱动的 10 分 —— 否则同族新鼠标永远读不到私有电量。
        return identity.UsagePage == 0xFF01 ? 60 : 0;
    }

    public Task<IDeviceSession> OpenAsync(DeviceIdentity identity, CancellationToken cancellationToken)
    {
        return Task.FromResult<IDeviceSession>(new McHoseSession(identity));
    }
}

internal sealed class McHoseSession : IDeviceSession, IDpiControl
{
    private static readonly HashSet<DeviceCapability> SupportedCapabilities =
        [DeviceCapability.Battery, DeviceCapability.DpiProfile];

    private readonly HidConnection? _connection;
    private DeviceHealth _health = DeviceHealth.Online;
    private BatteryLevel _last = BatteryLevel.Unknown;

    /// <summary>
    /// 命令级互斥：2.4G dongle 一次只能处理一条命令，两个「发-等-读」序列交错会互相串包
    /// （读电量时插进一次配置读取，两边都拿到对方的部分应答）。
    /// 所以粒度必须是「整条命令」，不能只靠 HidConnection 内部的单次 IO 锁。
    /// </summary>
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    public McHoseSession(DeviceIdentity identity)
    {
        Identity = identity;

        try
        {
            _connection = HidConnection.Open(identity.DevicePath);
        }
        catch (Exception)
        {
            _connection = null;
        }

        if (_connection is null || !_connection.CanWrite)
        {
            // 能读 Feature 但写不了，就没法发查询命令；这种会话不该显示成可用。
            _health = DeviceHealth.Faulted;
        }
    }

    public DeviceIdentity Identity { get; }

    public IReadOnlySet<DeviceCapability> Capabilities =>
        _health == DeviceHealth.Faulted ? new HashSet<DeviceCapability>() : SupportedCapabilities;

    public DeviceHealth Health => _health;

    public Task<BatteryLevel?> ReadBatteryAsync(CancellationToken cancellationToken)
    {
        if (_connection is null || _health == DeviceHealth.Faulted)
        {
            return Task.FromResult<BatteryLevel?>(null);
        }

        // 协议里有 Thread.Sleep（等 RF 周期），必须离开调用线程，
        // 否则宿主在 UI 线程 await 时会冻结面板 —— 这是设计里的硬约束。
        return Task.Run<BatteryLevel?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            _commandGate.Wait(cancellationToken);
            try
            {
                if (!McHoseProtocol.TryRead(_connection, attempts: 5, out var reading, out _))
                {
                    // 读不到就是读不到：无线鼠标休眠时属正常。保留上次值并标 Stale，
                    // 由 UI 决定文案，绝不把界面清成「读取失败」。
                    _health = DeviceHealth.Asleep;
                    return _last.Percent is null ? null : BatteryLevel.StaleOf(_last);
                }

                _health = DeviceHealth.Online;
                _last = BatteryLevel.Of(
                    reading.Percent,
                    reading.Charging ? BatteryState.Charging : BatteryState.Discharging);

                return _last;
            }
            finally
            {
                _commandGate.Release();
            }
        }, cancellationToken);
    }

    public Task<DpiProfile?> ReadDpiAsync(CancellationToken cancellationToken)
    {
        if (_connection is null || _health == DeviceHealth.Faulted)
        {
            return Task.FromResult<DpiProfile?>(null);
        }

        return Task.Run<DpiProfile?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _commandGate.Wait(cancellationToken);
            try
            {
                if (!McHoseProtocol.TryReadSettings(_connection, attempts: 5, out var settings, out _) || settings is null)
                {
                    return null;
                }

                _health = DeviceHealth.Online;
                return ToProfile(settings);
            }
            finally
            {
                _commandGate.Release();
            }
        }, cancellationToken);
    }

    public async Task<bool> SetActiveStageAsync(int stageIndex, CancellationToken cancellationToken)
    {
        var current = await ReadDpiAsync(cancellationToken).ConfigureAwait(false);
        if (current is null || stageIndex < 0 || stageIndex >= current.Stages.Count)
        {
            return false;
        }

        return await ApplyDpiAsync(current.Stages.ToArray(), stageIndex, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetStageValueAsync(int stageIndex, int dpi, CancellationToken cancellationToken)
    {
        if (dpi is < 50 or > 42000)
        {
            return false;
        }

        var current = await ReadDpiAsync(cancellationToken).ConfigureAwait(false);
        if (current is null || stageIndex < 0 || stageIndex >= current.Stages.Count)
        {
            return false;
        }

        var stages = current.Stages.ToArray();
        stages[stageIndex] = dpi;

        // 改的是当前生效档位时，生效索引保持不变（仍是同一档，只是值变了）。
        return await ApplyDpiAsync(stages, current.ActiveIndex, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读-改-写-读回确认。写板载 flash 后固件需要时间落盘，立刻读会拿到旧值。
    /// </summary>
    private Task<bool> ApplyDpiAsync(int[] stages, int activeIndex, CancellationToken cancellationToken)
    {
        if (_connection is null || _health == DeviceHealth.Faulted)
        {
            return Task.FromResult(false);
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _commandGate.Wait(cancellationToken);
            try
            {
                // 必须先读一次拿硬件档位数：设备可能只启用 4 档，硬发 6 档会把配置写坏。
                if (!McHoseProtocol.TryReadSettings(_connection, attempts: 5, out var before, out _) || before is null)
                {
                    return false;
                }

                var payload = McHoseProtocol.BuildDpiPayload(stages, activeIndex, before.StageCount);
                if (!McHoseProtocol.TryWriteDpi(_connection, payload, out _))
                {
                    return false;
                }

                Thread.Sleep(300);

                if (!McHoseProtocol.TryReadSettings(_connection, attempts: 5, out var after, out _) || after is null)
                {
                    return false;
                }

                return after.ActiveStageIndex == activeIndex
                       && after.DpiStages.Take(after.StageCount).SequenceEqual(stages.Take(after.StageCount));
            }
            finally
            {
                _commandGate.Release();
            }
        }, cancellationToken);
    }

    private static DpiProfile ToProfile(McHoseSettings settings) =>
        new(settings.DpiStages.Take(settings.StageCount).ToArray(), settings.ActiveStageIndex);

    public ValueTask DisposeAsync()
    {
        _connection?.Dispose();
        _commandGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
