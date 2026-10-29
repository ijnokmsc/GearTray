using GearTray.Core;
using GearTray.Transport.Hid;

namespace GearTray.Drivers.Generic;

/// <summary>
/// 兜底驱动：任何 HID 设备都接得住，只提供标准电量字段的读取。
/// 匹配分最低（10），任何时候都会被专用驱动抢走。
/// </summary>
public sealed class GenericHidDriver : IDeviceDriver
{
    public DriverManifest Manifest { get; } = new(
        Id: "generic.hid",
        DisplayName: "通用 HID 驱动",
        Version: new Version(0, 1, 0),
        Kind: DriverKind.Generic,
        Description: "标准 HID 电量字段（Generic Device Controls 0x0006 / Battery Strength 0x0020）");

    public int MatchScore(DeviceIdentity identity)
    {
        // 只接 usage page < 0xFF00 的标准接口。
        // 厂商自定义页（0xFF00+）留给专用驱动，避免兜底驱动乱发指令。
        return identity.UsagePage < 0xFF00 ? 10 : 0;
    }

    public Task<IDeviceSession> OpenAsync(DeviceIdentity identity, CancellationToken cancellationToken)
    {
        IDeviceSession session = new GenericHidSession(identity);
        return Task.FromResult(session);
    }
}

internal sealed class GenericHidSession : IDeviceSession
{
    private readonly HidConnection? _connection;
    private readonly HashSet<DeviceCapability> _capabilities = new();
    private DeviceHealth _health = DeviceHealth.Online;

    public GenericHidSession(DeviceIdentity identity)
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

        if (_connection is null)
        {
            _health = DeviceHealth.Faulted;
            return;
        }

        if (_connection.Info.FeatureReportLength >= 2)
        {
            _capabilities.Add(DeviceCapability.Battery);
        }
    }

    public DeviceIdentity Identity { get; }

    public IReadOnlySet<DeviceCapability> Capabilities => _capabilities;

    public DeviceHealth Health => _health;

    public Task<BatteryLevel?> ReadBatteryAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            _health = DeviceHealth.Faulted;
            return Task.FromResult<BatteryLevel?>(null);
        }

        try
        {
            if (_connection.TryReadStandardBattery(out var percent))
            {
                _health = DeviceHealth.Online;
                return Task.FromResult<BatteryLevel?>(BatteryLevel.Of(percent));
            }

            // 读不到不是错误：多数 2.4G 外设不实现标准电量字段，
            // 它们的私有协议在 M3 的专用驱动里处理。
            _health = DeviceHealth.Online;
            return Task.FromResult<BatteryLevel?>(null);
        }
        catch (Exception)
        {
            _health = DeviceHealth.Faulted;
            return Task.FromResult<BatteryLevel?>(null);
        }
    }

    public ValueTask DisposeAsync()
    {
        _connection?.Dispose();
        return ValueTask.CompletedTask;
    }
}
