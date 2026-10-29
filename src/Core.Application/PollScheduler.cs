namespace GearTray.Runtime;

/// <summary>
/// 轮询调度：单设备串行、全局不重叠、可随时手动刷新。
/// 用 Task.Delay 而不是 PeriodicTimer —— 后者间隔固定，改不了配置。
/// </summary>
public sealed class PollScheduler : IAsyncDisposable
{
    private readonly DeviceRegistry _registry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PollScheduler(DeviceRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>空闲刷新间隔，默认 60 秒。</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);

    public event Action<Exception>? Error;

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    public async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        await TickAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>单独刷新控制量（DPI 档位）。面板打开或用户改完设置后调用。</summary>
    public async Task RefreshControlsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _registry.ReadControlsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await TickAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出路径。
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // 上一轮还没跑完，直接跳过，避免堆积。
            return;
        }

        try
        {
            await _registry.RefreshAsync(cancellationToken).ConfigureAwait(false);
            await _registry.PollAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts?.Dispose();
        _gate.Dispose();
    }
}
