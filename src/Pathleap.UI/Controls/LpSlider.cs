using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pathleap.UI.Theme;

namespace Pathleap.UI.Controls;

/// <summary>设计稿样式的细轨滑块（3px 轨道 + 12px 圆点），数值 0–1。</summary>
public sealed partial class LpSlider : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(LpSlider),
        new PropertyMetadata(0.92, OnValueChanged));

    private bool _lpDragging;

    public LpSlider()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Apply();
        Loaded += (_, _) =>
        {
            ThemeService.Instance.Changed += Apply;
            Apply();
        };
        Unloaded += (_, _) => ThemeService.Instance.Changed -= Apply;
    }

    public event EventHandler? ValueChanged;

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((LpSlider)d).Apply();
    }

    private void Apply()
    {
        var lpValue = Math.Clamp(Value, 0, 1);
        var lpWidth = Root.ActualWidth;
        if (lpWidth <= 0)
        {
            return;
        }

        Fill.Width = Math.Max(0, lpWidth * lpValue);
        Fill.Background = ThemeService.Instance.AccentBrush;
        Thumb.Margin = new Thickness(Math.Max(0, lpWidth * lpValue - 6), 0, 0, 0);
        Thumb.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            ThemeService.Instance.IsDark ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 255, 255, 255));
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _lpDragging = true;
        Root.CapturePointer(e.Pointer);
        UpdateFromPointer(e);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_lpDragging)
        {
            UpdateFromPointer(e);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_lpDragging)
        {
            return;
        }

        _lpDragging = false;
        Root.ReleasePointerCapture(e.Pointer);
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateFromPointer(PointerRoutedEventArgs e)
    {
        var lpPosition = e.GetCurrentPoint(Root).Position.X;
        var lpWidth = Root.ActualWidth;
        if (lpWidth <= 0)
        {
            return;
        }

        Value = Math.Clamp(lpPosition / lpWidth, 0, 1);
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}
