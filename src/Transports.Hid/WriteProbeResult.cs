namespace GearTray.Transport.Hid;

/// <summary>
/// 可写性判定结果。刻意区分「打开成功」与「真能写」——
/// M0 实测里 0x0001 鼠标集合用 GENERIC_WRITE 也能打开，但那是系统独占的顶层集合，
/// 根本不该被驱动当作控制通道。
/// </summary>
public enum WriteProbeResult
{
    /// <summary>真的写出去了（WriteFile 返回成功且字节数一致）。</summary>
    Writable,

    /// <summary>句柄打开失败。</summary>
    OpenFailed,

    /// <summary>句柄能开，但 WriteFile 被驱动拒绝 —— 前面那个「可写」是假象。</summary>
    WriteRejected,

    /// <summary>接口没有 Output Report，无从写起。</summary>
    SkippedNoOutputReport,

    /// <summary>键盘/鼠标集合：写入会向系统注入真实输入，探测阶段禁止触碰。</summary>
    SkippedInputRisk,
}
