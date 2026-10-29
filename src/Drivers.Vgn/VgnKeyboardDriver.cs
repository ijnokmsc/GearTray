using GearTray.Core;
using GearTray.Transport.Hid;

namespace GearTray.Drivers.Vgn;

/// <summary>
/// VGN 键盘（weisheng / 微盛方案，VID 0x320F）。
///
/// 匹配靠「VID + UsagePage」：一只键盘在设备管理器里会冒出 7 个 HID 接口，
/// 只有 0xFF1C 那个厂商通道能收发命令，其余标准键盘/消费者接口必须让给通用驱动。
/// </summary>
public sealed class VgnKeyboardDriver : IDeviceDriver
{
    private const ushort VendorId = 0x320F;

    private const ushort VendorUsagePage = 0xFF1C;

    /// <summary>
    /// 已实测过「读电量 + 读写功能信息块」的 PID。
    /// 0x5088 是这批键盘**共用**的 dongle PID（S99 / V98pro / V87 / S87 / VXE75 / N75 PRO 都用它），
    /// 型号靠产品名区分而不靠 PID，所以这里只能按「协议族」而不是「型号」判断。
    /// </summary>
    private static readonly HashSet<ushort> VerifiedProductIds = [0x5088];

    public DriverManifest Manifest { get; } = new(
        Id: "vgn.weisheng-ws",
        DisplayName: "VGN 微盛键盘协议",
        Version: new Version(0, 2, 0),
        Kind: DriverKind.Native,
        Description: "N75 PRO 2.4G 等：Output Report 0x04，命令 26 读电量，功能信息块 48 字节承载灯效/轮询率/休眠/Win 锁");

    public int MatchScore(DeviceIdentity identity)
    {
        if (identity.VendorId != VendorId || identity.UsagePage != VendorUsagePage)
        {
            return 0;
        }

        if (VerifiedProductIds.Contains(identity.ProductId))
        {
            return 100;
        }

        // 同族未验证型号：只读电量大概率能用，但写功能信息会整块覆盖灯效和按键配置，不给写权限。
        return 50;
    }

    public Task<IDeviceSession> OpenAsync(DeviceIdentity identity, CancellationToken cancellationToken)
    {
        var allowWrites = VerifiedProductIds.Contains(identity.ProductId);
        return Task.FromResult<IDeviceSession>(new VgnKeyboardSession(identity, allowWrites));
    }
}

internal sealed class VgnKeyboardSession : IDeviceSession, IWinKeyLockControl, IDeviceSettings
{
    /// <summary>厂商通道（0xFF1C）的输入/输出报文长度，实测 in=64 out=64。</summary>
    private const int VendorInputReportLength = 64;

    private static readonly HashSet<DeviceCapability> SupportedCapabilities =
    [
        DeviceCapability.Battery,
        DeviceCapability.WinKeyLock,
        DeviceCapability.PollingRate,
        DeviceCapability.FirmwareInfo,
    ];

    private readonly HidConnection? _connection;

    private readonly HidInputChannel? _channel;

    private readonly bool _allowWrites;

    private readonly SemaphoreSlim _commandGate = new(1, 1);

    private DeviceHealth _health = DeviceHealth.Online;

    private BatteryLevel _last = BatteryLevel.Unknown;

    /// <summary>功能信息块缓存。写走的是同一块，所以读和写都以它为准。</summary>
    private byte[]? _functionBlock;

    private DateTimeOffset _blockSampledAt = DateTimeOffset.MinValue;

    private string? _lastError;

