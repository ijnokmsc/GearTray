using GearTray.Core;
using GearTray.Transport.Hid;

namespace GearTray.Runtime;

/// <summary>
/// 设备注册表：枚举 → 驱动竞标 → 建会话 → 轮询 → 推快照。
/// UI 只拿不可变快照，永远不直接持有会话。
/// </summary>
public sealed class DeviceRegistry : IAsyncDisposable
{
    private sealed class Entry
    {
        public required DeviceIdentity Identity { get; init; }
        public required IDeviceDriver Driver { get; init; }
        public IDeviceSession? Session { get; set; }
        public BatteryLevel Battery { get; set; } = BatteryLevel.Unknown;
        public DpiProfile? Dpi { get; set; }
        public DeviceHealth Health { get; set; } = DeviceHealth.Online;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    }

    private readonly List<IDeviceDriver> _drivers;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public DeviceRegistry(IEnumerable<IDeviceDriver> drivers)
    {
        // 版本降序：同分时高版本优先，保证归属确定。
        _drivers = drivers.OrderByDescending(d => d.Manifest.Version).ToList();
    }

    public event Action<IReadOnlyList<DeviceSnapshot>>? SnapshotsChanged;

    public IReadOnlyList<DeviceSnapshot> Current { get; private set; } = Array.Empty<DeviceSnapshot>();

    /// <summary>重新枚举接口，为新增设备建会话，销毁消失设备的会话。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<HidDeviceInfo> interfaces;
        try
        {
            interfaces = HidEnumerator.Enumerate();
        }
        catch (Exception)
        {
            interfaces = Array.Empty<HidDeviceInfo>();
        }

        // 一个物理设备会暴露多个 HID 接口集合（鼠标 / 键盘 / 厂商页 / 消费者控制），
        // 实测 A7 V2 Ultra+ 一口气 5 个。面板里必须合成一张卡片，否则一只鼠标显示 5 次。
        // 所以按物理身份分组，组内按驱动分数挑接口，同组只建一个会话。
        var candidates = new List<(HidDeviceInfo Info, IDeviceDriver Driver, int Score)>();
        foreach (var info in interfaces)
        {
            var (driver, score) = PickDriver(ToIdentity(info));
            if (driver is not null)
            {
                candidates.Add((info, driver, score));
            }
        }

