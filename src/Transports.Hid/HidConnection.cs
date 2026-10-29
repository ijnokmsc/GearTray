using System.Runtime.InteropServices;

namespace GearTray.Transport.Hid;

/// <summary>
/// 一条设备的控制通道。
///
/// 铁律：同一 HID 句柄上并发「阻塞读 + 写」会静默杀掉进程（连异常栈都没有）。
/// 本类只使用控制句柄做同步的 Feature / Output 短写，所有调用经 <see cref="_gate"/> 串行化；
/// 监听通道将随 MCHOSE 驱动（M3）落地，届时用独立的 FILE_FLAG_OVERLAPPED 句柄，绝不共用。
/// </summary>
public sealed class HidConnection : IDisposable
{
    private readonly IntPtr _control;
    private readonly IntPtr _write;
    private readonly IntPtr _preparsed;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private readonly Lazy<IReadOnlyList<byte>> _outputReportIds;
    private readonly Lazy<IReadOnlyList<byte>> _featureReportIds;
    private readonly Lazy<IReadOnlyList<byte>> _inputReportIds;

    private HidConnection(IntPtr control, IntPtr write, IntPtr preparsed, HidDeviceInfo info)
    {
        _control = control;
        _write = write;
        _preparsed = preparsed;
        Info = info;

        // 惰性求值：描述符解析走的是原生调用，不该在「只是打开一个句柄」时产生副作用。
        // 调试时也靠这条把「打开」和「解析」分成两个可独立复现的步骤。
        _outputReportIds = new Lazy<IReadOnlyList<byte>>(() => HidCaps.GetReportIds(_preparsed, NativeMethods.HidP_Output));
        _featureReportIds = new Lazy<IReadOnlyList<byte>>(() => HidCaps.GetReportIds(_preparsed, NativeMethods.HidP_Feature));
        _inputReportIds = new Lazy<IReadOnlyList<byte>>(() => HidCaps.GetReportIds(_preparsed, NativeMethods.HidP_Input));
    }

    public HidDeviceInfo Info { get; }

    /// <summary>描述符里声明过的 Output Report ID。厂商协议的命令通道就长在这里。</summary>
    public IReadOnlyList<byte> OutputReportIds => _outputReportIds.Value;

    public IReadOnlyList<byte> FeatureReportIds => _featureReportIds.Value;

    public IReadOnlyList<byte> InputReportIds => _inputReportIds.Value;

    /// <summary>描述符里全部 value cap，诊断用（协议探测时靠它看字段布局）。</summary>
    public IReadOnlyList<HidCapInfo> Caps => HidCaps.EnumerateAll(_preparsed);

    public bool CanWrite => _write != NativeMethods.INVALID_HANDLE_VALUE;

    public bool CanReadFeature => _control != NativeMethods.INVALID_HANDLE_VALUE;

