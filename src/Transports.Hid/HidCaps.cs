namespace GearTray.Transport.Hid;

/// <summary>一条 value cap 的摘要，用于探测台诊断。</summary>
public sealed record HidCapInfo(
    string ReportType,
    ushort UsagePage,
    byte ReportId,
    ushort BitSize,
    ushort ReportCount,
    ushort UsageMin,
    ushort UsageMax)
{
    public override string ToString() =>
        $"{ReportType,-7} page=0x{UsagePage:X4} id=0x{ReportId:X2} bits={BitSize,-3} count={ReportCount,-3} usage=0x{UsageMin:X4}..0x{UsageMax:X4}";
}

/// <summary>
/// 从报表描述符里读协议事实。
///
/// 为什么值得做：Report ID 不是猜出来的，是描述符里声明过的。
/// 盲扫 0x01–0x20 既慢又可能踩到未声明的 ID 被驱动层拒绝，
/// 而 HidP_GetValueCaps 一次就能拿到全部合法 ID。
/// </summary>
public static class HidCaps
{
    /// <summary>HIDP_VALUE_CAPS 的托管布局大小。必须等于 60，不等就说明字段定义错位。</summary>
    public static int ValueCapsStructSize => System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.HIDP_VALUE_CAPS>();

    public static IReadOnlyList<byte> GetReportIds(IntPtr preparsed, int reportType)
    {
        var ids = new SortedSet<byte>();
        foreach (var cap in Enumerate(preparsed, reportType))
        {
            ids.Add(cap.ReportId);
        }

        return [.. ids];
    }

    /// <summary>
    /// 枚举某个报表类型的全部 value cap。
    /// 两次调用模式：第一次传 null 只为拿所需长度（返回 BUFFER_TOO_SMALL 是预期行为）。
    /// </summary>
    public static IReadOnlyList<HidCapInfo> Enumerate(IntPtr preparsed, int reportType)
    {
        if (preparsed == IntPtr.Zero)
        {
            return [];
        }

        ushort length = 0;
        NativeMethods.HidP_GetValueCaps(reportType, null, ref length, preparsed);

        if (length == 0)
        {
            return [];
        }

        var caps = new NativeMethods.HIDP_VALUE_CAPS[length];
        var status = NativeMethods.HidP_GetValueCaps(reportType, caps, ref length, preparsed);
        if (status != NativeMethods.HIDP_STATUS_SUCCESS)
        {
            return [];
        }

        var name = reportType switch
        {
            NativeMethods.HidP_Input => "input",
            NativeMethods.HidP_Output => "output",
            _ => "feature",
        };

        var result = new List<HidCapInfo>(length);
        for (var i = 0; i < length; i++)
        {
            var cap = caps[i];
            result.Add(new HidCapInfo(
                name, cap.UsagePage, cap.ReportID, cap.BitSize, cap.ReportCount, cap.UsageMin, cap.UsageMax));
        }

        return result;
    }

    /// <summary>三个报表类型全部展开，调试用。</summary>
    public static IReadOnlyList<HidCapInfo> EnumerateAll(IntPtr preparsed) =>
    [
        .. Enumerate(preparsed, NativeMethods.HidP_Input),
        .. Enumerate(preparsed, NativeMethods.HidP_Output),
        .. Enumerate(preparsed, NativeMethods.HidP_Feature),
    ];
}
