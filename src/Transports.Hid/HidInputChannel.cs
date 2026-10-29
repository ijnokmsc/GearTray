using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace GearTray.Transport.Hid;

/// <summary>
/// 监听通道：独立句柄 + FILE_FLAG_OVERLAPPED + 事件等待。
///
/// 实测教训（M3，A7 V2 Ultra+）：用**阻塞** ReadFile 挂在 0xFF0B 上，再向同一路径的
/// 另一个句柄 WriteFile，进程会被静默杀死——没有异常栈、没有托管异常，就是直接没了。
/// 所以这里必须是 overlapped 读：IRP 走异步队列，不会占住驱动同步路径。
///
/// 关闭路径：_stopping → 事件等待超时返回 → CancelIoEx 取消这条 IRP → GetOverlappedResult
/// 收尾 → 线程退出。顺序反了会留下悬空 IRP，CloseHandle 时同样崩。
/// </summary>
public sealed class HidInputChannel : IDisposable
{
    private readonly IntPtr _handle;
    private readonly IntPtr _ioEvent;
    private readonly IntPtr _overlapped;
    private readonly int _reportLength;
    private readonly Thread _pump;
    private readonly BlockingCollection<byte[]> _queue;
    private volatile bool _stopping;

    private HidInputChannel(IntPtr handle, int reportLength)
    {
        _handle = handle;
        _reportLength = reportLength;
        _ioEvent = NativeMethods.CreateEventW(IntPtr.Zero, manualReset: true, initialState: false, IntPtr.Zero);
        _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.OVERLAPPED>());
        Marshal.StructureToPtr(new NativeMethods.OVERLAPPED { hEvent = _ioEvent }, _overlapped, false);

        // 有界队列：1000Hz 设备会灌满，超出的旧报文直接丢弃。
        // 电量是状态不是事件，丢中间帧无所谓，要的是「最新一帧」。
        _queue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 256);

        _pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = $"hid-input:{reportLength}",
        };
        _pump.Start();
    }

    public static HidInputChannel? Open(string path, int reportLength)
    {
        if (reportLength < 1)
        {
            return null;
        }

        var handle = NativeMethods.CreateFileW(
            path,
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (handle == NativeMethods.INVALID_HANDLE_VALUE)
        {
            return null;
        }

        try
        {
            return new HidInputChannel(handle, reportLength);
        }
        catch
        {
            NativeMethods.CloseHandle(handle);
            throw;
        }
    }

    /// <summary>等下一帧，超时返回 null。用于「发命令 → 等应答」的配对读取。</summary>
    public byte[]? Read(TimeSpan timeout)
    {
        try
        {
            return _queue.TryTake(out var report, timeout) ? report : null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>把队列里积压的报文全部丢掉，避免上一条命令的应答污染下一次配对。</summary>
    public void Drain()
    {
        while (_queue.TryTake(out _))
        {
        }
    }

    private void Pump()
    {
        var buffer = new byte[Math.Max(_reportLength, 1)];

        while (!_stopping)
        {
            var overlapped = Marshal.PtrToStructure<NativeMethods.OVERLAPPED>(_overlapped);
            overlapped.hEvent = _ioEvent;
            Marshal.StructureToPtr(overlapped, _overlapped, false);
            NativeMethods.ResetEvent(_ioEvent);

            if (!NativeMethods.ReadFile(_handle, buffer, buffer.Length, out _, _overlapped))
            {
                var error = Marshal.GetLastWin32Error();
                const int errorIoPending = 997;

                if (error == NativeMethods.ERROR_OPERATION_ABORTED)
                {
                    return;
                }

                if (error != errorIoPending)
                {
                    // 设备休眠 / 接收器瞬断：退避重试，绝不在这里抛异常打死宿主。
                    Thread.Sleep(50);
                    continue;
                }
            }

            // 200ms 一跳：既是空闲节流，也是响应关闭请求的最坏延迟。
            var wait = NativeMethods.WaitForSingleObject(_ioEvent, 200);

            if (wait == NativeMethods.WAIT_TIMEOUT || _stopping)
            {
                NativeMethods.CancelIoEx(_handle, _overlapped);

                // 必须收尸：取消后的 IRP 仍要 GetOverlappedResult 归还，否则句柄关闭时崩溃。
                NativeMethods.GetOverlappedResult(_handle, _overlapped, out _, wait: true);
                continue;
            }

            if (!NativeMethods.GetOverlappedResult(_handle, _overlapped, out var read, wait: false) || read <= 0)
            {
                continue;
            }

            _queue.TryAdd(buffer[..read]);
        }
    }

    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;

        _pump.Join(TimeSpan.FromSeconds(2));

        NativeMethods.CloseHandle(_handle);
        NativeMethods.CloseHandle(_ioEvent);
        Marshal.FreeHGlobal(_overlapped);
        _queue.Dispose();
    }
}
