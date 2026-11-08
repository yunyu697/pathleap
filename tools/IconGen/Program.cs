using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pathleap.IconGen;

/// <summary>
/// 按 assets/brand/pathleap-bolt-aurora-{dark|light}.svg 的几何与配色生成图标。
/// 输出：Assets/Pathleap.ico（暗色多尺寸）、Assets/pathleap-mark-dark.png / light.png（界面用）。
/// 小尺寸（16/24）省略装饰元素并加粗主线，保证 16px 仍能读出「放大镜 + 闪电」。
/// </summary>
internal static class Program
{
    private static readonly int[] IconSizes = { 16, 24, 32, 48, 64, 128, 256 };

    private static int Main(string[] args)
    {
        var lpRoot = args.Length > 0
            ? Path.GetFullPath(args[0])
            : FindRepositoryRoot();
        if (lpRoot is null || !Directory.Exists(lpRoot))
        {
            Console.Error.WriteLine("找不到仓库根目录，请把仓库根路径作为第一个参数传入。");
            return 2;
        }

        var lpAssets = Path.Combine(lpRoot, "src", "Pathleap.UI", "Assets");
        Directory.CreateDirectory(lpAssets);

        // 程序图标（exe / 安装包 / 托盘 / 任务栏）统一用暗色版。
        WriteIco(Path.Combine(lpAssets, "Pathleap.ico"), dark: true);

        // 界面内标记随明暗主题切换，用 128px PNG。
        WritePng(Path.Combine(lpAssets, "pathleap-mark-dark.png"), 128, dark: true);
        WritePng(Path.Combine(lpAssets, "pathleap-mark-light.png"), 128, dark: false);

        Console.WriteLine($"图标已生成: {lpAssets}");
        return 0;
    }

    private static string? FindRepositoryRoot()
    {
        var lpDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (lpDir is not null)
        {
            if (File.Exists(Path.Combine(lpDir.FullName, "Directory.Build.props")))
            {
                return lpDir.FullName;
            }

            lpDir = lpDir.Parent;
        }

        return null;
    }

    private static void WritePng(string path, int size, bool dark)
    {
        var lpBitmap = Render(size, dark);
        var lpEncoder = new PngBitmapEncoder();
        lpEncoder.Frames.Add(BitmapFrame.Create(lpBitmap));
        using var lpStream = File.Create(path);
        lpEncoder.Save(lpStream);
    }

    /// <summary>写 PNG 压缩帧的多尺寸 ICO（Windows Vista 起原生支持）。</summary>
    private static void WriteIco(string path, bool dark)
    {
        var lpFrames = new List<byte[]>();
        foreach (var lpSize in IconSizes)
        {
            var lpEncoder = new PngBitmapEncoder();
            lpEncoder.Frames.Add(BitmapFrame.Create(Render(lpSize, dark)));
            using var lpStream = new MemoryStream();
            lpEncoder.Save(lpStream);
            lpFrames.Add(lpStream.ToArray());
        }

        using var lpFile = File.Create(path);
        using var lpWriter = new BinaryWriter(lpFile);
        lpWriter.Write((ushort)0);
        lpWriter.Write((ushort)1);
        lpWriter.Write((ushort)lpFrames.Count);

        var lpOffset = 6 + 16 * lpFrames.Count;
        for (var lpIndex = 0; lpIndex < lpFrames.Count; lpIndex++)
        {
            var lpSize = IconSizes[lpIndex];
            lpWriter.Write((byte)(lpSize >= 256 ? 0 : lpSize));
            lpWriter.Write((byte)(lpSize >= 256 ? 0 : lpSize));
            lpWriter.Write((byte)0);
            lpWriter.Write((byte)0);
            lpWriter.Write((ushort)1);
            lpWriter.Write((ushort)32);
            lpWriter.Write(lpFrames[lpIndex].Length);
            lpWriter.Write(lpOffset);
            lpOffset += lpFrames[lpIndex].Length;
        }

        foreach (var lpFrame in lpFrames)
        {
            lpWriter.Write(lpFrame);
        }
    }

    private static BitmapSource Render(int size, bool dark)
    {
        var lpScale = size / 128.0;
        var lpVisual = new DrawingVisual();
        using (var lpContext = lpVisual.RenderOpen())
        {
            lpContext.PushTransform(new ScaleTransform(lpScale, lpScale));

            var lpTile = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            lpTile.GradientStops.Add(dark
                ? new GradientStop(Color.FromRgb(0x1D, 0x29, 0x3C), 0)
                : new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), 0));
            lpTile.GradientStops.Add(dark
                ? new GradientStop(Color.FromRgb(0x0A, 0x11, 0x20), 1)
                : new GradientStop(Color.FromRgb(0xF4, 0xF7, 0xFF), 1));

            lpContext.DrawRoundedRectangle(lpTile, null, new Rect(0, 0, 128, 128), 30, 30);