    public VgnKeyboardSession(DeviceIdentity identity, bool allowWrites)
    {
        Identity = identity;
        _allowWrites = allowWrites;

        try
        {
            _connection = HidConnection.Open(identity.DevicePath);
            _channel = _connection?.CanWrite == true
                ? HidInputChannel.Open(identity.DevicePath, VendorInputReportLength)
                : null;
        }
        catch (Exception)
        {
            _connection = null;
            _channel = null;
        }

        if (_connection is null || _channel is null)
        {
            _health = DeviceHealth.Faulted;
            _lastError = "vendor channel unavailable";
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

        // 协议里有阻塞等待（等 RF 往返），必须离开调用线程，否则面板会卡在 UI 线程上。
        return Task.Run<BatteryLevel?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            _commandGate.Wait(cancellationToken);
            try
            {
                if (!VgnWsProtocol.TryReadBattery(_connection, _channel, attempts: 2, out var percent, out var charging))
                {
                    // 键盘休眠时接收器不回包，属正常现象：保留上次值并标 Stale。
                    _health = DeviceHealth.Asleep;
                    return _last.Percent is null ? null : BatteryLevel.StaleOf(_last);
                }

                _health = DeviceHealth.Online;
                _last = BatteryLevel.Of(
                    percent,
                    charging ? BatteryState.Charging : percent >= 100 ? BatteryState.Full : BatteryState.Discharging);
                _lastError = null;

                RefreshBlockCore(force: false);
                return _last;
            }
            catch (Exception ex)
            {
                _health = DeviceHealth.Faulted;
                _lastError = ex.Message;
                return _last.Percent is null ? null : BatteryLevel.StaleOf(_last);
            }
            finally
            {
                _commandGate.Release();
            }
        }, cancellationToken);
    }

    public WinKeyLockMode Mode => WinKeyLockMode.Firmware;

    public bool IsLocked => _functionBlock is { } block && block[VgnWsProtocol.WinKeyLockIndex] != 0;

    public bool IsAvailable => _allowWrites && _health != DeviceHealth.Faulted;

    public string? LastError => _lastError;

    public IReadOnlyList<DeviceSetting> Settings =>
        _functionBlock is { } block
            ? VgnWsSettings.Specs.Select(spec => spec.ToSetting(block)).ToArray()
            : [];

    bool IDeviceSettings.IsAvailable => _allowWrites && _health != DeviceHealth.Faulted;

    /// <summary>用户在键盘上按 FN+Win / FN+组合键也能改这些项，所以界面打开时要能主动拉一次。</summary>
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!_allowWrites || _connection is null || _channel is null)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            _commandGate.Wait(cancellationToken);
            try
            {
                RefreshBlockCore(force: true);
            }
            finally
            {
                _commandGate.Release();
            }
        }, cancellationToken);
    }

    public bool SetLocked(bool locked) => SetSetting("winlock", locked ? 1 : 0);

    public bool SetSetting(string key, int value)
    {
        if (!IsAvailable || _connection is null || _channel is null)
        {
            return false;
        }

        // Win 锁不在字段表里（它有自己的强类型接口），单独映射一下，走同一条写入流程。
        var spec = VgnWsSettings.Find(key);

        if (spec is null)
        {
            _lastError = $"unknown setting '{key}'";
            return false;
        }

        _commandGate.Wait();
        try
        {
            // 设备没有「只改一个字段」的命令：读整块、改一位、整块写回，否则灯效和按键配置会被一起清掉。
            if (!VgnWsProtocol.TryMutateFunctionInformation(
                    _connection,
                    _channel,
                    block => spec.Write(block, value),
                    confirm => spec.MatchesReadBack(spec.Read(confirm), value),
                    out var updated,
                    out var error))
            {
                _lastError = $"{key}: {error}";
                return false;
            }

            _functionBlock = updated;
            _blockSampledAt = DateTimeOffset.Now;
            _lastError = null;
            return true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            return false;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private void RefreshBlockCore(bool force)
    {
        if (_connection is null || _channel is null)
        {
            return;
        }

        // 每次轮询都读两块功能信息太浪费，5 秒一次足够跟手。
        if (!force && _functionBlock is not null && DateTimeOffset.Now - _blockSampledAt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        if (!VgnWsProtocol.TryReadFunctionInformation(_connection, _channel, out var block))
        {
            return;
        }

        _functionBlock = block;
        _blockSampledAt = DateTimeOffset.Now;
    }

    public async ValueTask DisposeAsync()
    {
        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _channel?.Dispose();
            _connection?.Dispose();
        }
        finally
        {
            _commandGate.Release();
            _commandGate.Dispose();
        }
    }
}
