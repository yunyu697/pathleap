using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    private bool _lpSyncingElevation;
    private bool _lpSyncingScopeApps;
    private bool _lpSyncingScopeBrowser;
    private bool _lpSyncingNetwork;
    private bool _lpElevationBusy;

    public event Action? UpdateRequested;

    /// <summary>用户选中某项预置唤醒方式；宿主负责实际重注册，完成后回调 <see cref="ReportHotkeyResult"/>。</summary>
    internal event Action<HotkeySpec>? HotkeyRequested;

    private readonly List<(Button Row, HotkeySpec Spec)> _lpHotkeyRows = new();
    private static readonly SolidColorBrush FailureBrush = new(Color.FromArgb(255, 226, 132, 90));

    public SettingsWindow(AppConfig config)
    {
        _lpConfig = config;
        InitializeComponent();

        // 打开时不要在首个控件上留下键盘焦点框：把焦点放到 1x1 的隐藏接管点。
        var lpKeySink = new Button { Width = 1, Height = 1, Opacity = 0 };
        RootGrid.Children.Add(lpKeySink);
        RootGrid.Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => lpKeySink.Focus(FocusState.Programmatic));

        Title = $"设置 · {Branding.DisplayName}";
        ProductNameText.Text = Branding.DisplayName;
        NavVersionText.Text = $"版本 {Branding.Version}";
        VersionLineText.Text = $"版本 {Branding.Version}";
        BuildVersionDot();

        ConfigureWindow();
        ApplyTheme();

        OpacitySlider.Value = _lpTheme.WindowOpacity;
        OpacityValueText.Text = $"{_lpTheme.WindowOpacity * 100:0}%";
        AcrylicSwitch.IsOn = _lpTheme.AcrylicEnabled;
        AutoCheckSwitch.IsOn = _lpConfig.AutoCheckUpdates;
        FrequencyText.Text = FrequencyLabel(_lpConfig.UpdateCheckHours);
        LastCheckText.Text = FormatLastCheck(_lpConfig.LastUpdateCheck);

        _lpSyncingElevation = true;
        ElevationSwitch.IsOn = _lpConfig.AlwaysRunAsAdmin;
        _lpSyncingElevation = false;
        _lpSyncingScopeApps = true;
        ScopeAppsSwitch.IsOn = _lpConfig.IncludeInstalledPrograms;
        _lpSyncingScopeApps = false;
        _lpSyncingScopeBrowser = true;
        ScopeBrowserSwitch.IsOn = _lpConfig.IncludeBrowserHistory;
        _lpSyncingScopeBrowser = false;
        _lpSyncingNetwork = true;
        ScopeNetworkSwitch.IsOn = _lpConfig.IncludeNetworkLocations;
        _lpSyncingNetwork = false;
        NetworkPanel.Visibility = _lpConfig.IncludeNetworkLocations ? Visibility.Visible : Visibility.Collapsed;
        RefreshNetworkLocations();
        RefreshIndexDirectories();
        ApplyInstallInfo();
        RefreshElevationState();

        ThemeService.Instance.Changed += OnThemeChanged;
        UpdateNavVisuals();
        UpdateColorThemeVisuals();
        UpdateThemeCardVisuals();
        UpdateDensityVisuals();
        ShowPage("appearance");
    }

    private void ConfigureWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter lpPresenter)
        {
            // 设置窗是固定尺寸窗：不可拉伸、不可最大化，切页也不会改变窗口尺寸。
            // 最大化按钮不渲染（Windows 固定尺寸对话框的常规做法），避免留一个禁用变淡的按钮
            // 被看成“三个标题栏按钮颜色不一致”。
            lpPresenter.IsResizable = false;
            lpPresenter.IsMaximizable = false;
            lpPresenter.IsMinimizable = true;
            try
            {
                // 去掉系统标题栏与系统按钮：按钮改由 CaptionButton 自绘，避免两套按钮并存、颜色与粗细不同源。
                lpPresenter.SetBorderAndTitleBar(false, false);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"设置窗无边框设置失败: {ex.Message}");
            }
        }

        AppWindow.IsShownInSwitchers = true;
        NcFrame.Apply(this);
        CaptionMinButton.IsEnabled = AppWindow.Presenter is OverlappedPresenter { IsMinimizable: true };
        ResizeAndCenter(840, 640);
    }

    /// <summary>自绘标题栏的最小化：交给系统 presenter，按钮可用性在 ConfigureWindow 里按 presenter 能力设置。</summary>
    private void OnCaptionMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter lpPresenter)
        {
            lpPresenter.Minimize();
        }
    }

    private void OnCaptionCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>标题栏空白处拖动窗口；按在按钮上时不拦截，交给按钮自己处理点击。</summary>
    private void OnCaptionBarPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var lpSource = e.OriginalSource as DependencyObject;
        while (lpSource is not null)
        {
            if (lpSource is Controls.CaptionButton)
            {
                return;
            }

            lpSource = VisualTreeHelper.GetParent(lpSource);
        }

        Platform.WindowChrome.BeginDrag(this);
    }

    /// <summary>
    /// 固定 840×640 DIP，居中到“设置窗会出现的那块屏幕”的工作区：
    /// 目标屏幕取鼠标当前所在屏幕（托盘、主窗或热键打开时用户正在那块屏上操作），
    /// 尺寸与坐标都按该屏幕的 DPI 换算，工作区已扣掉任务栏；
    /// 若窗口比工作区还大（小屏 + 高缩放），缩到工作区并留一圈边距，页面内容由右侧 ScrollViewer 滚动。
    /// </summary>
    private void ResizeAndCenter(int width, int height)
    {
        try
        {
            var lpInfo = new NativeMethods.MonitorInfo
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
            };

            NativeMethods.Point lpCursor = default;
            var lpMonitor = NativeMethods.GetCursorPos(out lpCursor)
                ? NativeMethods.MonitorFromPoint(lpCursor, NativeMethods.MonitorDefaultToNearest)
                : IntPtr.Zero;

            var lpHasMonitor = lpMonitor != IntPtr.Zero && NativeMethods.GetMonitorInfoW(lpMonitor, ref lpInfo);
            if (!lpHasMonitor)
            {
                // 取不到鼠标所在屏幕时退回主屏工作区，与旧行为一致，避免窗口跑到屏幕外。
                var lpPrimary = DisplayArea.Primary.WorkArea;
                lpInfo.Work = new NativeMethods.Rect
                {
                    Left = lpPrimary.X,
                    Top = lpPrimary.Y,
                    Right = lpPrimary.X + lpPrimary.Width,
                    Bottom = lpPrimary.Y + lpPrimary.Height
                };
            }

            var lpScale = DpiUtil.Scale(this);
            uint lpDpiX = 0;
            uint lpDpiY = 0;
            if (lpHasMonitor &&
                NativeMethods.GetDpiForMonitor(lpMonitor, NativeMethods.MdtEffectiveDpi, out lpDpiX, out lpDpiY) == 0 &&
                lpDpiX > 0)
            {
                lpScale = lpDpiX / 96.0;
            }

            var lpWorkWidth = lpInfo.Work.Right - lpInfo.Work.Left;
            var lpWorkHeight = lpInfo.Work.Bottom - lpInfo.Work.Top;
            var lpMargin = (int)Math.Round(16 * lpScale);

            var lpWidth = Math.Min((int)Math.Round(width * lpScale), lpWorkWidth - lpMargin * 2);
            var lpHeight = Math.Min((int)Math.Round(height * lpScale), lpWorkHeight - lpMargin * 2);
            lpWidth = Math.Max(lpWidth, 1);
            lpHeight = Math.Max(lpHeight, 1);

            var lpX = lpInfo.Work.Left + (lpWorkWidth - lpWidth) / 2;
            var lpY = lpInfo.Work.Top + (lpWorkHeight - lpHeight) / 2;
            AppWindow.MoveAndResize(new RectInt32(lpX, lpY, lpWidth, lpHeight));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置窗定位失败: {ex.Message}");
        }
    }

    private void BuildVersionDot()
    {
        // 界面内品牌标记用当前明暗对应的图标（系统跟随模式下由 Changed 重新套用）。
        VersionDot.Background = _lpTheme.BrandMarkBrush;
        BrandDot.Background = _lpTheme.BrandMarkBrush;
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _lpTheme.ElementTheme;
        RootGrid.Background = _lpTheme.SurfaceBrush;
        Platform.WindowChrome.Apply(this, _lpTheme.WindowBorderColor, _lpTheme.IsDark);
        BuildVersionDot();
        BuildHotkeyPresets();
        UpdateNavVisuals();
        UpdateThemeCardVisuals();
        UpdateColorThemeVisuals();
        UpdateDensityVisuals();
    }

    /// <summary>
    /// 渲染预置唤醒方式清单（双击 Ctrl/Alt/Shift 与组合键）。每项都标明适用与冲突风险，
    /// 选中后立即交给宿主重注册；鼠标中键/侧键未接入，不在这里放假开关。
    /// </summary>
    private void BuildHotkeyPresets()
    {
        _lpHotkeyRows.Clear();
        HotkeyPresetPanel.Children.Clear();

        foreach (var lpPreset in HotkeySpec.Presets)
        {
            var lpTitle = new TextBlock
            {
                Text = lpPreset.Title,
                FontSize = 12.5,
                Foreground = _lpTheme.Text1Brush
            };
            var lpTag = new TextBlock
            {
                Text = lpPreset.Tag,
                FontSize = 9,
                Foreground = _lpTheme.Text3Brush,
                VerticalAlignment = VerticalAlignment.Center
            };
            var lpHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            lpHeader.Children.Add(lpTitle);
            lpHeader.Children.Add(lpTag);

            var lpNote = new TextBlock
            {
                Text = lpPreset.Note,
                FontSize = 10,
                Foreground = _lpTheme.Text3Brush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            };

            var lpContent = new StackPanel();
            lpContent.Children.Add(lpHeader);
            lpContent.Children.Add(lpNote);

            // 用 Button 而不是 Border：可聚焦、可被 UI 自动化 Invoke，键盘用户也能选中。
            var lpRow = new Button
            {
                Background = _lpTheme.TransparentBrush,
                BorderBrush = _lpTheme.Border1Brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(11, 8, 11, 9),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinHeight = 0,
                Content = lpContent,
                Tag = lpPreset.Spec
            };
            AutomationProperties.SetName(lpRow, lpPreset.Spec.Text);
            lpRow.Click += OnHotkeyPresetClicked;

            _lpHotkeyRows.Add((lpRow, lpPreset.Spec));
            HotkeyPresetPanel.Children.Add(lpRow);
        }

        UpdateHotkeyPresetVisuals();
    }

    private void UpdateHotkeyPresetVisuals()
    {
        HotkeySpec.TryParse(_lpConfig.UiHotkey, out var lpCurrent, out _);
        if (UiHotkeyDisplayText is not null)
        {
            UiHotkeyDisplayText.Text = lpCurrent.Display;
        }

        foreach (var lpItem in _lpHotkeyRows)
        {
            var lpSelected = string.Equals(lpItem.Spec.Text, lpCurrent.Text, StringComparison.OrdinalIgnoreCase);
            lpItem.Row.Background = lpSelected ? _lpTheme.NavSelectedBrush : _lpTheme.TransparentBrush;
            lpItem.Row.BorderBrush = lpSelected ? _lpTheme.AccentBrush : _lpTheme.Border1Brush;
            lpItem.Row.BorderThickness = new Thickness(lpSelected ? 1.5 : 1);
            if (lpItem.Row.Content is StackPanel lpContent && lpContent.Children.Count > 0
                && lpContent.Children[0] is StackPanel lpHeader && lpHeader.Children.Count > 0
                && lpHeader.Children[0] is TextBlock lpTitle)
            {
                lpTitle.Foreground = lpSelected ? _lpTheme.Text1Brush : _lpTheme.Text2Brush;
            }
        }
    }

    private void OnHotkeyPresetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button lpRow || lpRow.Tag is not HotkeySpec lpSpec)
        {
            return;
        }

        AppLog.Info($"设置页请求切换唤醒方式: {lpSpec.Text}");
        HotkeyStatusText.Foreground = _lpTheme.Text3Brush;
        HotkeyStatusText.Text = $"正在应用 {lpSpec.Display}…";
        HotkeyRequested?.Invoke(lpSpec);
    }

    /// <summary>宿主在重注册完成后回报结果；成功同步选中态，失败给出原因并指路备选方式。</summary>
    internal void ReportHotkeyResult(HotkeySpec spec, bool ok, string message)
    {
        if (ok)
        {
            _lpConfig.UiHotkey = spec.Text;
            UpdateHotkeyPresetVisuals();
            HotkeyStatusText.Foreground = _lpTheme.Text3Brush;
            HotkeyStatusText.Text = $"已生效：{spec.Display}，已写入 config.json，立即可以使用。";
            AppLog.Info($"设置页唤醒方式已生效: {spec.Text}");
            return;
        }

        HotkeyStatusText.Foreground = FailureBrush;
        HotkeyStatusText.Text = $"未生效：{message}";
        AppLog.Warn($"设置页唤醒方式未生效: {spec.Text} · {message}");
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
        PageHelp.Visibility = page == "help" ? Visibility.Visible : Visibility.Collapsed;

        UpdateNavVisuals();
        // 窗口尺寸固定（840×640 DIP），切页只换内容、不改变窗口大小；内容超出时由 ContentScroller 自己滚动。
        ContentScroller.ChangeView(null, 0, null, disableAnimation: true);

        if (page == "scope")
        {
            RefreshExcludeRules();
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
        ApplyNav(NavHelp, _lpCurrentPage == "help");
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

    private void UpdateColorThemeVisuals()
    {
        var lpSelected = _lpTheme.CurrentColorTheme.Id;
        SetColorThemeCard(ColorThemeIndigo, ColorThemeIndigoCheck, string.Equals(lpSelected, "indigo", StringComparison.Ordinal));
        SetColorThemeCard(ColorThemeViolet, ColorThemeVioletCheck, string.Equals(lpSelected, "violet", StringComparison.Ordinal));
        SetColorThemeCard(ColorThemeAurora, ColorThemeAuroraCheck, string.Equals(lpSelected, "aurora", StringComparison.Ordinal));
        SetColorThemeCard(ColorThemeForest, ColorThemeForestCheck, string.Equals(lpSelected, "forest", StringComparison.Ordinal));
        SetColorThemeCard(ColorThemeEmber, ColorThemeEmberCheck, string.Equals(lpSelected, "ember", StringComparison.Ordinal));
    }

    private void SetColorThemeCard(Border card, FontIcon check, bool selected)
    {
        card.BorderBrush = selected ? _lpTheme.AccentBrush : _lpTheme.Border1Brush;
        card.Opacity = selected ? 1.0 : 0.72;
        check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnNavHelpClick(object sender, RoutedEventArgs e) => ShowPage("help");

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

    private void OnColorThemeTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is Border lpCard && lpCard.Tag is string lpId)
        {
            _lpTheme.SetColorTheme(lpId);
        }
    }

    private void OnOpacityChanged(object? sender, EventArgs e)
    {
        var lpValue = OpacitySlider.Value;
        OpacityValueText.Text = $"{lpValue * 100:0}%";
        _lpTheme.SetOpacity(lpValue);
    }

    private void OnScopeAppsToggled(object? sender, EventArgs e)
    {
        if (_lpSyncingScopeApps)
        {
            return;
        }

        _lpConfig.IncludeInstalledPrograms = ScopeAppsSwitch.IsOn;
        _lpTheme.SaveConfig();
        AppLog.Info($"已安装程序搜索：{(ScopeAppsSwitch.IsOn ? "开" : "关")}（下一次搜索即生效）");
    }

    private void OnScopeBrowserToggled(object? sender, EventArgs e)
    {
        if (_lpSyncingScopeBrowser)
        {
            return;
        }

        _lpConfig.IncludeBrowserHistory = ScopeBrowserSwitch.IsOn;
        _lpTheme.SaveConfig();
        AppLog.Info($"浏览器历史与书签搜索：{(ScopeBrowserSwitch.IsOn ? "开" : "关")}（下一次搜索即生效）");
    }

    private void OnScopeNetworkToggled(object? sender, EventArgs e)
    {
        if (_lpSyncingNetwork)
        {
            return;
        }

        _lpConfig.IncludeNetworkLocations = ScopeNetworkSwitch.IsOn;
        _lpTheme.SaveConfig();
        NetworkPanel.Visibility = ScopeNetworkSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        NetworkStatusText.Text = ScopeNetworkSwitch.IsOn
            ? "已开启，正在后台建立网络位置索引…"
            : "已关闭，正在清空网络位置索引…";
        AppLog.Info($"网络位置搜索：{(_lpConfig.IncludeNetworkLocations ? "开" : "关")}");
        RefreshNetworkLocations();
        _ = RequestExtrasBuildAsync();
    }

    /// <summary>网络位置开关打开时显示已映射盘（自动包含）与用户添加的共享，附删除按钮。</summary>
    private void RefreshNetworkLocations()
    {
        NetworkLocationPanel.Children.Clear();

        var lpMapped = new List<string>();
        try
        {
            foreach (var lpDrive in DriveInfo.GetDrives())
            {
                if (lpDrive.DriveType == DriveType.Network)
                {
                    lpMapped.Add(lpDrive.Name.TrimEnd('\\'));
                }
            }
        }
        catch (IOException)
        {
            // 枚举失败时只是不显示映射盘，不影响手动添加的共享。
        }

        NetworkMappedText.Text = lpMapped.Count == 0
            ? "未检测到已映射的网络驱动器。"
            : "已映射、将自动包含：" + string.Join("、", lpMapped);

        if (_lpConfig.NetworkLocations.Count == 0)
        {
            NetworkLocationPanel.Children.Add(new TextBlock
            {
                Text = "尚未添加共享路径",
                FontSize = 10.5,
                Foreground = _lpTheme.Text3Brush
            });
            return;
        }

        foreach (var lpLocation in _lpConfig.NetworkLocations)
        {
            NetworkLocationPanel.Children.Add(BuildNetworkRow(lpLocation));
        }
    }

    private Border BuildNetworkRow(string location)
    {
        var lpText = new TextBlock
        {
            Text = location,
            FontSize = 11,
            Foreground = _lpTheme.Text1Brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(lpText, location);

        var lpRemove = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Background = _lpTheme.TransparentBrush,
            BorderBrush = _lpTheme.TransparentBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0, 6, 0),
            Height = 24,
            MinWidth = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = location
        };
        AutomationProperties.SetName(lpRemove, $"移除网络位置 {location}");
        ToolTipService.SetToolTip(lpRemove, "移除");
        lpRemove.Click += OnRemoveNetworkLocationClick;

        var lpRow = new Grid();
        lpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lpRow.Children.Add(lpText);
        Grid.SetColumn(lpRemove, 1);
        lpRow.Children.Add(lpRemove);

        return new Border
        {
            Background = _lpTheme.Surface2Brush,
            BorderBrush = _lpTheme.Border1Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 4, 4),
            Child = lpRow
        };
    }

    private void OnAddNetworkLocationClick(object sender, RoutedEventArgs e)
    {
        var lpInput = NetworkPathBox.Text.Trim();
        if (lpInput.Length == 0)
        {
            NetworkStatusText.Text = "请输入共享路径，例如 \\\\NAS\\Media";
            return;
        }

        if (!lpInput.StartsWith(@"\\", StringComparison.Ordinal) && !Path.IsPathRooted(lpInput))
        {
            NetworkStatusText.Text = "请输入 UNC 共享路径（\\\\服务器\\共享）或盘符路径";
            return;
        }

        string lpFullPath;
        try
        {
            lpFullPath = Path.GetFullPath(lpInput).TrimEnd('\\');
        }
        catch (Exception ex)
        {
            NetworkStatusText.Text = "路径无效：" + ex.Message;
            return;
        }

        // 网络盘离线是常态：不可访问时也允许保存，构建时会跳过并在日志里说明。
        if (!Directory.Exists(lpFullPath))
        {
            NetworkStatusText.Text = $"路径当前不可访问，仍会保存：{lpFullPath}";
        }

        if (_lpConfig.NetworkLocations.Any(lpExisting =>
                string.Equals(lpExisting.TrimEnd('\\'), lpFullPath, StringComparison.OrdinalIgnoreCase)))
        {
            NetworkStatusText.Text = "该位置已在列表中";
            return;
        }

        _lpConfig.NetworkLocations.Add(lpFullPath);
        _lpTheme.SaveConfig();
        NetworkPathBox.Text = string.Empty;
        NetworkStatusText.Text = _lpConfig.IncludeNetworkLocations
            ? $"已添加：{lpFullPath}（正在后台建立索引）"
            : $"已添加：{lpFullPath}（打开开关后才会索引）";
        RefreshNetworkLocations();
        AppLog.Info($"网络位置已添加: {lpFullPath}");
        if (_lpConfig.IncludeNetworkLocations)
        {
            _ = RequestExtrasBuildAsync();
        }
    }

    private void OnRemoveNetworkLocationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string lpLocation })
        {
            return;
        }

        var lpIndex = _lpConfig.NetworkLocations.FindIndex(lpExisting =>
            string.Equals(lpExisting.TrimEnd('\\'), lpLocation.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (lpIndex < 0)
        {
            return;
        }

        _lpConfig.NetworkLocations.RemoveAt(lpIndex);
        _lpTheme.SaveConfig();
        NetworkStatusText.Text = $"已移除：{lpLocation}";
        RefreshNetworkLocations();
        AppLog.Info($"网络位置已移除: {lpLocation}");
        if (_lpConfig.IncludeNetworkLocations)
        {
            _ = RequestExtrasBuildAsync();
        }
    }

    /// <summary>附加索引目录（本地文件夹）列表：与网络位置共用 extras 索引与重建通道。</summary>
    private void RefreshIndexDirectories()
    {
        IndexDirectoryPanel.Children.Clear();
        IndexDirStatusText.Text = _lpConfig.IndexDirectories.Count == 0
            ? "未添加附加索引目录；主索引卷之外的文件夹可加到这里。"
            : $"已添加 {_lpConfig.IndexDirectories.Count} 个附加索引目录。";

        foreach (var lpDirectory in _lpConfig.IndexDirectories)
        {
            IndexDirectoryPanel.Children.Add(BuildIndexDirectoryRow(lpDirectory));
        }
    }

    private Border BuildIndexDirectoryRow(string directory)
    {
        var lpText = new TextBlock
        {
            Text = directory,
            FontSize = 11,
            Foreground = _lpTheme.Text1Brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(lpText, directory);

        var lpRemove = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Background = _lpTheme.TransparentBrush,
            BorderBrush = _lpTheme.TransparentBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0, 6, 0),
            Height = 24,
            MinWidth = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = directory
        };
        AutomationProperties.SetName(lpRemove, $"移除索引目录 {directory}");
        ToolTipService.SetToolTip(lpRemove, "移除");
        lpRemove.Click += OnRemoveIndexDirectoryClick;

        var lpRow = new Grid();
        lpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lpRow.Children.Add(lpText);
        Grid.SetColumn(lpRemove, 1);
        lpRow.Children.Add(lpRemove);

        return new Border
        {
            Background = _lpTheme.Surface2Brush,
            BorderBrush = _lpTheme.Border1Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 4, 4),
            Child = lpRow
        };
    }

    private void OnAddIndexDirectoryClick(object sender, RoutedEventArgs e)
    {
        var lpInput = IndexDirPathBox.Text.Trim();
        if (lpInput.Length == 0)
        {
            IndexDirStatusText.Text = "请输入文件夹完整路径，例如 D:\\Projects";
            return;
        }

        string lpFullPath;
        try
        {
            lpFullPath = Path.GetFullPath(lpInput).TrimEnd('\\');
        }
        catch (Exception ex)
        {
            IndexDirStatusText.Text = "路径无效：" + ex.Message;
            return;
        }

        if (!Directory.Exists(lpFullPath))
        {
            IndexDirStatusText.Text = $"目录不存在：{lpFullPath}";
            return;
        }

        if (_lpConfig.IndexDirectories.Any(lpExisting =>
                string.Equals(lpExisting.TrimEnd('\\'), lpFullPath, StringComparison.OrdinalIgnoreCase)))
        {
            IndexDirStatusText.Text = "该目录已在列表中";
            return;
        }

        _lpConfig.IndexDirectories.Add(lpFullPath);
        _lpTheme.SaveConfig();
        IndexDirPathBox.Text = string.Empty;
        AppLog.Info($"附加索引目录已添加: {lpFullPath}");
        RefreshIndexDirectories();
        IndexDirStatusText.Text = $"已添加：{lpFullPath}（正在后台建立索引）";
        _ = RequestExtrasBuildAsync();
    }

    private void OnRemoveIndexDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string lpDirectory })
        {
            return;
        }

        var lpIndex = _lpConfig.IndexDirectories.FindIndex(lpExisting =>
            string.Equals(lpExisting.TrimEnd('\\'), lpDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (lpIndex < 0)
        {
            return;
        }

        _lpConfig.IndexDirectories.RemoveAt(lpIndex);
        _lpTheme.SaveConfig();
        AppLog.Info($"附加索引目录已移除: {lpDirectory}");
        RefreshIndexDirectories();
        IndexDirStatusText.Text = $"已移除：{lpDirectory}";
        _ = RequestExtrasBuildAsync();
    }

    /// <summary>请求服务端按当前配置重建附加索引；成功后取消旧映射并重新装载。</summary>
    private async Task RequestExtrasBuildAsync()
    {
        try
        {
            var lpReply = await PipeClient.SendAsync(
                Branding.ServicePipeName,
                new IpcRequest { Command = "INDEX_EXTRAS" },
                TimeSpan.FromSeconds(10));
            if (!lpReply.Ok)
            {
                NetworkStatusText.Text = "重建网络索引失败：" + lpReply.Message;
            }
        }
        catch (Exception ex)
        {
            NetworkStatusText.Text = "重建网络索引失败：" + ex.Message;
        }
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

    /// <summary>安装范围来自安装包写入的注册值；便携版没有该值。</summary>
    private void ApplyInstallInfo()
    {
        var lpInfo = ElevationService.ReadInstallInfo();
        InstallScopeBadge.Text = lpInfo.ScopeLabel;
        InstallScopeHint.Text = lpInfo.IsInstalled
            ? $"维护者：安装包（Inno Setup） · 目录 {lpInfo.Directory} · 切换安装范围需重新运行安装包"
            : "未通过安装包安装（便携版）；由解压目录维护，切换安装范围需运行安装包。";
    }

    private async void RefreshElevationState()
    {
        var lpElevated = ElevationService.IsElevated();
        var lpTask = await Task.Run(ElevationService.TaskExists);
        ElevationStateText.Text =
            $"当前进程：{(lpElevated ? "管理员权限（高完整性）" : "标准权限")} · 计划任务：{(lpTask ? "已注册" : "未注册")}";
    }

    private readonly record struct ElevationTaskResult(bool Ok, string Error);

    /// <summary>
    /// 开关是真逻辑：注册/删除计划任务失败（含用户取消 UAC）时回退开关并说明原因。
    /// 注册成功后下次启动才会走提权路径，不会立刻重启当前进程。
    /// </summary>
    private async void OnElevationToggled(object? sender, EventArgs e)
    {
        if (_lpSyncingElevation || _lpElevationBusy)
        {
            return;
        }

        var lpEnable = ElevationSwitch.IsOn;
        if (lpEnable == _lpConfig.AlwaysRunAsAdmin)
        {
            return;
        }

        _lpElevationBusy = true;
        ElevationSwitch.IsEnabled = false;
        ElevationStateText.Text = lpEnable ? "正在注册计划任务（可能弹出 UAC）…" : "正在删除计划任务（可能弹出 UAC）…";

        var lpResult = await Task.Run(() =>
        {
            if (lpEnable)
            {
                var lpOk = ElevationService.TryCreateTask(out var lpError);
                return new ElevationTaskResult(lpOk, lpError);
            }

            var lpRemoved = ElevationService.TryDeleteTask(out var lpRemoveError);
            return new ElevationTaskResult(lpRemoved, lpRemoveError);
        });

        if (lpResult.Ok)
        {
            _lpConfig.AlwaysRunAsAdmin = lpEnable;
            _lpTheme.SaveConfig();
            AppLog.Info($"始终以管理员身份运行: {(lpEnable ? "已开启" : "已关闭")}");
        }
        else
        {
            _lpSyncingElevation = true;
            ElevationSwitch.IsOn = !lpEnable;
            _lpSyncingElevation = false;
            AppLog.Warn($"提权设置未生效: {lpResult.Error}");

            var lpDialog = new ContentDialog
            {
                Title = lpEnable ? "未能开启管理员运行" : "未能关闭管理员运行",
                Content = lpResult.Error + "\n\n可以稍后重试；没有注册任务时，程序仍按标准权限启动。",
                CloseButtonText = "知道了",
                XamlRoot = RootGrid.XamlRoot
            };
            await lpDialog.ShowAsync();
        }

        _lpElevationBusy = false;
        ElevationSwitch.IsEnabled = true;
        RefreshElevationState();
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
            IndexFootText.Text = lpReply.Ok
                ? "重建请求已受理，正在扫描…"
                : $"未重新开始：{lpReply.Message}";
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
                "building" => lpStatus.Percent >= 0 ? $"构建中 {lpStatus.Percent:0}%" : "构建中",
                "error" => "失败",
                _ => "未建"
            };

            ScopeLocalDetail.Text = lpStatus.Loaded
                ? $"由后台服务提供 · {FormatCount(lpStatus.EntryCount)} 项"
                : "由后台服务提供 · 索引尚未建立";

            if (lpStatus.State == "building")
            {
                IndexFootText.Text = $"构建中 · {DescribeBuildProgress(lpStatus)} · {lpStatus.Note}";
            }
            else if (lpStatus.State == "error")
            {
                IndexFootText.Text = $"{lpStatus.Note}（{lpStatus.Warning}）";
            }
            else
            {
                IndexFootText.Text = lpStatus.Loaded
                    ? $"来源 {DescribeSource(lpStatus.Source)} · 载入 {lpStatus.LoadMs:0} ms · {lpStatus.Note}" +
                      (string.IsNullOrEmpty(lpStatus.Warning) ? string.Empty : $" · {lpStatus.Warning}")
                    : "索引尚未建立；启动后会自动构建，也可点击右侧「重建索引」。";
            }

            var lpTrackWidth = (IndexProgressFill.Parent as FrameworkElement)?.ActualWidth ?? 0;
            IndexProgressFill.Width = lpStatus.State == "building"
                ? lpStatus.Percent >= 0
                    ? Math.Max(2, lpTrackWidth * lpStatus.Percent / 100)
                    : Math.Max(28, lpTrackWidth * 0.04)
                : 0;

            BuildIndexChips(lpStatus);

            // 自动构建没有经过「重建索引」按钮，这里按状态自行续上轮询，构建结束再停。
            if (lpStatus.State == "building")
            {
                StartIndexPolling();
            }
            else
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
            IndexChipsPanel.Children.Add(BuildChip("\uE7C3", $"索引文件 {Path.GetFileName(status.FilePath)}", false));
        }
    }

    private static string DescribeBuildProgress(IndexStatusDto status)
    {
        var lpPercent = status.Percent >= 0 ? $"{status.Percent:0.#}%，" : string.Empty;
        return status.Phase == "write"
            ? $"{lpPercent}正在写入 {status.Scanned:N0} 条"
            : $"{lpPercent}已扫描 {status.Scanned:N0} 条";
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

        if (source.StartsWith("usn:", StringComparison.OrdinalIgnoreCase) || source == "usn")
        {
            return "USN 全量索引";
        }

        if (source.StartsWith("walk:", StringComparison.OrdinalIgnoreCase) || source == "walk")
        {
            return "目录遍历索引";
        }

        return source;
    }

    /// <summary>按当前配置重建排除规则列表；只在进入该页或增删后调用，不放进每秒的状态轮询。</summary>
    private void RefreshExcludeRules()
    {
        ExcludeRulePanel.Children.Clear();
        if (_lpConfig.ExcludedPaths.Count == 0)
        {
            ExcludeRulePanel.Children.Add(new TextBlock
            {
                Text = "尚未添加排除规则",
                FontSize = 10.5,
                Foreground = _lpTheme.Text3Brush
            });
            return;
        }

        foreach (var lpRule in _lpConfig.ExcludedPaths)
        {
            ExcludeRulePanel.Children.Add(BuildExcludeRuleRow(lpRule));
        }
    }

    private Border BuildExcludeRuleRow(string rule)
    {
        var lpText = new TextBlock
        {
            Text = rule,
            FontSize = 11,
            Foreground = _lpTheme.Text1Brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(lpText, rule);

        var lpRemove = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Background = _lpTheme.TransparentBrush,
            BorderBrush = _lpTheme.TransparentBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0, 6, 0),
            Height = 24,
            MinWidth = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = rule
        };
        AutomationProperties.SetName(lpRemove, $"移除排除规则 {rule}");
        ToolTipService.SetToolTip(lpRemove, "移除");
        lpRemove.Click += OnRemoveExcludeClick;

        var lpRow = new Grid();
        lpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lpRow.Children.Add(lpText);
        Grid.SetColumn(lpRemove, 1);
        lpRow.Children.Add(lpRemove);

        return new Border
        {
            Background = _lpTheme.Surface2Brush,
            BorderBrush = _lpTheme.Border1Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 4, 4),
            Child = lpRow
        };
    }

    private void OnAddExcludeClick(object sender, RoutedEventArgs e)
    {
        var lpInput = ExcludePathBox.Text.Trim();
        if (lpInput.Length == 0)
        {
            ExcludeStatusText.Text = "请输入要排除的文件夹完整路径";
            return;
        }

        string lpFullPath;
        try
        {
            lpFullPath = Path.GetFullPath(lpInput);
        }
        catch (Exception ex)
        {
            ExcludeStatusText.Text = "路径无效：" + ex.Message;
            return;
        }

        if (!Directory.Exists(lpFullPath))
        {
            ExcludeStatusText.Text = $"目录不存在：{lpFullPath}";
            return;
        }

        lpFullPath = NormalizeExcludePath(lpFullPath);
        if (_lpConfig.ExcludedPaths.Any(lpExisting =>
                string.Equals(NormalizeExcludePath(lpExisting), lpFullPath, StringComparison.OrdinalIgnoreCase)))
        {
            ExcludeStatusText.Text = "该目录已在排除列表中";
            return;
        }

        _lpConfig.ExcludedPaths.Add(lpFullPath);
        _lpTheme.SaveConfig();
        ExcludePathBox.Text = string.Empty;
        ExcludeStatusText.Text = $"已添加排除：{lpFullPath}（下一次搜索即生效）";
        RefreshExcludeRules();
        AppLog.Info($"排除规则已添加: {lpFullPath}");
    }

    private void OnRemoveExcludeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string lpRule })
        {
            return;
        }

        var lpIndex = _lpConfig.ExcludedPaths.FindIndex(lpExisting =>
            string.Equals(NormalizeExcludePath(lpExisting), NormalizeExcludePath(lpRule), StringComparison.OrdinalIgnoreCase));
        if (lpIndex < 0)
        {
            return;
        }

        _lpConfig.ExcludedPaths.RemoveAt(lpIndex);
        _lpTheme.SaveConfig();
        ExcludeStatusText.Text = $"已移除排除：{lpRule}（下一次搜索即生效）";
        RefreshExcludeRules();
        AppLog.Info($"排除规则已移除: {lpRule}");
    }

    /// <summary>存盘形态：去掉末尾分隔符；盘符根保留为 C:\，避免显示成 C:。</summary>
    private static string NormalizeExcludePath(string path)
    {
        var lpTrimmed = path.Trim().TrimEnd('\\', '/');
        return lpTrimmed.Length == 2 && lpTrimmed[1] == ':' ? lpTrimmed + "\\" : lpTrimmed;
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
