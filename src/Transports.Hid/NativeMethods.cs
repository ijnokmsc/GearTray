using System.Runtime.InteropServices;

namespace GearTray.Transport.Hid;

/// <summary>
/// 纯 Win32 互操作。刻意不用 HidSharp 之类的第三方库：
/// 依赖为零、行为可控，且能精确控制「哪条通道用哪个句柄」。
/// </summary>
internal static class NativeMethods
{
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    /// <summary>ERROR_OPERATION_ABORTED —— CancelIoEx 打断阻塞读后的返回值，是正常关闭路径。</summary>
    internal const int ERROR_OPERATION_ABORTED = 995;

    internal const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0x00000000;
    internal const int CR_SUCCESS = 0;

    /// <summary>HIDP_STATUS_SUCCESS —— 注意 NTSTATUS 不是 0。</summary>
    internal const int HIDP_STATUS_SUCCESS = 0x00110000;

    internal const int HidP_Input = 0;
    internal const int HidP_Output = 1;
    internal const int HidP_Feature = 2;

    internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    /// <summary>{4D1E55B2-F16F-11CF-88CB-001111000030}</summary>
    internal static readonly Guid GUID_DEVINTERFACE_HID = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;

        internal static HIDP_CAPS Create() => new() { Reserved = new ushort[17] };
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CM_Get_Device_Interface_List_SizeW(
        out uint pulLen, ref Guid interfaceClassGuid, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CM_Get_Device_Interface_ListW(
        ref Guid interfaceClassGuid, string? deviceId, [Out] char[] buffer, uint bufferLen, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetAttributes(IntPtr device, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", ExactSpelling = true)]
    internal static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetProductString(IntPtr device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetSerialNumberString(IntPtr device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetFeature(IntPtr device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_SetFeature(IntPtr device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetManufacturerString(IntPtr device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_SetOutputReport(IntPtr device, byte[] buffer, int bufferLength);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteFile(
        IntPtr hFile, byte[] buffer, int bytesToWrite, out int bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadFile(
        IntPtr hFile, byte[] buffer, int bytesToRead, out int bytesRead, IntPtr overlapped);

    /// <summary>
    /// 打断挂起的 IO。第二个参数传 IntPtr.Zero 表示取消该句柄上本进程所有未完成请求——
    /// 这是关闭阻塞读线程的唯一正确方式，直接 CloseHandle 会让读线程永久卡在内核里。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CancelIoEx(IntPtr hFile, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern IntPtr CreateEventW(
        IntPtr eventAttributes, bool manualReset, bool initialState, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ResetEvent(IntPtr hEvent);

    internal const uint WAIT_OBJECT_0 = 0;
    internal const uint WAIT_TIMEOUT = 258;

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetOverlappedResult(
        IntPtr hFile, IntPtr overlapped, out int bytesTransferred, bool wait);

    /// <summary>OVERLAPPED，x64 下 32 字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    [DllImport("hid.dll", ExactSpelling = true)]
    internal static extern int HidP_GetUsageValue(
        int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint value, IntPtr preparsedData, byte[] report, int reportLength);

    /// <summary>
    /// HIDP_VALUE_CAPS。大小 60 字节（x64 下也是 60，字段全是 2/4 字节标量，无指针）。
    /// 布局照 hidpi.h 原样抄，错一个字段后面全错位 —— ReportID 在偏移 2。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsagePage;
        public ushort LinkUsage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public ushort[] Reserved2;

        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;
        public ushort UsageMin;
        public ushort UsageMax;
    }

    /// <summary>枚举某个报表类型（Input/Output/Feature）下声明过的 Report ID。</summary>
    [DllImport("hid.dll", ExactSpelling = true)]
    internal static extern int HidP_GetValueCaps(
        int reportType, [Out] HIDP_VALUE_CAPS[]? caps, ref ushort capsLength, IntPtr preparsedData);
}
