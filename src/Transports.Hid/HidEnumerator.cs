using System.Runtime.InteropServices;
using System.Text;

namespace GearTray.Transport.Hid;

/// <summary>
/// HID 接口枚举。
/// 走 cfgmgr32 的接口列表而不是 SetupAPI 的 SetupDiGetDeviceInterfaceDetail ——
/// 后者在 x64 上 cbSize / 设备路径偏移有坑，前者直接返回 MULTI_SZ，少一层误用空间。
/// </summary>
public static class HidEnumerator
{
    public static IReadOnlyList<HidDeviceInfo> Enumerate()
    {
        var result = new List<HidDeviceInfo>();
        foreach (var path in GetInterfacePaths())
        {
            var info = Describe(path);
            if (info is not null)
            {
                result.Add(info);
            }
        }

        return result;
    }

    public static IReadOnlyList<string> GetInterfacePaths()
    {
        var guid = NativeMethods.GUID_DEVINTERFACE_HID;
        var cr = NativeMethods.CM_Get_Device_Interface_List_SizeW(
            out var length, ref guid, null, NativeMethods.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);

        if (cr != NativeMethods.CR_SUCCESS || length == 0)
        {
            return Array.Empty<string>();
        }

        var buffer = new char[length];
        cr = NativeMethods.CM_Get_Device_Interface_ListW(
            ref guid, null, buffer, length, NativeMethods.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);

        if (cr != NativeMethods.CR_SUCCESS)
        {
            return Array.Empty<string>();
        }

        return new string(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HidDeviceInfo? Describe(string path)
    {
        // dwDesiredAccess = 0：可以读属性、读 Feature Report，但不能发指令。
        // 鼠标/键盘的顶层集合被系统独占，只有这个权限。
        var handle = NativeMethods.CreateFileW(
            path, 0, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle == NativeMethods.INVALID_HANDLE_VALUE)
        {
            return null;
        }

        try
        {
            var attributes = new NativeMethods.HIDD_ATTRIBUTES
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.HIDD_ATTRIBUTES>(),
            };

            if (!NativeMethods.HidD_GetAttributes(handle, ref attributes))
            {
                return null;
            }

            var caps = GetCaps(handle);
            var product = ReadUnicodeString(handle, NativeMethods.HidD_GetProductString);
            var serial = ReadUnicodeString(handle, NativeMethods.HidD_GetSerialNumberString);
            var manufacturer = ReadUnicodeString(handle, NativeMethods.HidD_GetManufacturerString);

            return new HidDeviceInfo(
                Path: path,
                VendorId: attributes.VendorID,
                ProductId: attributes.ProductID,
                VersionNumber: attributes.VersionNumber,
                UsagePage: caps.UsagePage,
                Usage: caps.Usage,
                ProductName: product,
                SerialNumber: serial,
                InputReportLength: caps.InputReportByteLength,
                OutputReportLength: caps.OutputReportByteLength,
                FeatureReportLength: caps.FeatureReportByteLength,
                CanWrite: ProbeWritable(path),
                Manufacturer: manufacturer);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// 试探该接口是否可写。这是「DPI / 键锁能不能写」的前提，
    /// 探测台要把它直接显示出来。
    /// </summary>
    private static bool ProbeWritable(string path)
    {
        const uint share = NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE;

        var handle = NativeMethods.CreateFileW(
            path, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE, share,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle != NativeMethods.INVALID_HANDLE_VALUE)
        {
            NativeMethods.CloseHandle(handle);
            return true;
        }

        handle = NativeMethods.CreateFileW(
            path, NativeMethods.GENERIC_WRITE, share,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle != NativeMethods.INVALID_HANDLE_VALUE)
        {
            NativeMethods.CloseHandle(handle);
            return true;
        }

        return false;
    }

    internal static NativeMethods.HIDP_CAPS GetCaps(IntPtr deviceHandle)
    {
        if (!NativeMethods.HidD_GetPreparsedData(deviceHandle, out var preparsed))
        {
            return NativeMethods.HIDP_CAPS.Create();
        }

        try
        {
            var caps = NativeMethods.HIDP_CAPS.Create();
            return NativeMethods.HidP_GetCaps(preparsed, ref caps) == NativeMethods.HIDP_STATUS_SUCCESS
                ? caps
                : NativeMethods.HIDP_CAPS.Create();
        }
        finally
        {
            NativeMethods.HidD_FreePreparsedData(preparsed);
        }
    }

    private delegate bool StringGetter(IntPtr device, byte[] buffer, int length);

    private static string? ReadUnicodeString(IntPtr handle, StringGetter getter)
    {
        var buffer = new byte[512];
        if (!getter(handle, buffer, buffer.Length))
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
