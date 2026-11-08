using System.Runtime.InteropServices;
using System.Text.Json;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.UI.Platform;

/// <summary>
/// --layout-probe：不改系统缩放，按 100%/125%/150%/200% 四种有效缩放走一遍与真实运行
/// 完全相同的 <see cref="QuickSwitchLayout"/> 计算，并核对半宽、间距、居中、工作区边界、
/// 列数阈值与各窗口物理尺寸。结果写成 JSON，供发布验收留档。
/// </summary>
internal static class LayoutProbe
{
    private const double DesignDialogWidthDip = 720;
    private const double DesignDialogHeightDip = 480;
    private const double NarrowDialogWidthDip = 280;
    private const double NarrowDialogHeightDip = 360;

    private static readonly double[] Scales = { 1.0, 1.25, 1.5, 2.0 };

    private static readonly (string Name, int WidthDip, int HeightDip)[] Windows =
    {
        ("main", 660, 436),
        ("settings", 840, 640),
        ("update", 440, 300),
        ("about", 380, 236)
    };

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    public static int Run(string outputPath)
    {
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4)); // PerMonitorV2，与 UI 进程一致

            var lpMonitor = MonitorFromCursor();
            if (lpMonitor == IntPtr.Zero)
            {
                AppLog.Error("layout-probe 失败：找不到显示器");
                return 4;
            }

            var lpInfo = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (!NativeMethods.GetMonitorInfoW(lpMonitor, ref lpInfo))
            {
                AppLog.Error("layout-probe 失败：GetMonitorInfoW 返回 false");
                return 4;
            }

            var lpRealScale = 1.0;
            if (NativeMethods.GetDpiForMonitor(lpMonitor, NativeMethods.MdtEffectiveDpi, out var lpDpiX, out _) == 0
                && lpDpiX > 0)
            {
                lpRealScale = lpDpiX / 96.0;
            }

            // 把当前物理工作区换算成 DIP（逻辑）坐标，再按目标缩放生成对应物理工作区：
            // 这样 125%/150% 的数字来自本机真实显示器尺寸，而不是凭空的画布。
            var lpLogicalWork = ToLogical(lpInfo.Work, lpRealScale);

            var lpResult = new
            {
                probe = "quick-switch-layout",
                realScale = lpRealScale,
                realWork = ToObject(lpInfo.Work),
                logicalWork = new
                {
                    left = Math.Round(lpLogicalWork.Left, 2),
                    top = Math.Round(lpLogicalWork.Top, 2),
                    width = Math.Round(lpLogicalWork.Width, 2),
                    height = Math.Round(lpLogicalWork.Height, 2)
                },
                scales = Scales.Select(scale => new
                {
                    scale,
                    windows = Windows.Select(window => new
                    {
                        name = window.Name,
                        dip = $"{window.WidthDip}x{window.HeightDip}",
                        px = $"{Round(window.WidthDip, scale)}x{Round(window.HeightDip, scale)}"
                    }).ToArray(),
                    cases = Scenarios(scale, lpLogicalWork)
                }).ToArray()
            };

            var lpJson = JsonSerializer.Serialize(lpResult, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outputPath, lpJson);
            AppLog.Info($"layout-probe 已写入: {outputPath}（realScale={lpRealScale:0.##}）");
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("layout-probe 失败", ex);
            return 1;
        }
    }

    private static object[] Scenarios(double scale, LogicalRect work)
    {
        var lpPixelWork = ToPixel(work, scale);
        var lpResults = new List<object>();

        // 常规对话框：720×480 DIP，居中偏上。
        var lpWide = Dialog(work, DesignDialogWidthDip, DesignDialogHeightDip,
            work.Left + (work.Width - DesignDialogWidthDip) / 2,
            work.Top + work.Height * 0.25, scale);
        lpResults.Add(Case("regular", scale, lpWide, lpPixelWork, 6));

        // 窄对话框：宽度不足一半列阈值，必须退回 3 列且栏宽仍为一半。
        var lpNarrow = Dialog(work, NarrowDialogWidthDip, NarrowDialogHeightDip,
            work.Left + 24, work.Top + work.Height * 0.3, scale);
        lpResults.Add(Case("narrow", scale, lpNarrow, lpPixelWork, 6));

        // 最大化：对话框占满工作区，下方与上方都没有空间。
        var lpMax = new DialogRect(
            lpPixelWork.Left,
            lpPixelWork.Top,
            lpPixelWork.Right,
            lpPixelWork.Bottom,
            lpPixelWork.Left,
            lpPixelWork.Top,
            lpPixelWork.Right,
            lpPixelWork.Bottom);
        lpResults.Add(Case("maximized", scale, lpMax, lpPixelWork, 6));

        // 贴底：对话框底边离工作区底部只剩 24 DIP，栏放不下，应翻到对话框上方。
        var lpBottom = Dialog(work, DesignDialogWidthDip, DesignDialogHeightDip,
            work.Left + (work.Width - DesignDialogWidthDip) / 2,
            work.Bottom - DesignDialogHeightDip - 24, scale);
        lpResults.Add(Case("bottom-edge", scale, lpBottom, lpPixelWork, 6));

        // 两个对话框重叠：各自算各自的一条栏，检查栏是否只落在自己对话框的几何范围内。
        var lpFront = Dialog(work, 600, 420, work.Left + work.Width * 0.45, work.Top + work.Height * 0.1, scale);
        var lpBack = Dialog(work, 600, 420, work.Left + work.Width * 0.2, work.Top + work.Height * 0.3, scale);
        var lpFrontBar = QuickSwitchLayout.Attach(lpFront.Frame, lpFront.WindowRect, true, lpPixelWork, true, scale, 6);
        var lpBackBar = QuickSwitchLayout.Attach(lpBack.Frame, lpBack.WindowRect, true, lpPixelWork, true, scale, 6);
        lpResults.Add(new
        {
            name = "two-dialogs",
            scale,
            frontBar = ToObject(lpFrontBar),
            backBar = ToObject(lpBackBar),
            frontBarOverlapsBackDialog = Intersects(lpFrontBar, lpBack.Frame),
            frontBarOverlapsOwnDialog = Intersects(lpFrontBar, lpFront.Frame),
            note = "栏位于对话框矩形之外，故 frontBarOverlapsOwnDialog 必须为 false；" +
                   "frontBarOverlapsBackDialog 为 true 是既定取舍（见快速切换栏文档「已知限制」1）。"
        });

        return lpResults.ToArray();
    }

    private static object Case(string name, double scale, DialogRect dialog, NativeMethods.Rect work, int folderCount)
    {
        var lpBar = QuickSwitchLayout.Attach(dialog.Frame, dialog.WindowRect, true, work, true, scale, folderCount);
        var lpExpectedGap = (int)Math.Round(QuickSwitchLayout.GapDip * scale);
        var lpExpectedColumns = QuickSwitchLayout.Columns(lpBar.Width, scale);
        var lpBottom = Math.Max(dialog.Frame.Bottom, dialog.WindowRect.Bottom);
        var lpFrameCenter = dialog.Frame.Left + dialog.Frame.Width / 2.0;
        var lpBarCenter = lpBar.X + lpBar.Width / 2.0;

        var lpAtBottom = lpBar.Y == lpBottom + lpExpectedGap;
        var lpInsideWork = lpBar.X >= work.Left && lpBar.X + lpBar.Width <= work.Right
                           && lpBar.Y >= work.Top && lpBar.Y + lpBar.Height <= work.Bottom;

        return new
        {
            name,
            scale,
            dialogFrame = ToObject(dialog.Frame),
            bar = ToObject(lpBar),
            checks = new
            {
                halfWidth = Math.Abs(lpBar.Width - dialog.Frame.Width / 2.0) <= 1,
                gapPx = lpAtBottom ? lpExpectedGap : (int?)null,
                gapMatches = lpAtBottom ? Math.Abs(lpBar.Y - lpBottom) <= lpExpectedGap : (bool?)null,
                centerOffsetPx = Math.Round(lpBarCenter - lpFrameCenter, 2),
                centered = Math.Abs(lpBarCenter - lpFrameCenter) <= 1,
                columns = lpExpectedColumns,
                insideWorkArea = lpInsideWork,
                flippedAbove = lpBar.Y < dialog.Frame.Top
            }
        };
    }

    private static DialogRect Dialog(LogicalRect work, double widthDip, double heightDip, double leftDip, double topDip, double scale)
    {
        var lpLeft = Round(leftDip, scale);
        var lpTop = Round(topDip, scale);
        var lpWidth = Round(widthDip, scale);
        var lpHeight = Round(heightDip, scale);
        var lpRight = lpLeft + lpWidth;
        var lpBottom = lpTop + lpHeight;

        // 窗口矩形比 DWM 可视外框各向外 4 DIP：贴近真实对话框的不可见缩放边框。
        var lpInset = Round(4, scale);
        return new DialogRect(
            lpLeft, lpTop, lpRight, lpBottom,
            lpLeft - lpInset, lpTop - lpInset, lpRight + lpInset, lpBottom + lpInset);
    }

    private static bool Intersects(QuickSwitchLayout.BarRect bar, ScreenBounds other)
    {
        return bar.X < other.Right && bar.X + bar.Width > other.Left
            && bar.Y < other.Bottom && bar.Y + bar.Height > other.Top;
    }

    private static IntPtr MonitorFromCursor()
    {
        return NativeMethods.GetCursorPos(out var lpPoint)
            ? NativeMethods.MonitorFromPoint(lpPoint, NativeMethods.MonitorDefaultToNearest)
            : IntPtr.Zero;
    }

    private static int Round(double dip, double scale)
    {
        return (int)Math.Round(dip * scale);
    }

    private static object ToObject(NativeMethods.Rect rect)
    {
        return new
        {
            left = rect.Left,
            top = rect.Top,
            right = rect.Right,
            bottom = rect.Bottom,
            width = Math.Max(0, rect.Right - rect.Left),
            height = Math.Max(0, rect.Bottom - rect.Top)
        };
    }

    private static object ToObject(ScreenBounds rect)
    {
        return new
        {
            left = rect.Left,
            top = rect.Top,
            right = rect.Right,
            bottom = rect.Bottom,
            width = rect.Width,
            height = rect.Height
        };
    }

    private static object ToObject(QuickSwitchLayout.BarRect rect)
    {
        return new
        {
            x = rect.X,
            y = rect.Y,
            width = rect.Width,
            height = rect.Height,
            columns = rect.Columns
        };
    }

    private static LogicalRect ToLogical(NativeMethods.Rect rect, double scale)
    {
        return new LogicalRect(
            rect.Left / scale,
            rect.Top / scale,
            rect.Right / scale,
            rect.Bottom / scale);
    }

    private static NativeMethods.Rect ToPixel(LogicalRect rect, double scale)
    {
        return new NativeMethods.Rect
        {
            Left = Round(rect.Left, scale),
            Top = Round(rect.Top, scale),
            Right = Round(rect.Right, scale),
            Bottom = Round(rect.Bottom, scale)
        };
    }

    private readonly record struct LogicalRect(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Right - Left;

        public double Height => Bottom - Top;
    }

    private readonly record struct DialogRect(
        int FrameLeft,
        int FrameTop,
        int FrameRight,
        int FrameBottom,
        int WindowLeft,
        int WindowTop,
        int WindowRight,
        int WindowBottom)
    {
        public ScreenBounds Frame => new(FrameLeft, FrameTop, FrameRight, FrameBottom);

        public NativeMethods.Rect WindowRect => new()
        {
            Left = WindowLeft,
            Top = WindowTop,
            Right = WindowRight,
            Bottom = WindowBottom
        };
    }
}
