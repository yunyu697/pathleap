using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Pathleap.UI.Theme;
using Windows.UI.Text;

namespace Pathleap.UI.Controls;

/// <summary>
/// 文件名的高亮文本：命中片段使用强调色派生的底色与前色（TextHighlighter），
/// 与设计稿的 mark 片段一致。颜色随 ThemeService 变化实时刷新。
/// </summary>
public sealed partial class LpHighlightText : UserControl
{
    public static readonly DependencyProperty RichTextProperty = DependencyProperty.Register(
        nameof(RichText),
        typeof(string),
        typeof(LpHighlightText),
        new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(
        nameof(Query),
        typeof(string),
        typeof(LpHighlightText),
        new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty LabelFontSizeProperty = DependencyProperty.Register(
        nameof(LabelFontSize),
        typeof(double),
        typeof(LpHighlightText),
        new PropertyMetadata(13.5, OnStyleChanged));

    public static readonly DependencyProperty LabelFontWeightProperty = DependencyProperty.Register(
        nameof(LabelFontWeight),
        typeof(FontWeight),
        typeof(LpHighlightText),
        new PropertyMetadata(Microsoft.UI.Text.FontWeights.Normal, OnStyleChanged));

    public LpHighlightText()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string RichText
    {
        get => (string)GetValue(RichTextProperty);
        set => SetValue(RichTextProperty, value);
    }

    public string Query
    {
        get => (string)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public double LabelFontSize
    {
        get => (double)GetValue(LabelFontSizeProperty);
        set => SetValue(LabelFontSizeProperty, value);
    }

    public FontWeight LabelFontWeight
    {
        get => (FontWeight)GetValue(LabelFontWeightProperty);
        set => SetValue(LabelFontWeightProperty, value);
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((LpHighlightText)d).Rebuild();
    }

    private static void OnStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var lpControl = (LpHighlightText)d;
        lpControl.Label.FontSize = lpControl.LabelFontSize;
        lpControl.Label.FontWeight = lpControl.LabelFontWeight;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeService.Instance.Changed -= OnThemeChanged;
        ThemeService.Instance.Changed += OnThemeChanged;
        Label.FontSize = LabelFontSize;
        Label.FontWeight = LabelFontWeight;
        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ThemeService.Instance.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        Rebuild();
    }

    private void Rebuild()
    {
        var lpText = RichText ?? string.Empty;
        Label.Text = lpText;
        Label.TextHighlighters.Clear();

        var lpQuery = (Query ?? string.Empty).Trim();
        if (lpQuery.Length == 0 || lpQuery.Length > lpText.Length)
        {
            return;
        }

        var lpHighlighter = new TextHighlighter
        {
            Background = ThemeService.Instance.MarkBackgroundBrush,
            Foreground = ThemeService.Instance.MarkForegroundBrush
        };
        var lpHaystack = lpText.ToLowerInvariant();
        var lpNeedle = lpQuery.ToLowerInvariant();
        var lpOffset = 0;
        while (lpOffset < lpText.Length)
        {
            var lpIndex = lpHaystack.IndexOf(lpNeedle, lpOffset, StringComparison.Ordinal);
            if (lpIndex < 0)
            {
                break;
            }

            lpHighlighter.Ranges.Add(new TextRange(lpIndex, lpIndex + lpQuery.Length));
            lpOffset = lpIndex + lpQuery.Length;
        }

        if (lpHighlighter.Ranges.Count > 0)
        {
            Label.TextHighlighters.Add(lpHighlighter);
        }
    }
}
