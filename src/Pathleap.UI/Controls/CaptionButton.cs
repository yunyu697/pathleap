using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Pathleap.UI.Theme;
using Windows.Foundation;

namespace Pathleap.UI.Controls;

public enum CaptionGlyphKind
{
    Minimize,
    Maximize,
    Close
}

/// <summary>
/// 标题栏按钮。所有按钮共用同一套几何基准：同一 12×12 坐标系、1 DIP 笔画、平头端点，
/// 字形只按 <see cref="Kind"/> 换折线，不换笔画宽度和端点样式。
/// 颜色全部走当前配色方案的 token：常态字形所有按钮同源，悬停/按下只改背景与字形亮度。
/// 关闭红是一份独立定义的 token，只在 <see cref="IsClose"/> 按钮的悬停/按下态出现。
/// 自绘的直接原因是系统非客户区按钮的字形颜色跟随操作系统明暗而非应用主题，
/// 且无法按按钮控制笔画与悬停色，会和自绘的关闭按钮混成两种来源。
/// </summary>
public sealed class CaptionButton : Button
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(CaptionGlyphKind),
        typeof(CaptionButton),
        new PropertyMetadata(CaptionGlyphKind.Minimize, OnGlyphChanged));

    public static readonly DependencyProperty IsCloseProperty = DependencyProperty.Register(
        nameof(IsClose),
        typeof(bool),
        typeof(CaptionButton),
        new PropertyMetadata(false, OnGlyphChanged));

    private readonly Microsoft.UI.Xaml.Shapes.Path _lpIcon;
    private bool _lpHover;
    private bool _lpPressed;

    public CaptionButton()
    {
        _lpIcon = new Microsoft.UI.Xaml.Shapes.Path
        {
            StrokeThickness = 1,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Content = _lpIcon;

        Loaded += (_, _) =>
        {
            ThemeService.Instance.Changed += ApplyColors;
            ApplyColors();
            // XAML 里写的 IsEnabled 在构造期间不会触发 IsEnabledChanged，Loaded 时补一次禁用态。
            ApplyOpacity();
        };
        Unloaded += (_, _) => ThemeService.Instance.Changed -= ApplyColors;
        IsEnabledChanged += (_, _) => ApplyOpacity();

        PointerEntered += (_, _) =>
        {
            _lpHover = true;
            ApplyColors();
        };
        PointerExited += (_, _) =>
        {
            _lpHover = false;
            _lpPressed = false;
            ApplyColors();
        };
        PointerPressed += (_, _) =>
        {
            _lpPressed = true;
            ApplyColors();
        };
        PointerReleased += (_, _) =>
        {
            _lpPressed = false;
            ApplyColors();
        };
        PointerCaptureLost += (_, _) =>
        {
            _lpPressed = false;
            ApplyColors();
        };

        ApplyGlyph();
        ApplyColors();
        ApplyOpacity();
    }

    public CaptionGlyphKind Kind
    {
        get => (CaptionGlyphKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool IsClose
    {
        get => (bool)GetValue(IsCloseProperty);
        set => SetValue(IsCloseProperty, value);
    }

    private static void OnGlyphChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var lpButton = (CaptionButton)sender;
        lpButton.ApplyGlyph();
        lpButton.ApplyColors();
    }

    private void ApplyGlyph()
    {
        _lpIcon.Data = BuildGlyph(Kind);
    }

    private void ApplyColors()
    {
        var lpTheme = ThemeService.Instance;
        var lpActive = _lpHover || _lpPressed;

        if (IsClose)
        {
            _lpIcon.Stroke = lpActive ? lpTheme.CaptionCloseInkBrush : lpTheme.CaptionGlyphBrush;
            Background = _lpPressed
                ? lpTheme.CaptionClosePressBrush
                : (_lpHover ? lpTheme.CaptionCloseHoverBrush : lpTheme.TransparentBrush);
            return;
        }

        _lpIcon.Stroke = lpActive ? lpTheme.CaptionGlyphHoverBrush : lpTheme.CaptionGlyphBrush;
        Background = _lpPressed
            ? lpTheme.CaptionPressBrush
            : (_lpHover ? lpTheme.CaptionHoverBrush : lpTheme.TransparentBrush);
    }

    private void ApplyOpacity()
    {
        Opacity = IsEnabled ? 1.0 : 0.38;
    }

    /// <summary>
    /// 字形都落在 12×12 坐标系里，笔画 1 DIP、平头端点。外廓统一为 1.0～11.0 DIP（10 DIP 见方），
    /// 最小化横线也用满这 10 DIP 宽度，所有按钮的视觉重量一致；Path 元素按几何外包自动居中。
    /// </summary>
    private static Geometry BuildGlyph(CaptionGlyphKind kind)
    {
        var lpGeometry = new PathGeometry();
        switch (kind)
        {
            case CaptionGlyphKind.Close:
                lpGeometry.Figures.Add(Figure((1.5, 1.5), (10.5, 10.5)));
                lpGeometry.Figures.Add(Figure((10.5, 1.5), (1.5, 10.5)));
                break;
            case CaptionGlyphKind.Maximize:
                lpGeometry.Figures.Add(Figure((1.5, 1.5), (10.5, 1.5), (10.5, 10.5), (1.5, 10.5), (1.5, 1.5)));
                break;
            default:
                // 平头端点不外扩，横线端点直接取 1.0 / 11.0，宽度与另外两个字形对齐为 10 DIP。
                lpGeometry.Figures.Add(Figure((1.0, 6.0), (11.0, 6.0)));
                break;
        }

        return lpGeometry;
    }

    private static PathFigure Figure(params (double X, double Y)[] points)
    {
        var lpFigure = new PathFigure
        {
            StartPoint = new Point(points[0].X, points[0].Y),
            IsClosed = false,
            IsFilled = false
        };
        for (var lpIndex = 1; lpIndex < points.Length; lpIndex++)
        {
            lpFigure.Segments.Add(new LineSegment { Point = new Point(points[lpIndex].X, points[lpIndex].Y) });
        }

        return lpFigure;
    }
}
