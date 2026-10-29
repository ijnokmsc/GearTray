namespace GearTray.Theming;

/// <summary>
/// 宿主内置布局模板。S1 层：布局决定窗口尺寸与分区排布，皮肤只换 Token。
/// </summary>
public enum LayoutKind
{
    /// <summary>窄竖条，一列卡片。380×auto，适合常驻小面板。</summary>
    CardList,

    /// <summary>双列大卡。760×520，信息密度低但一眼看清。</summary>
    Showcase,

    /// <summary>左导航 + 右详情。780×560，设置项多时用。</summary>
    Console,

    /// <summary>左设备列表 + 右信息栏。760×560。</summary>
    Sidebar,
}

/// <summary>
/// 语义化设计 Token。**全部是语义名，不是颜色名** ——
/// 皮肤改的是「次要文字是什么色」，不是「灰色是什么灰」。
/// 这样换皮肤只需要换一张表，改版面时也不会因为「这里原来写的是 Gray」而漏掉。
///
/// 颜色一律用 <c>#RRGGBB</c> / <c>#AARRGGBB</c> 字符串，
/// 本工程刻意不引用 WPF：Token 是数据，怎么变成 Brush 是宿主的事。
/// </summary>
public sealed record SkinTokens
{
    public string WindowBackground { get; init; } = "#FFFFFF";

    /// <summary>卡片 / 列表行底色。</summary>
    public string Surface { get; init; } = "#FFFFFF";

    /// <summary>次级面：分组标题栏、内嵌块。</summary>
    public string SurfaceAlt { get; init; } = "#F7F7F9";

    public string TextPrimary { get; init; } = "#1A1A1A";

    /// <summary>说明文字、值标签。</summary>
    public string TextSecondary { get; init; } = "#666666";

    /// <summary>更弱的提示，如空状态。</summary>
    public string TextTertiary { get; init; } = "#999999";

    /// <summary>分隔线（通常带透明度）。</summary>
    public string Border { get; init; } = "#E4E4E8";

    public string Accent { get; init; } = "#2D7DD2";

    /// <summary>Accent 底色上的文字色。</summary>
    public string AccentForeground { get; init; } = "#FFFFFF";

    public string ControlBackground { get; init; } = "#FFFFFF";

    public string ControlBorder { get; init; } = "#C8C8D0";

    /// <summary>写入失败、设备异常。</summary>
    public string Danger { get; init; } = "#D13438";

    /// <summary>电量告急。</summary>
    public string Warning { get; init; } = "#F0A020";

    public string Success { get; init; } = "#3FA45B";

    /// <summary>圆角，px。</summary>
    public double Radius { get; init; } = 6;

    /// <summary>基础间距，px。布局内部按 1x/2x/3x 使用。</summary>
    public double Spacing { get; init; } = 8;

    public string FontFamily { get; init; } = "Microsoft YaHei UI, Segoe UI";

    public double FontSizeCaption { get; init; } = 11;

    public double FontSizeBase { get; init; } = 12;

    public double FontSizeTitle { get; init; } = 14;

    public double FontSizeHeading { get; init; } = 18;

    /// <summary>大号读数，托盘面板里的电量数字。</summary>
    public double FontSizeDisplay { get; init; } = 26;
}

/// <summary>
/// 一套皮肤 = 一个 Token 表 + 一个内置布局。
/// 皮肤不写 XAML，也不带代码：它只描述「长什么样」，不描述「有什么控件」。
/// </summary>
public sealed record Skin
{
    /// <summary>目录名 / 文件名用，需唯一。</summary>
    public string Id { get; init; } = "light";

    public string Name { get; init; } = "浅色";

    /// <summary>仅用于让宿主挑合适的托盘图标配色，不参与布局。</summary>
    public bool IsDark { get; init; }

    public LayoutKind Layout { get; init; } = LayoutKind.CardList;

    public SkinTokens Tokens { get; init; } = new();

    /// <summary>来源路径。内置皮肤为 null，磁盘皮肤是 skin.json 的绝对路径。</summary>
    public string? SourcePath { get; init; }
}