        var byDevice = candidates
            .GroupBy(c => PhysicalKey(c.Info), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Score).ToList(), StringComparer.OrdinalIgnoreCase);

        // 1) 清掉消失的设备
        List<Entry> removed = new();
        lock (_sync)
        {
            foreach (var key in _entries.Keys.Where(k => !byDevice.ContainsKey(k)).ToList())
            {
                removed.Add(_entries[key]);
                _entries.Remove(key);
            }
        }

        foreach (var entry in removed)
        {
            await DisposeSessionAsync(entry).ConfigureAwait(false);
        }

        // 2) 为新增设备建会话
        foreach (var (key, group) in byDevice)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                if (_entries.ContainsKey(key))
                {
                    continue;
                }
            }

            // 组内按分数从高到低试：最高分的接口打不开（被独占 / 权限不足）时还有退路。
            IDeviceSession? session = null;
            HidDeviceInfo chosen = group[0].Info;
            IDeviceDriver? chosenDriver = null;

            foreach (var candidate in group)
            {
                try
                {
                    session = await candidate.Driver.OpenAsync(ToIdentity(candidate.Info), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    session = null;
                }

                if (session is not null)
                {
                    chosen = candidate.Info;
                    chosenDriver = candidate.Driver;
                    break;
                }
            }

            if (chosenDriver is null)
            {
                continue;
            }

            var entry = new Entry
            {
                Identity = ToIdentity(chosen),
                Driver = chosenDriver,
                Session = session,
                Health = session is null ? DeviceHealth.Faulted : DeviceHealth.Online,
            };

            lock (_sync)
            {
                _entries[key] = entry;
            }
        }

        Publish();
    }

    /// <summary>轮询所有会话。读不到电量不算失败，按 Stale 处理。</summary>
    public async Task PollAsync(CancellationToken cancellationToken)
    {
        List<Entry> snapshot;
        lock (_sync)
        {
            snapshot = _entries.Values.ToList();
        }

        foreach (var entry in snapshot)
        {
            if (entry.Session is null)
            {
                continue;
            }

            try
            {
                var level = await entry.Session.ReadBatteryAsync(cancellationToken).ConfigureAwait(false);

                entry.Battery = level ?? (entry.Battery.Percent is null
                    ? BatteryLevel.Unknown
                    : BatteryLevel.StaleOf(entry.Battery));

                entry.Health = entry.Session.Health;
                entry.UpdatedAt = DateTimeOffset.Now;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                entry.Health = DeviceHealth.Faulted;
                entry.UpdatedAt = DateTimeOffset.Now;
            }
        }

        Publish();
    }

    /// <summary>
    /// 读那些「不便宜、也不常变」的控制量（DPI 档位）。
    ///
    /// 刻意不放进 <see cref="PollAsync"/>：读一次 DPI 要发 65 字节 Feature 帧并等固件应答，
    /// 比读电量贵一个量级，而用户 60 秒内改 DPI 的概率极低。
    /// 由面板打开 / 用户操作后主动触发，避免常驻轮询白烧 dongle 带宽。
    /// </summary>
    public async Task ReadControlsAsync(CancellationToken cancellationToken)
    {
        List<Entry> snapshot;
        lock (_sync)
        {
            snapshot = _entries.Values.ToList();
        }

        foreach (var entry in snapshot)
        {
            // 固件态控制量（Win 锁 / 灯效 / 轮询率 / 休眠）都读自同一块功能信息，
            // 谁实现了 IDeviceSettings 就由它统一刷新，避免同一块被读两遍。
            // 只实现 IWinKeyLockControl 的设备（软件钩子）走老路径。
            if (entry.Session is IDeviceSettings settings && settings.IsAvailable)
            {
                try
                {
                    await settings.RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 读不到就沿用缓存。
                }
            }
            else if (entry.Session is IWinKeyLockControl { IsAvailable: true } lockControl)
            {
                try
                {
                    await lockControl.RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 同上，读不到就沿用缓存。
                }
            }

            if (entry.Session is not IDpiControl dpi)
            {
                continue;
            }

            try
            {
                entry.Dpi = await dpi.ReadDpiAsync(cancellationToken).ConfigureAwait(false) ?? entry.Dpi;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 控制量读失败不影响电量显示，保留旧值。
            }
        }

        Publish();
    }

    /// <summary>切换某台设备的 DPI 档位，成功后立即回读并推送快照。</summary>
    public async Task<bool> SetActiveStageAsync(DeviceIdentity identity, int stageIndex, CancellationToken cancellationToken)
    {
        Entry? entry;
        lock (_sync)
        {
            entry = _entries.Values.FirstOrDefault(e => e.Identity == identity);
        }

        if (entry?.Session is not IDpiControl dpi)
        {
            return false;
        }

        try
        {
            if (!await dpi.SetActiveStageAsync(stageIndex, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            entry.Dpi = await dpi.ReadDpiAsync(cancellationToken).ConfigureAwait(false) ?? entry.Dpi;
            entry.UpdatedAt = DateTimeOffset.Now;
            Publish();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>切换 Win 键锁，成功后重新发布快照（开关状态必须来自设备回读，不能凭点击假定）。</summary>
    public Task<bool> SetWinKeyLockAsync(DeviceIdentity identity, bool locked, CancellationToken cancellationToken)
    {
        Entry? entry;
        lock (_sync)
        {
            entry = _entries.Values.FirstOrDefault(e => e.Identity == identity);
        }

        if (entry?.Session is not IWinKeyLockControl lockControl || !lockControl.IsAvailable)
        {
            return Task.FromResult(false);
        }

        return Task.Run(() =>
        {
            // SetLocked 内部是阻塞的 HID 往返（读整块 → 写两块 → 回读），不能占着调用线程。
            try
            {
                if (!lockControl.SetLocked(locked))
                {
                    return false;
                }

                entry.UpdatedAt = DateTimeOffset.Now;
                Publish();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 写一个固件设置项。写失败时设备侧已回滚，快照里的值也就不会变 ——
    /// 界面必须按回读结果刷新，不能凭点击假定生效。
    /// </summary>
    public Task<bool> SetSettingAsync(DeviceIdentity identity, string key, int value, CancellationToken cancellationToken)
    {
        Entry? entry;
        lock (_sync)
        {
            entry = _entries.Values.FirstOrDefault(e => e.Identity == identity);
        }

        if (entry?.Session is not IDeviceSettings settings || !settings.IsAvailable)
        {
            return Task.FromResult(false);
        }

        return Task.Run(() =>
        {
            // SetSetting 内部是阻塞的 HID 往返（读整块 → 写两块 → 回读），不能占着调用线程。
            try
            {
                if (!settings.SetSetting(key, value))
                {
                    return false;
                }

                entry.UpdatedAt = DateTimeOffset.Now;
                Publish();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }, cancellationToken);
    }

    private (IDeviceDriver? Driver, int Score) PickDriver(DeviceIdentity identity)    {
        IDeviceDriver? best = null;
        var bestScore = 0;

        foreach (var driver in _drivers)
        {
            int score;
            try
            {
                score = driver.MatchScore(identity);
            }
            catch (Exception)
            {
                continue;
            }

            if (score > bestScore)
            {
                best = driver;
                bestScore = score;
            }
        }

        return (best, bestScore);
    }

    /// <summary>
    /// 物理设备身份键。同一只鼠标的 5 个接口集合共享 VID/PID/序列号。
    ///
    /// 已知局限：没有序列号的设备（多数 2.4G dongle）无法区分「同一型号的第二只」。
    /// 要彻底解决得读容器 ID（SetupAPI 的 DEVPKEY_Device_ContainerId），
    /// 那是另一条依赖路径，等真的有人插两只同型号鼠标再说。
    /// </summary>
    private static string PhysicalKey(HidDeviceInfo info) =>
        $"{info.VendorId:X4}:{info.ProductId:X4}:{info.SerialNumber ?? "-"}";

    private void Publish()
    {
        List<DeviceSnapshot> list;
        lock (_sync)
        {
            list = _entries.Values
                .Select(e => new DeviceSnapshot(
                    e.Identity,
                    e.Session?.Capabilities ?? new HashSet<DeviceCapability>(),
                    e.Health,
                    e.Battery,
                    e.Dpi,
                    e.UpdatedAt,
                    e.Session is IWinKeyLockControl lockControl
                        ? new WinKeyLockState(lockControl.IsLocked, lockControl.IsAvailable, lockControl.Mode, lockControl.LastError)
                        : null,
                    e.Session is IDeviceSettings settings ? settings.Settings : null))
                .OrderBy(s => s.Identity.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        Current = list;
        SnapshotsChanged?.Invoke(list);
    }

    private static DeviceIdentity ToIdentity(HidDeviceInfo info) => new(
        info.VendorId,
        info.ProductId,
        info.UsagePage,
        info.Usage,
        info.ProductName,
        info.SerialNumber,
        info.Path);

    private static async Task DisposeSessionAsync(Entry entry)
    {
        if (entry.Session is null)
        {
            return;
        }

        try
        {
            await entry.Session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 销毁失败不应阻断枚举流程。
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Entry> all;
        lock (_sync)
        {
            all = _entries.Values.ToList();
            _entries.Clear();
        }

        foreach (var entry in all)
        {
            await DisposeSessionAsync(entry).ConfigureAwait(false);
        }
    }
}
