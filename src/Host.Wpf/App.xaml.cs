using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using GearTray.Runtime;
using GearTray.Core;
using GearTray.Drivers.Generic;
using GearTray.Drivers.McHose;
using GearTray.Drivers.Vgn;
using GearTray.Host.Input;
using GearTray.Host.Panel;
using GearTray.Host.Tray;

namespace GearTray.Host;

public partial class App : Application
{
    private const string MutexName = @"Local\GearTray.SingleInstance";

    private Mutex? _mutex;
    private TrayHost? _tray;
    private PanelWindow? _panel;
    private DeviceRegistry? _registry;
    private PollScheduler? _scheduler;

    /// <summary>最新一份快照 + 「已排队」标志，用来把密集的刷新合并成一次重绘。</summary>
    private IReadOnlyList<DeviceSnapshot>? _pendingSnapshots;
    private int _applyQueued;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 诊断入口：WPF 是 WinExe，没有控制台，得自己开一个才能看到结果。
        // 放在单实例检查之前，避免「已有一个托盘在跑」导致诊断直接退出。
        if (e.Args.Contains("--diagnose-winkey", StringComparer.OrdinalIgnoreCase))
        {
            ConsoleHost.Attach();

            var logPath = Path.Combine(Path.GetTempPath(), "batteryhub-winkey.log");
            using var logFile = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            var tee = new TeeWriter(Console.Out, logFile);
            Console.SetOut(tee);

            WinKeyLockDiagnostics.Run();

            Console.Out.Flush();
            Console.WriteLine($"log: {logPath}");
            Shutdown();
            return;
        }

