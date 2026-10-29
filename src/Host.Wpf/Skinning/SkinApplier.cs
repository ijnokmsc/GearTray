using System.Windows;
using System.Windows.Media;
using GearTray.Theming;

namespace GearTray.Host.Skinning;

/// <summary>
/// 把皮肤 Token 落成 WPF 资源字典，并支持运行时整表替换。
///
/// 之所以走 <see cref="ResourceDictionary"/> 而不是「逐个控件改属性」：
/// 控件里的 <c>{DynamicResource Skin.Brush.X}</c> 会在字典被替换后自动重新求值，
/// 所以换皮肤不需要重建任何控件树 —— 而重建控件树正是之前把进程搞崩的那条路。
/// </summary>
internal static class SkinApplier
{
    /// <summary>皮肤字典固定放在合并列表的第 0 位，换肤就是原位替换，不增不减。</summary>
    private const int Slot = 0;

    public static Skin Current { get; private set; } = SkinRegistry.Light;

    /// <summary>当前皮肤是否偏暗，给托盘图标选配色的。</summary>
    public static bool IsDark => Current.IsDark;

    public static void Apply(Skin skin)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var dictionary = Build(skin);
        var merged = app.Resources.MergedDictionaries;

        if (merged.Count > Slot)
        {
            merged[Slot] = dictionary;
        }
        else
        {
            merged.Insert(0, dictionary);
        }

        Current = skin;
    }

    public static ResourceDictionary Build(Skin skin)
    {
        var t = skin.Tokens;
        var dictionary = new ResourceDictionary
        {
            ["Skin.Id"] = skin.Id,
            ["Skin.Name"] = skin.Name,
            ["Skin.IsDark"] = skin.IsDark,
            ["Skin.Layout"] = skin.Layout.ToString(),

            ["Skin.Brush.WindowBackground"] = Brush(t.WindowBackground, Colors.White),
            ["Skin.Brush.Surface"] = Brush(t.Surface, Colors.White),
            ["Skin.Brush.SurfaceAlt"] = Brush(t.SurfaceAlt, Color.FromRgb(0xF7, 0xF7, 0xF9)),
            ["Skin.Brush.TextPrimary"] = Brush(t.TextPrimary, Colors.Black),
            ["Skin.Brush.TextSecondary"] = Brush(t.TextSecondary, Color.FromRgb(0x66, 0x66, 0x66)),
            ["Skin.Brush.TextTertiary"] = Brush(t.TextTertiary, Color.FromRgb(0x99, 0x99, 0x99)),
            ["Skin.Brush.Border"] = Brush(t.Border, Color.FromRgb(0xE4, 0xE4, 0xE8)),
            ["Skin.Brush.Accent"] = Brush(t.Accent, Color.FromRgb(0x2D, 0x7D, 0xD2)),
            ["Skin.Brush.AccentForeground"] = Brush(t.AccentForeground, Colors.White),
            ["Skin.Brush.ControlBackground"] = Brush(t.ControlBackground, Colors.White),
            ["Skin.Brush.ControlBorder"] = Brush(t.ControlBorder, Color.FromRgb(0xC8, 0xC8, 0xD0)),
            ["Skin.Brush.Danger"] = Brush(t.Danger, Color.FromRgb(0xD1, 0x34, 0x38)),
            ["Skin.Brush.Warning"] = Brush(t.Warning, Color.FromRgb(0xF0, 0xA0, 0x20)),
            ["Skin.Brush.Success"] = Brush(t.Success, Color.FromRgb(0x3F, 0xA4, 0x5B)),

            ["Skin.CornerRadius"] = new CornerRadius(t.Radius),
            ["Skin.Spacing"] = t.Spacing,
            ["Skin.FontFamily"] = new FontFamily(t.FontFamily),
            ["Skin.FontSize.Caption"] = t.FontSizeCaption,
            ["Skin.FontSize.Base"] = t.FontSizeBase,
            ["Skin.FontSize.Title"] = t.FontSizeTitle,
            ["Skin.FontSize.Heading"] = t.FontSizeHeading,
            ["Skin.FontSize.Display"] = t.FontSizeDisplay,
        };

        return dictionary;
    }

    /// <summary>
    /// 解析颜色。皮肤是外部文件，写错一个字符不该让面板变透明 ——
    /// 解析不了就退回给定的兜底色。
    /// </summary>
    private static SolidColorBrush Brush(string hex, Color fallback)
    {
        Color color;

        try
        {
            color = string.IsNullOrWhiteSpace(hex)
                ? fallback
                : (Color)ColorConverter.ConvertFromString(hex)!;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidCastException)
        {
            color = fallback;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
