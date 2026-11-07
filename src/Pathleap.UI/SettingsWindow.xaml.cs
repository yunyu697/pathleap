using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pathleap.Shared;
using Pathleap.Shared.Config;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Ipc;
using Pathleap.UI.Platform;
using Pathleap.UI.Theme;
using Windows.Graphics;
using Windows.UI;

namespace Pathleap.UI;

/// <summary>
/// 设置窗（设计稿屏 3）：左侧分类、右侧内容，四页——外观与主题、快捷键、搜索范围与索引、更新。
/// 主题/强调色/不透明度/磨砂/密度五项都是真实控件，改动立即作用到主窗口并写回 config.json。
/// 未实现的搜索范围项与快捷键组合在界面上明确标注「未接入」，不放假开关。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly AppConfig _lpConfig;
    private readonly ThemeService _lpTheme = ThemeService.Instance;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _lpIndexTimer;
    private bool _lpIndexBusy;
    private string _lpCurrentPage = "appearance";

    public event Action? UpdateRequested;

    public SettingsWindow(AppConfig config)
    {
        _lpConfig = config;
        InitializeComponent();

        // 打开时不要在首个控件上留下键盘焦点框：把焦点放到 1x1 的隐藏接管点。
        var lpKeySink = new Button { Width = 1, Height = 1, Opacity = 0 };
        RootGrid.Children.Add(lpKeySink);
        RootGrid.Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => lpKeySink.Focus(FocusState.Programmatic));

        Title = $"设置 · {Branding.DisplayName}";
        BrandMarkText.Text = Branding.Mark;
        ProductNameText.Text = Branding.DisplayName;
        NavVersionText.Text = $"版本 {Branding.Version}";
        VersionMarkText.Text = Branding.Mark;
        VersionLineText.Text = $"版本 {Branding.Version}";
        BuildVersionDot();

        ConfigureWindow();
        ApplyTheme();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(CaptionBar);

        OpacitySlider.Value = _lpTheme.WindowOpacity;
        OpacityValueText.Text = $"{_lpTheme.WindowOpacity * 100:0}%";
        AcrylicSwitch.IsOn = _lpTheme.AcrylicEnabled;
        AutoCheckSwitch.IsOn = _lpConfig.AutoCheckUpdates;
        FrequencyText.Text = FrequencyLabel(_lpConfig.UpdateCheckHours);
        LastCheckText.Text = FormatLastCheck(_lpConfig.LastUpdateCheck);

        ThemeService.Instance.Changed += OnThemeChanged;
        UpdateNavVisuals();
        UpdateAccentVisuals();
        UpdateThemeCardVisuals();
        UpdateDensityVisuals();
        ShowPage("appearance");
    }

    private void ConfigureWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter lpPresenter)
        {
            lpPresenter.IsResizable = false;
            lpPresenter.IsMaximizable = false;
            lpPresenter.IsMinimizable = true;
        }

        AppWindow.IsShownInSwitchers = true;
        ResizeAndCenter(840, 640);
    }

    private void ResizeAndCenter(int width, int height)
    {
        try
        {
            var lpScale = DpiUtil.Scale(this);
            var lpWidth = (int)Math.Round(width * lpScale);
            var lpHeight = (int)Math.Round(height * lpScale);
            var lpWork = DisplayArea.Primary.WorkArea;
            var lpX = lpWork.X + (lpWork.Width - lpWidth) / 2;
            var lpY = lpWork.Y + (lpWork.Height - lpHeight) / 2;
            AppWindow.MoveAndResize(new RectInt32(lpX, lpY, lpWidth, lpHeight));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置窗定位失败: {ex.Message}");
        }
    }

    private void BuildVersionDot()
    {
        var lpGradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1)
        };
        lpGradient.GradientStops.Add(new GradientStop { Color = _lpTheme.Accent, Offset = 0 });
        lpGradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 122, 92, 255), Offset = 1 });
        VersionDot.Background = lpGradient;
        BrandDot.Background = lpGradient;
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _lpTheme.ElementTheme;
        RootGrid.Background = _lpTheme.SurfaceBrush;
        Platform.WindowChrome.Apply(this, _lpTheme.WindowBorderColor, _lpTheme.IsDark);
        RootGrid.Resources["AccentBrush"] = _lpTheme.AccentBrush;
        BuildVersionDot();
        BuildHotkeyKeys();
        UpdateNavVisuals();
        UpdateThemeCardVisuals();
        UpdateAccentVisuals();
        UpdateDensityVisuals();
    }

    /// <summary>
    /// 按 config.json 的 uiHotkey（默认 Ctrl+Alt+Space）渲染键帽，
    /// 不再显示设计稿里占位用的「Ctrl Ctrl 连续两次」。
    /// </summary>
    private void BuildHotkeyKeys()
    {
        UiHotkeyKeys.Children.Clear();
        var lpHotkey = string.IsNullOrWhiteSpace(_lpConfig.UiHotkey) ? "Ctrl+Alt+Space" : _lpConfig.UiHotkey;
        var lpParts = lpHotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var lpIndex = 0; lpIndex < lpParts.Length; lpIndex++)
        {
            if (lpIndex > 0)
            {
                UiHotkeyKeys.Children.Add(new TextBlock
                {
                    Text = "+",
                    FontSize = 10,
                    Foreground = _lpTheme.Text4Brush,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            UiHotkeyKeys.Children.Add(new Border
            {
                Style = (Style)RootGrid.Resources["LpKbdStyle"],
                Child = new TextBlock
                {
                    Text = lpParts[lpIndex],
                    FontSize = 9.5,
                    Foreground = _lpTheme.Text2Brush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }
    }

    private void OnThemeChanged()
    {
        ApplyTheme();
    }

    private void ShowPage(string page)
    {
        _lpCurrentPage = page;
        PageAppearance.Visibility = page == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        PageShortcuts.Visibility = page == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        PageScope.Visibility = page == "scope" ? Visibility.Visible : Visibility.Collapsed;
        PageUpdate.Visibility = page == "update" ? Visibility.Visible : Visibility.Collapsed;

        UpdateNavVisuals();
        ResizeAndCenter(page is "shortcuts" or "update" ? 550 : 840, 640);
        ContentScroller.ChangeView(null, 0, null, disableAnimation: true);

        if (page == "scope")
        {
            _ = RefreshIndexAsync();
        }

        AppLog.Debug($"设置页切换: {page}");
    }

    /// <summary>供托盘菜单与截图模式切页。</summary>
    internal void ShowPageForTest(string page) => ShowPage(page);

    private void UpdateNavVisuals()
    {
        ApplyNav(NavAppearance, _lpCurrentPage == "appearance");
        ApplyNav(NavShortcuts, _lpCurrentPage == "shortcuts");
        ApplyNav(NavScope, _lpCurrentPage == "scope");
        ApplyNav(NavUpdate, _lpCurrentPage == "update");
    }

    private void ApplyNav(Button button, bool selected)
    {
        button.Background = selected ? _lpTheme.NavSelectedBrush : _lpTheme.TransparentBrush;
        button.Foreground = selected ? _lpTheme.Text1Brush : _lpTheme.Text2Brush;
        foreach (var child in GetNavTexts(button))
        {
            child.Foreground = selected ? _lpTheme.Text1Brush : _lpTheme.Text2Brush;
        }
    }

    private static IEnumerable<TextBlock> GetNavTexts(Button button)
    {
        if (button.Content is StackPanel lpPanel)
        {
            foreach (var lpChild in lpPanel.Children)
            {
                if (lpChild is TextBlock lpText)
                {
                    yield return lpText;
                }
            }
        }
    }

    private void UpdateThemeCardVisuals()
    {
        var lpSelected = _lpTheme.Mode;
        SetThemeCard(ThemeLightCard, ThemeLightRadio, lpSelected == LpThemeMode.Light);
        SetThemeCard(ThemeDarkCard, ThemeDarkRadio, lpSelected == LpThemeMode.Dark);
        SetThemeCard(ThemeSystemCard, ThemeSystemRadio, lpSelected == LpThemeMode.System);
    }

    private void SetThemeCard(Border card, Microsoft.UI.Xaml.Shapes.Ellipse radio, bool selected)
    {
        card.BorderBrush = selected ? _lpTheme.AccentBrush : _lpTheme.Border1Brush;
        radio.Fill = selected ? _lpTheme.AccentBrush : _lpTheme.TransparentBrush;
        radio.Stroke = selected ? _lpTheme.AccentBrush : _lpTheme.TrackOffBorderBrush;
    }

    private void UpdateAccentVisuals()
    {
        var lpSelectedHex = $"#{_lpTheme.Accent.R:X2}{_lpTheme.Accent.G:X2}{_lpTheme.Accent.B:X2}";
        foreach (var lpSwatch in new[] { AccentBlue, AccentPurple, AccentGreen, AccentAmber, AccentRed })
        {
            var lpIsSelected = string.Equals((string)lpSwatch.Tag, lpSelectedHex, StringComparison.OrdinalIgnoreCase);
            lpSwatch.Opacity = lpIsSelected ? 1.0 : 0.45;
        }
    }

    private void UpdateDensityVisuals()
    {
        var lpDensity = _lpTheme.Density;
        SetDensityChip(DensityCompact, lpDensity == LpDensity.Compact);
        SetDensityChip(DensityStandard, lpDensity == LpDensity.Standard);
        SetDensityChip(DensityComfortable, lpDensity == LpDensity.Comfortable);
    }

    private void SetDensityChip(Border chip, bool selected)
    {
        chip.Background = selected ? _lpTheme.NavSelectedBrush : _lpTheme.TransparentBrush;
        if (chip.Child is TextBlock lpText)
        {
            lpText.Foreground = selected ? _lpTheme.Text1Brush : _lpTheme.Text2Brush;
        }
    }

    private static string FrequencyLabel(int hours) => hours switch
    {
        0 => "仅手动",
        168 => "每周",
        _ => "每天"
    };

    private static string FormatLastCheck(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            return "上次检查：尚未检查";
        }

        try
        {
            var lpTime = DateTimeOffset.Parse(iso);
            return $"上次检查：{lpTime:MM-dd HH:mm}";
        }
        catch
        {
            return "上次检查：尚未检查";
        }
    }

    private void OnNavAppearanceClick(object sender, RoutedEventArgs e) => ShowPage("appearance");

    private void OnNavShortcutsClick(object sender, RoutedEventArgs e) => ShowPage("shortcuts");

    private void OnNavScopeClick(object sender, RoutedEventArgs e) => ShowPage("scope");

    private void OnNavUpdateClick(object sender, RoutedEventArgs e) => ShowPage("update");

    private void OnThemeLightTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        _lpTheme.SetMode(LpThemeMode.Light);
    }

    private void OnThemeDarkTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        _lpTheme.SetMode(LpThemeMode.Dark);
    }

    private void OnThemeSystemTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        _lpTheme.SetMode(LpThemeMode.System);
    }

    private void OnAccentTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not Border lpSwatch || lpSwatch.Tag is not string lpHex || lpHex.Length != 7)
        {
            return;
        }

        var lpColor = Color.FromArgb(
            255,
            Convert.ToByte(lpHex.Substring(1, 2), 16),
            Convert.ToByte(lpHex.Substring(3, 2), 16),
            Convert.ToByte(lpHex.Substring(5, 2), 16));
        _lpTheme.SetAccent(lpColor);
    }

    private void OnOpacityChanged(object? sender, EventArgs e)
    {
        var lpValue = OpacitySlider.Value;
        OpacityValueText.Text = $"{lpValue * 100:0}%";
        _lpTheme.SetOpacity(lpValue);
    }

    private void OnAcrylicToggled(object? sender, EventArgs e)
    {
        _lpTheme.SetAcrylic(AcrylicSwitch.IsOn);
    }

    private void OnDensityTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not Border lpChip || lpChip.Tag is not string lpTag)
        {
            return;
        }

        _lpTheme.SetDensity(lpTag switch
        {
            "Compact" => LpDensity.Compact,
            "Comfortable" => LpDensity.Comfortable,
            _ => LpDensity.Standard
        });
    }

    private void OnAutoCheckToggled(object? sender, EventArgs e)
    {
        _lpConfig.AutoCheckUpdates = AutoCheckSwitch.IsOn;
        _lpTheme.SaveConfig();
    }

    private void OnFrequencyDailyClick(object sender, RoutedEventArgs e) => SetFrequency(24);

    private void OnFrequencyWeeklyClick(object sender, RoutedEventArgs e) => SetFrequency(168);

    private void OnFrequencyManualClick(object sender, RoutedEventArgs e) => SetFrequency(0);

    private void SetFrequency(int hours)
    {
        _lpConfig.UpdateCheckHours = hours;
        _lpTheme.SaveConfig();
        FrequencyText.Text = FrequencyLabel(hours);
    }

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        UpdateRequested?.Invoke();
    }

    private async void OnRebuildClick(object sender, RoutedEventArgs e)
    {
        if (_lpIndexBusy)
        {
            return;
        }

        var lpDialog = new ContentDialog
        {
            Title = "重建索引",
            Content = "将从默认系统卷（C:\\）重新扫描，USN 需要管理员或服务身份；没有权限时自动降级为目录遍历。" +
                      "耗时可能从数十秒到数分钟，期间旧索引仍可搜索。",
            PrimaryButtonText = "开始重建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        if (await lpDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _lpIndexBusy = true;
        IndexFootText.Text = "正在请求服务重建索引…";
        RebuildButton.IsEnabled = false;
        try
        {
            var lpReply = await PipeClient.SendAsync(
                Branding.ServicePipeName,
                new IpcRequest { Command = "INDEX_BUILD", Root = string.Empty, Mode = "auto" },
                TimeSpan.FromSeconds(10));
            IndexFootText.Text = lpReply.Ok ? "重建请求已受理，正在扫描…" : $"重建失败：{lpReply.Message}";
            StartIndexPolling();
        }
        catch (Exception ex)
        {
            IndexFootText.Text = $"重建失败：{ex.Message}";
            _lpIndexBusy = false;
            RebuildButton.IsEnabled = true;
        }
    }

    private void StartIndexPolling()
    {
        _lpIndexTimer ??= DispatcherQueue.CreateTimer();
        _lpIndexTimer.Interval = TimeSpan.FromSeconds(1);
        _lpIndexTimer.IsRepeating = true;
        _lpIndexTimer.Tick += async (_, _) => await RefreshIndexAsync();
        _lpIndexTimer.Start();
    }

    private async Task RefreshIndexAsync()
    {
        try
        {
            var lpReply = await PipeClient.SendAsync(
                Branding.ServicePipeName,
                new IpcRequest { Command = "INDEX_STATUS" },
                TimeSpan.FromSeconds(3));
            var lpStatus = lpReply.Index;
            if (!lpReply.Ok || lpStatus is null)
            {
                StatStateText.Text = "未连接";
                IndexFootText.Text = $"读取索引状态失败：{lpReply.Message}";
                ScopeLocalDetail.Text = "后台索引服务未连接";
                return;
            }

            StatEntriesText.Text = FormatCount(lpStatus.EntryCount);
            StatSizeText.Text = FormatBytes(lpStatus.FileBytes);
            StatStateText.Text = lpStatus.State switch
            {
                "ready" => "就绪",
                "building" => "构建中",
                _ => "未建"
            };

            ScopeLocalDetail.Text = lpStatus.Loaded
                ? $"由后台服务提供 · {FormatCount(lpStatus.EntryCount)} 项"
                : "由后台服务提供 · 索引尚未建立";

            IndexFootText.Text = lpStatus.Loaded
                ? $"来源 {DescribeSource(lpStatus.Source)} · 载入 {lpStatus.LoadMs:0} ms · {lpStatus.Note}"
                : "索引尚未建立；可点击右侧「重建索引」开始扫描。";

            IndexProgressFill.Width = lpStatus.State == "building"
                ? Math.Max(28, RootGrid.ActualWidth * 0.04)
                : 0;

            BuildIndexChips(lpStatus);
            BuildExcludeChips();

            if (lpStatus.State != "building" && _lpIndexBusy)
            {
                _lpIndexBusy = false;
                RebuildButton.IsEnabled = true;
                _lpIndexTimer?.Stop();
            }
        }
        catch (Exception ex)
        {
            StatStateText.Text = "未连接";
            IndexFootText.Text = $"索引服务未连接：{ex.Message}";
        }
    }

    private void BuildIndexChips(IndexStatusDto status)
    {
        IndexChipsPanel.Children.Clear();
        IndexChipsPanel.Children.Add(BuildChip("\uE8B7", DescribeSource(status.Source), false));
        if (!string.IsNullOrWhiteSpace(status.FilePath))
        {
            IndexChipsPanel.Children.Add(BuildChip("\uE7C3", "索引文件 index.v4.db", false));
        }
    }

    private static string DescribeSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "索引来源未报告";
        }

        if (source.StartsWith("synthetic", StringComparison.OrdinalIgnoreCase))
        {
            return "合成索引（性能测试数据）";
        }

        return source == "usn" ? "USN 全量索引" : source == "walk" ? "目录遍历索引" : source;
    }

    private void BuildExcludeChips()
    {
        ExcludeChipsPanel.Children.Clear();
        ExcludeChipsPanel.Children.Add(BuildChip("\uE711", "排除规则（未接入）", true));
    }

    private Border BuildChip(string glyph, string text, bool dashed)
    {
        var lpIcon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 11,
            Foreground = _lpTheme.Text2Brush
        };
        var lpText = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            Foreground = _lpTheme.Text2Brush
        };
        var lpPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        lpPanel.Children.Add(lpIcon);
        lpPanel.Children.Add(lpText);

        var lpBorder = new Border
        {
            Background = _lpTheme.TransparentBrush,
            BorderBrush = dashed ? _lpTheme.TrackOffBorderBrush : _lpTheme.Border1Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 5, 9, 5),
            Child = lpPanel,
            Opacity = dashed ? 0.75 : 1.0
        };
        return lpBorder;
    }

    private static string FormatCount(int count) => count >= 10000
        ? $"{count / 10000.0:0.0} 万"
        : count.ToString("N0");

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.0} MB"
    };

    public void PrepareShutdown()
    {
        ThemeService.Instance.Changed -= OnThemeChanged;
        _lpIndexTimer?.Stop();
        _lpIndexTimer = null;
    }
}
