namespace Pathleap.UI.Platform;

/// <summary>
/// 主搜索窗几何：宽度取光标所在显示器工作区宽度的一半，顶边固定在工作区高度 1/4 处，水平居中。
/// 宽度按 DIP 计算后再一次性换算成物理像素，避免高 DPI 下逐次取整造成宽度抖动；
/// 工作区过窄时退到 <see cref="MinWidthDip"/> 下限，并保证不超过工作区宽度。
/// </summary>
internal static class MainWindowLayout
{
    /// <summary>宽度占工作区宽度的比例。</summary>
    public const double WidthFraction = 0.5;

    /// <summary>顶边相对工作区高度的位置。</summary>
    public const double TopFraction = 0.25;

    /// <summary>窄屏下限：低于该 DIP 宽度时不再按一半收缩，保证结果行仍能放下图标、名称与路径。</summary>
    public const double MinWidthDip = 560;

    public readonly record struct Frame(int X, int Y, int Width, int Height);

    /// <summary>
    /// 按工作区物理边界与目标缩放计算窗口矩形（物理像素）。
    /// </summary>
    /// <param name="workLeft">工作区左边（物理像素）。</param>
    /// <param name="workTop">工作区上边（物理像素）。</param>
    /// <param name="workRight">工作区右边（物理像素）。</param>
    /// <param name="workBottom">工作区下边（物理像素）。</param>
    /// <param name="scale">该显示器有效缩放（DPI / 96）。</param>
    /// <param name="heightDip">窗口高度（DIP），本次不改。</param>
    public static Frame Compute(int workLeft, int workTop, int workRight, int workBottom, double scale, double heightDip)
    {
        var lpWorkWidth = workRight - workLeft;
        var lpWorkHeight = workBottom - workTop;
        var lpWorkWidthDip = lpWorkWidth / scale;
        var lpWidthDip = Math.Min(lpWorkWidthDip, Math.Max(MinWidthDip, lpWorkWidthDip * WidthFraction));

        var lpWidth = (int)Math.Round(lpWidthDip * scale);
        var lpHeight = (int)Math.Round(heightDip * scale);
        var lpX = workLeft + (lpWorkWidth - lpWidth) / 2;
        var lpY = workTop + (int)Math.Round(lpWorkHeight * TopFraction);
        return new Frame(lpX, lpY, lpWidth, lpHeight);
    }
}
