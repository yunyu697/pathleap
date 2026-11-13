namespace Pathleap.UI.Platform;

/// <summary>
/// 快速切换栏的纯几何计算：宽度取对话框一半、上边缘紧贴对话框下边缘（间隙 0）、水平居中、不越出工作区，
/// 下方放不下时翻到上方（同样 0 间隙）。抽成无窗口依赖的纯函数，便于在非整数缩放（125%/150%）下
/// 用 --layout-probe 逐项核对，而不必真的改系统缩放。
/// </summary>
internal static class QuickSwitchLayout
{
    public const int MaxRows = 6;
    public const double RowHeightDip = 32;

    /// <summary>搜索行高：品牌点 18、输入框 26，行高取输入框。</summary>
    public const double SearchRowDip = 26;

    /// <summary>搜索行之外的开销：上下内边距各 9 + 行间距 6。</summary>
    public const double ChromeDip = 24;

    /// <summary>宽度达到这个 DIP 值时内容改成 6 列，否则 3 列。</summary>
    public const double WideColumnsMinDip = 680;

    /// <summary>
    /// 宽度低于这个 DIP 值时改成 2 列。3 列时每格可用文字宽度只有约 56 DIP（4–5 个汉字就被省略），
    /// 2 列把单格文字提到约 100 DIP，代价是多一行（6 个文件夹时 3 行，仍在 6 行上限内）。
    /// 该阈值对应「3 列时单格文字不足约 70 DIP」：3×(70+37)+24+10 ≈ 355 DIP。
    /// </summary>
    public const double NarrowColumnsMaxDip = 360;

    /// <summary>
    /// 栏与对话框之间的视觉间距（DIP）。用户要求严丝合缝贴住下边缘，因此为 0；
    /// 位置取 DWM 可视外框（不含阴影与不可见缩放边框），而不是 GetWindowRect。
    /// </summary>
    public const double GapDip = 0;

    internal readonly record struct BarRect(int X, int Y, int Width, int Height, int Columns);

    public static int Columns(int widthPx, double scale)
    {
        var lpWide = (int)Math.Round(WideColumnsMinDip * scale);
        var lpNarrow = (int)Math.Round(NarrowColumnsMaxDip * scale);
        if (widthPx >= lpWide)
        {
            return MaxRows;
        }

        return widthPx < lpNarrow ? 2 : 3;
    }

    /// <summary>首次引导的三行文案按两行内容格的高度排，与列数无关。</summary>
    public const int GuideRows = 2;

    /// <summary>
    /// 栏高 = 搜索行 + 内容区（没有常驻标题行）。最近文件夹按网格排列（列数随宽度变化），
    /// 搜索结果按单列列表排列（<paramref name="listMode"/>），两者共用同一套行高与上限。
    /// <paramref name="guide"/> 为真时内容区固定 <see cref="GuideRows"/> 行，不随文件夹数量变化。
    /// </summary>
    public static int Height(int widthPx, double scale, int itemCount, bool listMode = false, bool guide = false)
    {
        var lpRows = guide
            ? GuideRows
            : listMode
                ? Math.Clamp(itemCount, 1, MaxRows)
                : (int)Math.Ceiling(Math.Min(MaxRows, Math.Max(1, itemCount)) / (double)Columns(widthPx, scale));
        return (int)Math.Round(
            (ChromeDip + SearchRowDip + Math.Max(1, lpRows) * RowHeightDip) * scale);
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
        int itemCount,
        bool listMode = false,
        bool guide = false)
    {
        var lpScale = Math.Max(1.0, scale);
        var lpWidth = Math.Max(1, (int)Math.Round(frame.Width * 0.5));
        var lpHeight = Height(lpWidth, lpScale, itemCount, listMode, guide);
        var lpColumns = listMode ? 1 : Columns(lpWidth, lpScale);

        var lpX = frame.Left + (frame.Width - lpWidth) / 2;

        // 只用 DWM 可视外框：GetWindowRect 会把对话框的投影阴影也算进去，
        // 按它贴边会留下一条与阴影等宽的视觉缝。
        var lpBottom = frame.Bottom;
        var lpTop = frame.Top;
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
