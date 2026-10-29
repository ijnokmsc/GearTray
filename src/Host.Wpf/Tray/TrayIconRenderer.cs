using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GearTray.Core;

namespace GearTray.Host.Tray;

/// <summary>
/// 托盘图标绘制。托盘只吃 HICON，WPF 的控件树进不去，
/// 所以这里是独立的一条渲染路径（对应设计文档 12.8）。
/// </summary>
internal static class TrayIconRenderer
{
    private const int Size = 32;

    public static IntPtr Create(int? percent, bool isStale)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)), null, new Rect(0, 0, Size, Size));

            var thickness = Size * 0.13;
            var radius = (Size - thickness) / 2.0 - 2.0;
            var center = new Point(Size / 2.0, Size / 2.0);

            // 底环：始终画，保证「设备不存在 / 未知电量」时图标不是空白。
            dc.DrawEllipse(null,
                new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), thickness),
                center, radius, radius);

            if (percent is { } value and > 0)
            {
                var color = ColorFor(value, isStale);
                var pen = new Pen(new SolidColorBrush(color), thickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                };

                dc.DrawGeometry(null, pen, BuildArc(center, radius, value));
            }
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        return ToHIcon(bitmap);
    }

    private static Color ColorFor(int percent, bool isStale)
    {
        if (isStale)
        {
            return Color.FromRgb(0x8E, 0x8E, 0x93);
        }

        return percent switch
        {
            >= 60 => Color.FromRgb(0x34, 0xC7, 0x59),
            >= 30 => Color.FromRgb(0xFF, 0x9F, 0x0A),
            _ => Color.FromRgb(0xFF, 0x45, 0x3A),
        };
    }

    private static Geometry BuildArc(Point center, double radius, int percent)
    {
        // 留一点缺口，100% 时不会首尾重叠成一坨。
        var sweep = Math.Clamp(percent, 0, 100) / 100.0 * 360.0;
        if (sweep >= 359.0)
        {
            sweep = 359.0;
        }

        const double startAngle = -90.0;
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, startAngle + sweep);

        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            IsFilled = false,
        };

        figure.Segments.Add(new ArcSegment(
            end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static IntPtr ToHIcon(BitmapSource source)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // 负数 = 自上而下，和 RenderTargetBitmap 的行序一致
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
            biSizeImage = stride * height,
        };

        var info = new BITMAPINFO { bmiHeader = header };
        var dib = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);

        if (dib == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);

            var maskStride = (width + 7) / 8;
            var maskBits = new byte[maskStride * height];
            var mask = CreateBitmap(width, height, 1, 1, maskBits);

            var iconInfo = new ICONINFO
            {
                fIcon = true,
                hbmMask = mask,
                hbmColor = dib,
            };

            var icon = CreateIconIndirect(ref iconInfo);

            if (mask != IntPtr.Zero)
            {
                DeleteObject(mask);
            }

            return icon;
        }
        finally
        {
            DeleteObject(dib);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[]? bits);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr handle);
}