    public static HidConnection? Open(string path)
    {
        const uint share = NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE;

        var control = NativeMethods.CreateFileW(
            path, 0, share, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

        if (control == NativeMethods.INVALID_HANDLE_VALUE)
        {
            return null;
        }

        try
        {
            var attributes = new NativeMethods.HIDD_ATTRIBUTES
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.HIDD_ATTRIBUTES>(),
            };

            if (!NativeMethods.HidD_GetAttributes(control, ref attributes))
            {
                NativeMethods.CloseHandle(control);
                return null;
            }

            var caps = HidEnumerator.GetCaps(control);

            var preparsed = IntPtr.Zero;
            if (!NativeMethods.HidD_GetPreparsedData(control, out preparsed))
            {
                preparsed = IntPtr.Zero;
            }

            var write = NativeMethods.CreateFileW(
                path, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE, share,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

            if (write == NativeMethods.INVALID_HANDLE_VALUE)
            {
                write = NativeMethods.CreateFileW(
                    path, NativeMethods.GENERIC_WRITE, share,
                    IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            }

            var info = new HidDeviceInfo(
                Path: path,
                VendorId: attributes.VendorID,
                ProductId: attributes.ProductID,
                VersionNumber: attributes.VersionNumber,
                UsagePage: caps.UsagePage,
                Usage: caps.Usage,
                ProductName: null,
                SerialNumber: null,
                InputReportLength: caps.InputReportByteLength,
                OutputReportLength: caps.OutputReportByteLength,
                FeatureReportLength: caps.FeatureReportByteLength,
                CanWrite: write != NativeMethods.INVALID_HANDLE_VALUE);

            return new HidConnection(control, write, preparsed, info);
        }
        catch
        {
            NativeMethods.CloseHandle(control);
            throw;
        }
    }

    /// <summary>
    /// 标准 HID 电量（Generic Device Controls 0x0006 / Battery Strength 0x0020）。
    /// 走 Feature Report 而不是 Input Report：后者要常驻监听通道，
    /// 而 M1 阶段监听通道还没落地，硬上会引入 1000Hz 报文解析开销。
    /// </summary>
    public bool TryReadStandardBattery(out int percent)
    {
        percent = 0;

        if (_preparsed == IntPtr.Zero || Info.FeatureReportLength < 2)
        {
            return false;
        }

        _gate.Wait();
        try
        {
            var length = Info.FeatureReportLength;

            // Report ID 0~3 逐个试：标准电量字段可能挂在中任意一个 Feature 集合上。
            for (var reportId = 0; reportId <= 3; reportId++)
            {
                var buffer = new byte[length];
                buffer[0] = (byte)reportId;

                if (!NativeMethods.HidD_GetFeature(_control, buffer, length))
                {
                    continue;
                }

                var status = NativeMethods.HidP_GetUsageValue(
                    NativeMethods.HidP_Feature, 0x06, 0, 0x20,
                    out var value, _preparsed, buffer, length);

                if (status != NativeMethods.HIDP_STATUS_SUCCESS)
                {
                    continue;
                }

                if (value is > 0 and <= 100)
                {
                    percent = (int)value;
                    return true;
                }
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>转储指定 Report ID 的 Feature Report，供探测台使用。</summary>
    public byte[]? GetFeature(int reportId) => GetFeature(reportId, Info.FeatureReportLength);

    /// <summary>
    /// 指定长度读 Feature Report。厂商协议里同一接口往往声明多个长度不同的 Feature 报表
    /// （A7 的 0xFF01 接口 FeatureReportByteLength=65，但 0x11 号报表实际只有 20 字节），
    /// 必须按报表自身的长度传，传接口最大值会被设备拒绝。
    /// </summary>
    public byte[]? GetFeature(int reportId, int length)
    {
        if (!CanReadFeature || length < 2)
        {
            return null;
        }

        _gate.Wait();
        try
        {
            var buffer = new byte[length];
            buffer[0] = (byte)reportId;
            return NativeMethods.HidD_GetFeature(_control, buffer, buffer.Length) ? buffer : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TrySetFeature(byte[] report)
    {
        return TrySetFeature(report, out _);
    }

    /// <summary>
    /// 错误码是唯一能区分「长度不对」「权限不对」「设备拒收」的依据，
    /// 探测阶段必须能看到它，否则只能靠猜。
    /// </summary>
    public bool TrySetFeature(byte[] report, out int error)
    {
        error = 0;

        if (!CanWrite || report.Length == 0)
        {
            error = -1;
            return false;
        }

        _gate.Wait();
        try
        {
            if (NativeMethods.HidD_SetFeature(_write, report, report.Length))
            {
                return true;
            }

            error = Marshal.GetLastWin32Error();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 发一帧 Output Report。<paramref name="payload"/> 首字节必须是 Report ID。
    /// 长度会自动补零 / 截断到接口声明值——HID 驱动对长度极其挑剔，差一个字节就
    /// ERROR_INVALID_PARAMETER，而报错信息里不会告诉你是长度问题。
    /// </summary>
    public bool TryWriteOutput(ReadOnlySpan<byte> payload)
    {
        return TryWriteOutput(payload, out _);
    }

    public bool TryWriteOutput(ReadOnlySpan<byte> payload, out int error)
    {
        error = 0;

        if (!CanWrite || payload.Length == 0 || Info.OutputReportLength <= 0)
        {
            error = -1;
            return false;
        }

        var length = Info.OutputReportLength;
        var buffer = new byte[length];
        payload[..Math.Min(payload.Length, length)].CopyTo(buffer);

        _gate.Wait();
        try
        {
            if (NativeMethods.WriteFile(_write, buffer, length, out var written, IntPtr.Zero) && written == length)
            {
                return true;
            }

            error = Marshal.GetLastWin32Error();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 「真写入」验证，替代「CreateFile 带 GENERIC_WRITE 成功就算可写」这个错误判据。
    ///
    /// 只对厂商页（≥ 0xFF00）执行：向 0x0001 键盘集合写输出报文的副作用是**真的敲键**，
    /// 探测工具绝不能为了判一个布尔值而往系统里注入按键。
    ///
    /// 帧里必须用描述符声明过的 Report ID —— 写 0 号 ID 会被驱动直接拒绝，
    /// 那是「ID 不存在」，不是「不可写」。这两者混淆会得出完全错误的结论。
    /// </summary>
    public static WriteProbeResult VerifyWritable(string path, HidDeviceInfo device)
    {
        if (device.UsagePage < 0xFF00)
        {
            return device.UsagePage is 0x0001 or 0x000C
                ? WriteProbeResult.SkippedInputRisk
                : WriteProbeResult.SkippedNoOutputReport;
        }

        if (device.OutputReportLength < 2)
        {
            return WriteProbeResult.SkippedNoOutputReport;
        }

        using var connection = Open(path);
        if (connection is null)
        {
            return WriteProbeResult.OpenFailed;
        }

        return connection.VerifyWritable();
    }

    public WriteProbeResult VerifyWritable()
    {
        if (Info.OutputReportLength < 2)
        {
            return WriteProbeResult.SkippedNoOutputReport;
        }

        if (OutputReportIds.Count == 0)
        {
            return WriteProbeResult.SkippedNoOutputReport;
        }

        var frame = new byte[Info.OutputReportLength];
        frame[0] = OutputReportIds[0];
        return TryWriteOutput(frame) ? WriteProbeResult.Writable : WriteProbeResult.WriteRejected;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_preparsed != IntPtr.Zero)
        {
            NativeMethods.HidD_FreePreparsedData(_preparsed);
        }

        if (_write != NativeMethods.INVALID_HANDLE_VALUE)
        {
            NativeMethods.CloseHandle(_write);
        }

        if (_control != NativeMethods.INVALID_HANDLE_VALUE)
        {
            NativeMethods.CloseHandle(_control);
        }

        _gate.Dispose();
    }
}
