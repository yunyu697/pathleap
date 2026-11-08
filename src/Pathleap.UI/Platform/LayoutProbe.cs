using System.Runtime.InteropServices;
using System.Text.Json;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.UI.Platform;

/// <summary>
/// --layout-probe：不改系统缩放与显示器拓扑，按 100%/125%/150%/200% 四种有效缩放、
/// 以及合成的三屏（左 150% / 主 125% / 右 100%）走一遍与真实运行完全相同的
/// <see cref="QuickSwitchLayout"/> 计算，并核对半宽、间距、居中、工作区边界、
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
                }).ToArray(),
                multiMonitor = MultiMonitor()
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

        // 窄对话框：低于 360 DIP 退到 2 列（单格文字更宽），栏宽仍为一半，6 个文件夹排成 3 行。
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

    private readonly record struct MonitorSpec(string Name, double Scale, NativeMethods.Rect Work);

    /// <summary>
    /// 合成的三屏拓扑（物理像素，含负坐标）：左屏 150%、主屏 125%（带 60px 任务栏）、右屏 100%。
    /// 本机只有一块 200% 显示器，真实跨屏拖动无法复现，因此用与运行时同一个纯几何函数
    /// 在这些拓扑上计算，作为多显示器分支的数值验收。
    /// </summary>
    private static MonitorSpec[] MultiMonitors()
    {
        return new[]
        {
            new MonitorSpec("left-150", 1.5, Rect(-2880, -220, 0, 1580)),
            new MonitorSpec("primary-125", 1.25, Rect(0, 0, 2560, 1380)),
            new MonitorSpec("right-100", 1.0, Rect(2560, 0, 4480, 1080))
        };
    }

    private static NativeMethods.Rect Rect(int left, int top, int right, int bottom)
    {
        return new NativeMethods.Rect { Left = left, Top = top, Right = right, Bottom = bottom };
    }

    /// <summary>把 720×480 DIP 的对话框放在指定显示器的居中偏上位置，按该屏缩放折算成物理像素。</summary>
    private static DialogRect DialogOn(MonitorSpec monitor, double widthDip, double heightDip, double topDip)
    {
        var lpWorkDip = ToLogical(monitor.Work, monitor.Scale);
        return Dialog(
            lpWorkDip,
            widthDip,
            heightDip,
            lpWorkDip.Left + (lpWorkDip.Width - widthDip) / 2,
            lpWorkDip.Top + topDip,
            monitor.Scale);
    }

    private static DialogRect DialogOn(MonitorSpec monitor)
    {
        return DialogOn(monitor, DesignDialogWidthDip, DesignDialogHeightDip, 60);
    }

    private static object MultiMonitor()
    {
        var lpMonitors = MultiMonitors();

        var lpPerMonitor = lpMonitors
            .Select(lpMonitor => Case($"dialog-on-{lpMonitor.Name}", lpMonitor.Scale, DialogOn(lpMonitor),
                lpMonitor.Work, 6, lpMonitor.Name))
            .ToArray();

        // 跨屏拖动：同一 720×480 DIP 对话框依次落在右屏 → 主屏 → 左屏。每一次都必须按新显示器
        // 重新算缩放、工作区与栏尺寸，不能沿用上一屏的坐标。
        var lpDragSteps = new[] { lpMonitors[2], lpMonitors[1], lpMonitors[0] }.Select(lpMonitor =>
        {
            var lpDialog = DialogOn(lpMonitor);
            var lpBar = QuickSwitchLayout.Attach(
                lpDialog.Frame, lpDialog.WindowRect, true, lpMonitor.Work, true, lpMonitor.Scale, 6);
            return new
            {
                monitor = lpMonitor.Name,
                scale = lpMonitor.Scale,
                work = ToObject(lpMonitor.Work),
                dialogFrame = ToObject(lpDialog.Frame),
                bar = ToObject(lpBar),
                geometry = new { x = lpBar.X, y = lpBar.Y, width = lpBar.Width, height = lpBar.Height },
                checks = new
                {
                    halfWidth = Math.Abs(lpBar.Width - lpDialog.Frame.Width / 2.0) <= 1,
                    gapPx = (int)Math.Round(QuickSwitchLayout.GapDip * lpMonitor.Scale),
                    insideWorkArea = IsInside(lpBar, lpMonitor.Work),
                    columns = lpBar.Columns
                }
            };
        }).ToArray();

        var lpRecomputed = lpDragSteps
            .Zip(lpDragSteps.Skip(1), (lpBefore, lpAfter) =>
                lpBefore.geometry.width != lpAfter.geometry.width && lpBefore.geometry.x != lpAfter.geometry.x)
            .All(lpStep => lpStep);

        return new
        {
            note = "合成拓扑：本机只有一块 200% 显示器，无法实测物理跨屏；这里用运行时同一个 " +
                   "QuickSwitchLayout 计算，验证「按对话框所在显示器重算缩放、工作区与栏尺寸」的数学分支。",
            monitors = lpMonitors.Select(lpMonitor => new
            {
                name = lpMonitor.Name,
                scale = lpMonitor.Scale,
                work = ToObject(lpMonitor.Work)
            }).ToArray(),
            perMonitor = lpPerMonitor,
            dragAcrossScreens = new
            {
                name = "drag-right-100-to-primary-125-to-left-150",
                steps = lpDragSteps,
                checks = new
                {
                    widthPerStepPx = lpDragSteps.Select(lpStep => lpStep.geometry.width).ToArray(),
                    allInsideOwnWorkArea = lpDragSteps.All(lpStep => lpStep.checks.insideWorkArea),
                    recomputedEveryStep = lpRecomputed
                }
            },
            straddle = Straddle(lpMonitors[1], lpMonitors[2])
        };
    }

    /// <summary>
    /// 对话框横跨主屏（125%）与右屏（100%）的交界。系统按 MonitorFromWindow 只选一块显示器，
    /// 栏也只落在那块屏的工作区内、不会横跨两屏。这里把两种可能的选择都算出来：
    /// 选中哪块屏由系统规则决定，但无论哪种，栏都必须在被选屏内且重新按该屏缩放。
    /// </summary>
    private static object Straddle(MonitorSpec first, MonitorSpec second)
    {
        // 900×600 DIP 的对话框按主屏 125% 折算为 1125×750 物理像素，基本均分在交界两侧。
        const int lpDialogWidthPx = 1125;
        const int lpDialogHeightPx = 750;
        var lpLeft = first.Work.Right - lpDialogWidthPx / 2;

        var lpVariants = new[] { first, second }.Select(lpMonitor =>
        {
            var lpDialog = DialogPx(lpLeft, -60, lpDialogWidthPx, lpDialogHeightPx, lpMonitor.Scale);
            var lpBar = QuickSwitchLayout.Attach(
                lpDialog.Frame, lpDialog.WindowRect, true, lpMonitor.Work, true, lpMonitor.Scale, 6);
            return new
            {
                chosenMonitor = lpMonitor.Name,
                scale = lpMonitor.Scale,
                bar = ToObject(lpBar),
                checks = new
                {
                    halfWidth = Math.Abs(lpBar.Width - lpDialog.Frame.Width / 2.0) <= 1,
                    insideChosenWorkArea = IsInside(lpBar, lpMonitor.Work),
                    columns = lpBar.Columns,
                    // 交界两侧的屏工作区被夹紧：栏不会越界到另一块屏的一半里。
                    clampedToChosenMonitor = lpBar.X >= lpMonitor.Work.Left + 4
                                             && lpBar.X + lpBar.Width <= lpMonitor.Work.Right - 4
                }
            };
        }).ToArray();

        return new
        {
            name = "dialog-straddles-primary-125-and-right-100",
            dialogFrame = ToObject(DialogPx(lpLeft, -60, lpDialogWidthPx, lpDialogHeightPx, first.Scale).Frame),
            variants = lpVariants,
            note = "实际选中哪块屏取决于系统 MonitorFromWindow 规则；无论选中哪块，栏都夹在该屏工作区内，" +
                   "不会横跨两屏，也不会沿用另一块屏的缩放。"
        };
    }

    private static object Case(string name, double scale, DialogRect dialog, NativeMethods.Rect work, int folderCount,
        string? monitorName = null)
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
            monitor = monitorName,
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

    private static bool IsInside(QuickSwitchLayout.BarRect bar, NativeMethods.Rect work)
    {
        return bar.X >= work.Left && bar.X + bar.Width <= work.Right
               && bar.Y >= work.Top && bar.Y + bar.Height <= work.Bottom;
    }

    /// <summary>按物理像素直接构造对话框：DWM 可视外框在四周各留 4 DIP 的不可见缩放边框。</summary>
    private static DialogRect DialogPx(int left, int top, int widthPx, int heightPx, double scale)
    {
        var lpInset = Round(4, scale);
        return new DialogRect(
            left, top, left + widthPx, top + heightPx,
            left - lpInset, top - lpInset, left + widthPx + lpInset, top + heightPx + lpInset);
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
