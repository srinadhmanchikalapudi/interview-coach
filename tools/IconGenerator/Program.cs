using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Draws the Interview Coach icon: a speech bubble (the interview) holding a check mark (the coaching),
// on a rounded indigo-to-violet tile. All shapes live in a 256 x 256 design space and are scaled to each size.

namespace IconGenerator;

internal static class Program
{
    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256];

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : "Assets";
        Directory.CreateDirectory(outDir);

        var frames = IconSizes.ToDictionary(size => size, size => Png(Render(size)));
        File.WriteAllBytes(Path.Combine(outDir, "app.ico"), BuildIco(frames));
        File.WriteAllBytes(Path.Combine(outDir, "logo-256.png"), frames[256]);
        File.WriteAllBytes(Path.Combine(outDir, "logo-64.png"), frames[64]);
        Console.WriteLine($"Wrote app.ico ({IconSizes.Length} sizes), logo-256.png and logo-64.png to {Path.GetFullPath(outDir)}");
        return 0;
    }

    private static RenderTargetBitmap Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            Draw(dc, small: size <= 32);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Draw(DrawingContext dc, bool small)
    {
        // The tile
        var tile = new Rect(8, 8, 240, 240);
        var tileBrush = new LinearGradientBrush(Color.FromRgb(0x4F, 0x46, 0xE5), Color.FromRgb(0x8B, 0x3F, 0xE8), new Point(0, 0), new Point(1, 1));
        dc.DrawRoundedRectangle(tileBrush, null, tile, 58, 58);

        // A soft highlight across the top of the tile
        var gloss = new LinearGradientBrush(Color.FromArgb(0x38, 255, 255, 255), Color.FromArgb(0x00, 255, 255, 255), new Point(0.5, 0), new Point(0.5, 0.55));
        dc.DrawRoundedRectangle(gloss, null, tile, 58, 58);

        // The bubble: a rounded body with a tail at the bottom left
        var bubble = BubbleGeometry();
        dc.PushTransform(new TranslateTransform(0, 7));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x42, 0x1E, 0x1B, 0x6B)), null, bubble); // shadow
        dc.Pop();
        dc.DrawGeometry(Brushes.White, null, bubble);

        // The check mark inside it
        var checkColor = new LinearGradientBrush(Color.FromRgb(0x4F, 0x46, 0xE5), Color.FromRgb(0x7C, 0x3A, 0xED), new Point(0, 0), new Point(1, 1));
        var pen = new Pen(checkColor, small ? 26 : 22) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var check = new StreamGeometry();
        using (var ctx = check.Open())
        {
            ctx.BeginFigure(new Point(99, 116), isFilled: false, isClosed: false);
            ctx.LineTo(new Point(123, 140), true, true);
            ctx.LineTo(new Point(167, 90), true, true);
        }
        check.Freeze();
        dc.DrawGeometry(null, pen, check);

        // A small spark at the top right, left out at tiny sizes where it would only be noise
        if (!small)
        {
            var spark = SparkGeometry(new Point(198, 62), 17);
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)), null, spark);
        }
    }

    private static Geometry BubbleGeometry()
    {
        var body = new RectangleGeometry(new Rect(50, 62, 156, 108), 34, 34);
        var tail = new StreamGeometry();
        using (var ctx = tail.Open())
        {
            ctx.BeginFigure(new Point(78, 160), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(76, 210), true, false);
            ctx.LineTo(new Point(126, 164), true, false);
        }
        tail.Freeze();
        var union = new CombinedGeometry(GeometryCombineMode.Union, body, tail);
        union.Freeze();
        return union;
    }

    // A four-pointed star
    private static Geometry SparkGeometry(Point center, double radius)
    {
        var inner = radius * 0.32;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < 8; i++)
            {
                var angle = Math.PI / 4 * i - Math.PI / 2;
                var r = i % 2 == 0 ? radius : inner;
                var point = new Point(center.X + r * Math.Cos(angle), center.Y + r * Math.Sin(angle));
                if (i == 0) ctx.BeginFigure(point, isFilled: true, isClosed: true);
                else ctx.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // An .ico is a small directory followed by the images. Modern Windows reads PNG-compressed images at every size.
    private static byte[] BuildIco(IReadOnlyDictionary<int, byte[]> pngBySize)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);                    // reserved
        writer.Write((ushort)1);                    // type: icon
        writer.Write((ushort)pngBySize.Count);

        var offset = 6 + 16 * pngBySize.Count;
        foreach (var (size, png) in pngBySize.OrderBy(p => p.Key))
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // width (0 means 256)
            writer.Write((byte)(size >= 256 ? 0 : size)); // height
            writer.Write((byte)0);                        // palette colors
            writer.Write((byte)0);                        // reserved
            writer.Write((ushort)1);                      // color planes
            writer.Write((ushort)32);                     // bits per pixel
            writer.Write((uint)png.Length);
            writer.Write((uint)offset);
            offset += png.Length;
        }
        foreach (var (_, png) in pngBySize.OrderBy(p => p.Key))
            writer.Write(png);

        writer.Flush();
        return stream.ToArray();
    }
}
