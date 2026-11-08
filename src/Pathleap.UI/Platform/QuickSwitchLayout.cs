namespace Pathleap.UI.Platform;

/// <summary>
/// 快速切换栏的纯几何计算：宽度取对话框一半、贴在底边外侧 2 DIP、水平居中、不越出工作区，
/// 下方放不下时翻到上方。抽成无窗口依赖的纯函数，便于在非整数缩放（125%/150%）下
/// 用 --layout-probe 逐项核对，而不必真的改系统缩放。
/// </summary>
internal static class QuickSwitchLayout
{
    public const int MaxRows = 6;
    public const double RowHeightDip = 32;
    public const double HeaderHeightDip = 34;
    public const double BodyPaddingDip = 12;

    /// <summary>宽度达到这个 DIP 值时内容改成 6 列，否则 3 列。</summary>
    public const double WideColumnsMinDip = 680;

    /// <summary>栏与对话框之间的视觉间距（DIP）。</summary>
    public const double GapDip = 2;

    internal readonly record struct BarRect(int X, int Y, int Width, int Height, int Columns);

    public static int Columns(int widthPx, double scale)
    {
        return widthPx >= (int)Math.Round(WideColumnsMinDip * scale) ? MaxRows : 3;
    }

    public static int Height(int widthPx, double scale, int folderCount)
    {
        var lpColumns = Columns(widthPx, scale);
        var lpRows = (int)Math.Ceiling(Math.Min(MaxRows, Math.Max(1, folderCount)) / (double)lpColumns);
        return (int)Math.Round((HeaderHeightDip + Math.Max(1, lpRows) * RowHeightDip + BodyPaddingDip) * scale);
    }

    /// <summary>
    /// 计算吸附栏的位置与尺寸。frame 是 DWM 可视外框，windowRect 是窗口矩形（含不可见缩放边框）；
    /// work 是对话框所在显示器的工作区。三者都是物理像素，scale 是该显示器的有效缩放。
    /// </summary>
    public static BarRect Attach(
        ScreenBounds frame,
        NativeMethods.Rect windowRect,
        bool hasWindowRect,
        NativeMethods.Rect work,
        bool hasWork,
        double scale,
        int folderCount)
    {
        var lpScale = Math.Max(1.0, scale);
        var lpWidth = Math.Max(1, (int)Math.Round(frame.Width * 0.5));
        var lpHeight = Height(lpWidth, lpScale, folderCount);
        var lpColumns = Columns(lpWidth, lpScale);

        var lpX = frame.Left + (frame.Width - lpWidth) / 2;

        var lpBottom = hasWindowRect ? Math.Max(frame.Bottom, windowRect.Bottom) : frame.Bottom;
        var lpTop = hasWindowRect ? Math.Min(frame.Top, windowRect.Top) : frame.Top;
        var lpY = lpBottom + (int)Math.Round(GapDip * lpScale);

        if (hasWork)
        {
            lpX = Math.Clamp(lpX, work.Left + 4, Math.Max(work.Left + 4, work.Right - 4 - lpWidth));
            if (lpY + lpHeight > work.Bottom)
            {
                lpY = lpTop - lpHeight - (int)Math.Round(GapDip * lpScale);
            }

            lpY = Math.Max(lpY, work.Top + 4);
        }

        return new BarRect(lpX, lpY, lpWidth, lpHeight, lpColumns);
    }
}