        // 诊断入口：不开托盘、不占单实例，跑一次完整轮询后把快照打到控制台。
        // 用来验证「驱动竞标 → 会话 → 电量」整条链路，不必盯着托盘图标猜。
        if (e.Args.Contains("--snapshot", StringComparer.OrdinalIgnoreCase))
        {
            ConsoleHost.Attach();
            RunSnapshot().GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        // 诊断入口：走完整链路写一个设置项，验证「竞标 → 会话 → 设置 → 协议」。
        var setIndex = Array.IndexOf(e.Args, "--set");
        if (setIndex >= 0 && setIndex + 1 < e.Args.Length)
        {
            ConsoleHost.Attach();
            RunSet(e.Args[setIndex + 1]).GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // 已在运行：不重复启动。M1 只做静默退出，M2 再加「激活已有实例」。
            Shutdown();
            return;
        }

        _registry = new DeviceRegistry(CreateDrivers());

        _registry.SnapshotsChanged += OnSnapshotsChanged;

        _tray = new TrayHost();
        _tray.ShowPanelRequested += ShowPanel;
        _tray.RefreshRequested += OnRefreshRequested;
        _tray.ExitRequested += OnExitRequested;
        _tray.Install();

        _scheduler = new PollScheduler(_registry);
        _scheduler.Start();

        // 预热一次：否则托盘图标要等一个轮询周期才有内容。
        _ = _scheduler.RefreshNowAsync();

        // 开发期入口：跳过托盘直接开面板，改 UI 时不必每次都去点托盘图标。
        if (e.Args.Contains("--panel", StringComparer.OrdinalIgnoreCase))
        {
            ShowPanel();
        }

        // 开发期入口：开面板 → 等首轮数据到位 → 自渲染 PNG → 退出。
        // 截图工具要么被组策略挡（Add-Type）、要么要抓屏幕，这条路径最省事。
        var shotIndex = Array.IndexOf(e.Args, "--shot");
        if (shotIndex >= 0 && shotIndex + 1 < e.Args.Length)
        {
            var path = e.Args[shotIndex + 1];
            ShowPanel();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _panel?.SaveSnapshot(path);
                Shutdown();
            };
            timer.Start();
        }
    }

    private static IDeviceDriver[] CreateDrivers() =>
    [
        // 顺序无关：竞标按分数，专用驱动 100 分必然压过通用驱动的 10 分。
        new McHoseDriver(),
        new McHoseHeadsetDriver(),
        new VgnKeyboardDriver(),
        new GenericHidDriver(),
    ];

    private static async Task RunSnapshot()
    {
        await using var registry = new DeviceRegistry(CreateDrivers());
        await registry.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        await registry.PollAsync(CancellationToken.None).ConfigureAwait(false);
        await registry.ReadControlsAsync(CancellationToken.None).ConfigureAwait(false);

        foreach (var snapshot in registry.Current)
        {
            Console.WriteLine($"{snapshot.Identity.DisplayName}  {snapshot.Identity.VidPid}  page=0x{snapshot.Identity.UsagePage:X4}");
            Console.WriteLine($"  health : {snapshot.Health}");
            Console.WriteLine($"  battery: {snapshot.Battery.Display}  stale={snapshot.Battery.IsStale}  state={snapshot.Battery.State}");
            Console.WriteLine($"  caps   : {string.Join(", ", snapshot.Capabilities)}");

            if (snapshot.WinKeyLock is { } winLock)
            {
                Console.WriteLine($"  winlock: {winLock.Mode} locked={winLock.IsLocked} available={winLock.IsAvailable} err={winLock.LastError ?? "-"}");
            }

            if (snapshot.Settings is { Count: > 0 } settings)
            {
                Console.WriteLine($"  settings: {settings.Count} 项");
                foreach (var setting in settings)
                {
                    Console.WriteLine($"    {setting.Key,-22} = {setting.Value,-6} {setting.DisplayValue}");
                }
            }
        }

        Console.WriteLine($"total: {registry.Current.Count}");
    }

    /// <summary>
    /// 走完整链路写一个设置项：Registry 竞标 → 会话 → IDeviceSettings → 协议。
    /// 面板上的点击最终就是这条路，所以这条命令能验证 UI 之外的全部环节。
    /// </summary>
    private static async Task RunSet(string assignment)
    {
        var parts = assignment.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            Console.WriteLine("usage: --set <key>=<value>");
            return;
        }

        var key = parts[0];
        var value = parts[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(parts[1][2..], 16)
            : int.Parse(parts[1]);

        await using var registry = new DeviceRegistry(CreateDrivers());
        await registry.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        await registry.PollAsync(CancellationToken.None).ConfigureAwait(false);

        var target = registry.Current.FirstOrDefault(
            s => s.Settings?.Any(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)) == true);

        if (target is null)
        {
            Console.WriteLine($"no device exposes setting '{key}'");
            return;
        }

        Console.WriteLine($"{target.Identity.DisplayName}: set {key}={value}");
        var ok = await registry.SetSettingAsync(target.Identity, key, value, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"  result: {(ok ? "accepted (read back confirmed)" : "rejected (device rolled back)")}");

        var after = registry.Current
            .First(s => s.Identity == target.Identity)
            .Settings?
            .FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine(after is null
            ? "  after : <setting missing>"
            : $"  after : {after.Key} = {after.Value} ({after.DisplayValue})");
    }

    private void OnSnapshotsChanged(IReadOnlyList<DeviceSnapshot> snapshots)
    {
        // 事件来自后台线程，必须切回 UI 线程再碰 WPF 对象。
        Volatile.Write(ref _pendingSnapshots, snapshots);

        // 合并排队：轮询和手动刷新可能同时到达，排多个 Apply 只会重复拆建控件，
        // 也更容易在用户正操作控件时把可视树拆掉。永远只应用最新的一份。
        if (Interlocked.Exchange(ref _applyQueued, 1) == 1)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _applyQueued, 0);

            if (Volatile.Read(ref _pendingSnapshots) is not { } latest)
            {
                return;
            }

            _tray?.Apply(latest);

            // 面板没开就完全不画：一张关着的窗口不需要跟着刷新。
            if (_panel is { IsVisible: true } panel)
            {
                panel.Apply(latest);
            }
        });
    }

    private void ShowPanel()
    {
        if (_panel is null)
        {
            _panel = new PanelWindow(RefreshFromPanel, SetDpiStageAsync, SetWinKeyLockAsync, SetSettingAsync);
            _panel.Closed += (_, _) => _panel = null;
        }

        if (_panel.IsVisible)
        {
            _panel.Activate();
            return;
        }

        _panel.Apply(_registry?.Current ?? Array.Empty<DeviceSnapshot>());
        _panel.Show();
        _panel.Activate();

        // DPI 不进常驻轮询：面板打开时才读一次，避免每 60 秒白烧一次 65 字节 Feature 往返。
        RefreshControls();
    }

    private void RefreshControls()
    {
        var scheduler = _scheduler;
        if (scheduler is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await scheduler.RefreshControlsAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 读不到 DPI 不影响电量显示。
            }
        });
    }

    private async Task<bool> SetDpiStageAsync(DeviceIdentity identity, int stageIndex)
    {
        var registry = _registry;
        if (registry is null)
        {
            return false;
        }

        return await registry.SetActiveStageAsync(identity, stageIndex, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task<bool> SetWinKeyLockAsync(DeviceIdentity identity, bool locked)
    {
        var registry = _registry;
        if (registry is null)
        {
            return false;
        }

        return await registry.SetWinKeyLockAsync(identity, locked, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task<bool> SetSettingAsync(DeviceIdentity identity, string key, int value)
    {
        var registry = _registry;
        if (registry is null)
        {
            return false;
        }

        return await registry.SetSettingAsync(identity, key, value, CancellationToken.None).ConfigureAwait(true);
    }

    private void RefreshFromPanel()    {
        var scheduler = _scheduler;
        if (scheduler is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await scheduler.RefreshNowAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 刷新失败不应打断 UI。
            }
        });
    }

    private void OnRefreshRequested() => RefreshFromPanel();

    private void OnExitRequested() => Shutdown();

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出路径必须完整：图标 → 托盘窗口 → 会话 → Mutex。
        // 少一步就是僵尸进程 + 之后再也起不来。
        _tray?.Dispose();
        _panel?.Close();

        if (_scheduler is not null)
        {
            _scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (_registry is not null)
        {
            _registry.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _mutex.Dispose();
        }

        base.OnExit(e);
    }
}
