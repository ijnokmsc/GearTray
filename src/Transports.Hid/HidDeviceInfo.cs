namespace GearTray.Transport.Hid;

/// <summary>
/// 一个 HID 接口（即一个顶层集合）。同一个物理接收器会暴露多条，
/// 所以要靠 Path 区分，而不是靠 VID/PID。
/// </summary>
public sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort VersionNumber,
    ushort UsagePage,
    ushort Usage,
    string? ProductName,
    string? SerialNumber,
    int InputReportLength,
    int OutputReportLength,
    int FeatureReportLength,
    bool CanWrite,
    string? Manufacturer = null)
{
    public string VidPid => $"VID_{VendorId:X4}&PID_{ProductId:X4}";

    public string DisplayName => string.IsNullOrWhiteSpace(ProductName) ? VidPid : ProductName!;

    /// <summary>usage page 0xFF00+ 是厂商自定义页，私有协议基本都落在这里。</summary>
    public bool IsVendorDefined => UsagePage >= 0xFF00;
}