            if (size >= 32)
            {
                var lpHalo = new RadialGradientBrush
                {
                    MappingMode = BrushMappingMode.RelativeToBoundingBox,
                    Center = new Point(0.28, 0.2),
                    GradientOrigin = new Point(0.28, 0.2),
                    RadiusX = 0.85,
                    RadiusY = 0.85
                };
                var lpHaloColor = dark ? Color.FromRgb(0x22, 0xC7, 0xE8) : Color.FromRgb(0x3B, 0x76, 0xF6);
                lpHalo.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(dark ? 43 : 26), lpHaloColor.R, lpHaloColor.G, lpHaloColor.B), 0));
                lpHalo.GradientStops.Add(new GradientStop(Color.FromArgb(0, lpHaloColor.R, lpHaloColor.G, lpHaloColor.B), 1));
                lpContext.DrawRoundedRectangle(lpHalo, null, new Rect(0, 0, 128, 128), 30, 30);
            }

            var lpEdge = dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(23, 16, 24, 40);
            lpContext.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(lpEdge), 1), new Rect(0.5, 0.5, 127, 127), 29.5, 29.5);

            var lpBrand = new LinearGradientBrush
            {
                StartPoint = new Point(0.05, 0),
                EndPoint = new Point(0.95, 1)
            };
            if (dark)
            {
                lpBrand.GradientStops.Add(new GradientStop(Color.FromRgb(0x66, 0x94, 0xF8), 0));
                lpBrand.GradientStops.Add(new GradientStop(Color.FromRgb(0x53, 0xD3, 0xED), 1));
            }
            else
            {
                lpBrand.GradientStops.Add(new GradientStop(Color.FromRgb(0x39, 0x71, 0xEC), 0));
                lpBrand.GradientStops.Add(new GradientStop(Color.FromRgb(0x20, 0xB7, 0xD7), 1));
            }

            var lpBold = size <= 24 ? 1.28 : 1.0;

            if (size >= 32)
            {
                var lpDash = new SolidColorBrush(dark
                    ? Color.FromRgb(0x53, 0xD3, 0xED)
                    : Color.FromRgb(0x20, 0xB7, 0xD7));
                DrawLine(lpContext, new Point(15, 47), new Point(29, 47), 3.4, lpDash, 0.55);
                DrawLine(lpContext, new Point(19, 60), new Point(33, 60), 3.4, lpDash, 0.35);
            }

            var lpBolt = new StreamGeometry();
            Point[] lpPoints;
            if (size <= 20)
            {
                // 16px 下放大镜环与闪电会挤成一团；只保留放大的闪电符号，保证仍可辨认。
                lpPoints = new[]
                {
                    new Point(74, 30),
                    new Point(42, 70),
                    new Point(61, 70),
                    new Point(52, 98),
                    new Point(86, 54),
                    new Point(65, 54),
                    new Point(78, 30)
                };
            }
            else
            {
                var lpCenter = new Point(61, 56);
                lpContext.DrawEllipse(null, new Pen(lpBrand, 8 * lpBold), lpCenter, 25, 25);
                DrawLine(lpContext, new Point(79, 74), new Point(97, 92), 9 * lpBold, lpBrand, 1.0);
                lpPoints = new[]
                {
                    new Point(64, 38),
                    new Point(48, 58),
                    new Point(58, 58),
                    new Point(55, 73),
                    new Point(74, 52),
                    new Point(63, 52),
                    new Point(67, 38)
                };
            }

            using (var lpBoltContext = lpBolt.Open())
            {
                lpBoltContext.BeginFigure(lpPoints[0], isFilled: true, isClosed: true);
                lpBoltContext.PolyLineTo(lpPoints.Skip(1).ToArray(), isStroked: false, isSmoothJoin: false);
            }

            lpBolt.Freeze();
            lpContext.DrawGeometry(new SolidColorBrush(dark
                ? Color.FromRgb(0xF7, 0xFA, 0xFF)
                : Color.FromRgb(0x11, 0x1C, 0x30)), null, lpBolt);

            if (size >= 32)
            {
                var lpDot = new SolidColorBrush(dark
                    ? Color.FromRgb(0x66, 0x94, 0xF8)
                    : Color.FromRgb(0x39, 0x71, 0xEC)) { Opacity = 0.55 };
                lpContext.DrawEllipse(lpDot, null, new Point(96, 33), 2.4, 2.4);
            }

            lpContext.Pop();
        }

        var lpBitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        lpBitmap.Render(lpVisual);
        lpBitmap.Freeze();
        return lpBitmap;
    }

    private static void DrawLine(DrawingContext context, Point from, Point to, double width, Brush brush, double opacity)
    {
        var lpBrush = brush.Clone();
        lpBrush.Opacity = opacity;
        var lpPen = new Pen(lpBrush, width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        context.DrawLine(lpPen, from, to);
    }
}
