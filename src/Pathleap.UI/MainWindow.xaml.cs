using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pathleap.Shared;
using Windows.ApplicationModel.DataTransfer;
using Pathleap.Shared.Config;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Ipc;
using Pathleap.UI.Data;
using Pathleap.UI.Perf;
using Pathleap.UI.Platform;
using Pathleap.UI.Theme;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace Pathleap.UI;

/// <summary>
/// 搜索窗（设计稿屏 1）：空输入展示「最近访问」首页，输入态展示结果行
/// （图标 → 高亮文件名 → 路径 → 来源标签），选中行有底色与行首 ↵ 键帽，底栏常驻键盘提示。
/// 无标题栏、默认置顶、失焦自动隐藏；每次唤出记 QPC 打点供基准测试使用。
/// </summary>
public sealed partial class MainWindow : Window
{
    private const double DeactivateGraceMs = 250;
    private const int ForegroundRetryDelayMs = 120;
    private const int ForegroundRetryAttempts = 2;
    private const int SearchDebounceMs = 120;
    private const int IndexPollMs = 1000;
    private const int SearchLimit = 24;
    private const int PageStep = 6;

    private readonly AppConfig _lpConfig;
    private readonly IntPtr _lpHwnd;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _lpDispatcher;
    private readonly ObservableCollection<LpResultRow> _lpRows = new();
    private readonly ThemeService _lpTheme = ThemeService.Instance;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _lpSearchTimer;
    private int _lpQueryGeneration;
    private bool _lpSuppressAutoSearch;
    private double _lpLastQueryMs = -1;
    private WakeSample? _lpPendingSample;
    private WakeSample? _lpActiveSample;
    private bool _lpVisible;
    private bool _lpAllowClose;
    private long _lpShownTick;
    private int _lpVisibleSession;
    private int _lpSelectedIndex = -1;
    private bool _lpHomeMode = true;
    private bool _lpServiceConnected;
    private string _lpServiceStatus = "正在连接索引服务…";
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _lpForegroundRetry;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _lpIndexPoll;
    private IndexStatusDto? _lpIndexStatus;

    /// <summary>搜索结果范围：all=全部、file=仅文件、folder=仅文件夹；仅本次会话有效。</summary>
    private string _lpScope = "all";

    /// <summary>设置 / 快速切换 / 检查更新 / 关于由 App 接管（避免窗口互相直接引用）。</summary>
    public event Action? SettingsRequested;

    public event Action? QuickSwitchRequested;

    public event Action? UpdateRequested;

    public event Action? AboutRequested;

    public MainWindow(AppConfig config)
    {
        _lpConfig = config;
        InitializeComponent();

        _lpHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _lpDispatcher = DispatcherQueue;
        Title = $"{Branding.DisplayName} · {Branding.ChineseName}";
        ResultsItems.ItemsSource = _lpRows;

        ConfigureWindow();
        ApplyTheme();
        _lpTheme.Changed += OnThemeChanged;

        SearchBox.KeyDown += OnSearchKeyDown;
        // Tab 在 WinUI 里先被焦点导航消费，连 PreviewKeyDown 都收不到；
        // 根元素用 handledEventsToo 兜底，保证 Tab 一定落到范围切换上。
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), true);
        SearchBox.GotFocus += OnSearchGotFocus;
        SearchBox.TextChanged += OnSearchTextChanged;
        CompositionTarget.Rendering += OnRendering;
        Activated += OnActivated;
        AppWindow.Closing += OnAppWindowClosing;

        RecentStore.Updated += OnRecentUpdated;
        ShowHome();

        // 首次启动主窗给一次三步引导；看过（或跳过）后写设置，不再出现。
        if (!_lpTheme.OnboardingDone)
        {
            GuideOverlay.Visibility = Visibility.Visible;
        }

        AppLog.Info($"主窗口已创建（未显示），HWND=0x{_lpHwnd.ToInt64():X}");
    }

    private void OnGuideSkipClick(object sender, RoutedEventArgs e)
    {
        DismissGuide();
    }

    private void OnGuideDoneClick(object sender, RoutedEventArgs e)
    {
        DismissGuide();
    }

    private void DismissGuide()
    {
        GuideOverlay.Visibility = Visibility.Collapsed;
        _lpTheme.MarkOnboardingDone();
        SearchBox.Focus(FocusState.Programmatic);
        AppLog.Info("主窗口首次引导已关闭");
    }

    public Microsoft.UI.Dispatching.DispatcherQueue UiDispatcher => _lpDispatcher;

    public bool IsForegroundAndVisible()
    {
        return NativeMethods.GetForegroundWindow().Equals(_lpHwnd) && NativeMethods.IsWindowVisible(_lpHwnd);
    }

    private void ConfigureWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter lpPresenter)
        {
            lpPresenter.IsAlwaysOnTop = _lpConfig.PinWindow;
            lpPresenter.IsResizable = false;
            lpPresenter.IsMaximizable = false;
            lpPresenter.IsMinimizable = false;
            try
            {
                lpPresenter.SetBorderAndTitleBar(false, false);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"无边框设置失败: {ex.Message}");
            }
        }

        AppWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = true;
        NcFrame.Apply(this);
        ApplyBackdrop();
    }

    private void ApplyBackdrop()
    {
        try
        {
            SystemBackdrop = _lpTheme.AcrylicEnabled ? new DesktopAcrylicBackdrop() : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Acrylic 背景不可用，回退纯色: {ex.Message}");
        }
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _lpTheme.ElementTheme;
        RootGrid.Background = _lpTheme.AcrylicEnabled ? _lpTheme.WindowBackgroundBrush : _lpTheme.SurfaceBrush;
        WindowChrome.Apply(this, _lpTheme.WindowBorderColor, _lpTheme.IsDark);
        RootBorder.Background = _lpTheme.AcrylicEnabled
            ? _lpTheme.WindowBackgroundBrush
            : _lpTheme.SurfaceBrush;
        RootBorder.CornerRadius = new CornerRadius(0);

        BrandDot.Background = _lpTheme.BrandMarkBrush;

        PinButton.Foreground = new SolidColorBrush(_lpTheme.Accent);
        PinButton.Opacity = _lpConfig.PinWindow ? 1.0 : 0.45;

        ApplyBackdrop();
        foreach (var lpRow in _lpRows)
        {
            lpRow.RefreshVisuals();
        }
    }

    private void OnThemeChanged()
    {
        ApplyTheme();
    }

    internal WakeSample ArmMeasurement(int index, string probeChar)
    {
        var lpSample = new WakeSample(index, probeChar);
        _lpPendingSample = lpSample;
        _lpActiveSample = null;
        return lpSample;
    }

    public void CancelMeasurement(string reason)
    {
        _lpPendingSample?.Abort(reason);
        _lpActiveSample?.Abort(reason);
        _lpPendingSample = null;
        _lpActiveSample = null;
    }

    public void ShowFromHotkey(long tick)
    {
        var lpSample = _lpPendingSample;
        _lpPendingSample = null;
        _lpActiveSample = null;

        if (lpSample is not null)
        {
            lpSample.HotkeyTick = tick;
            lpSample.ShowCallTick = Stopwatch.GetTimestamp();
            _lpActiveSample = lpSample;
        }

        ShowCore();

        if (lpSample is not null)
        {
            lpSample.ActivateTick = Stopwatch.GetTimestamp();
        }
    }

    public void ShowFromCommand()
    {
        _lpPendingSample = null;
        _lpActiveSample = null;
        ShowCore();
    }

    private void ShowCore()
    {
        PositionWindow();
        _lpVisible = true;
        _lpVisibleSession++;
        _lpShownTick = Stopwatch.GetTimestamp();
        Activate();
        NativeMethods.SetForegroundWindow(_lpHwnd);
        SearchBox.Focus(FocusState.Programmatic);
        AppLog.Debug("搜索窗已显示并请求焦点");
        StartIndexPolling();
        ScheduleForegroundRetry(_lpVisibleSession, ForegroundRetryAttempts);
    }

    private void ScheduleForegroundRetry(int session, int attemptsLeft)
    {
        if (attemptsLeft <= 0 || !_lpVisible || session != _lpVisibleSession)
        {
            return;
        }

        if (NativeMethods.GetForegroundWindow().Equals(_lpHwnd))
        {
            return;
        }

        _lpForegroundRetry?.Stop();
        var lpTimer = _lpDispatcher.CreateTimer();
        lpTimer.Interval = TimeSpan.FromMilliseconds(ForegroundRetryDelayMs);
        lpTimer.IsRepeating = false;
        lpTimer.Tick += (_, _) =>
        {
            lpTimer.Stop();
            if (!_lpVisible || session != _lpVisibleSession)
            {
                return;
            }

            if (NativeMethods.GetForegroundWindow().Equals(_lpHwnd))
            {
                return;
            }

            var lpTookForeground = ForceForeground();
            AppLog.Info($"前台锁重试命中：ok={lpTookForeground}，剩余 {attemptsLeft - 1} 次");
            ScheduleForegroundRetry(session, attemptsLeft - 1);
        };
        _lpForegroundRetry = lpTimer;
        lpTimer.Start();
    }

    private bool ForceForeground()
    {
        var lpForeground = NativeMethods.GetForegroundWindow();
        var lpForegroundThread = NativeMethods.GetWindowThreadProcessId(lpForeground, IntPtr.Zero);
        var lpOwnThread = NativeMethods.GetWindowThreadProcessId(_lpHwnd, IntPtr.Zero);
        var lpAttached = false;
        try
        {
            if (lpForegroundThread != 0 && lpOwnThread != 0 && lpForegroundThread != lpOwnThread)
            {
                lpAttached = NativeMethods.AttachThreadInput(lpForegroundThread, lpOwnThread, true);
            }

            NativeMethods.BringWindowToTop(_lpHwnd);
            NativeMethods.SetForegroundWindow(_lpHwnd);
            NativeMethods.SetFocus(_lpHwnd);
            Activate();
            SearchBox.Focus(FocusState.Programmatic);
            return NativeMethods.GetForegroundWindow().Equals(_lpHwnd);
        }
        finally
        {
            if (lpAttached)
            {
                NativeMethods.AttachThreadInput(lpForegroundThread, lpOwnThread, false);
            }
        }
    }

    public void HideForBenchmark()
    {
        _lpPendingSample = null;
        _lpActiveSample = null;
        HideCore();
    }

    public void HideWindow()
    {
        HideCore();
    }

    private void HideCore()
    {
        _lpForegroundRetry?.Stop();
        _lpForegroundRetry = null;
        _lpIndexPoll?.Stop();
        if (_lpVisible)
        {
            _lpVisible = false;
            _lpVisibleSession++;
            AppWindow.Hide();
            SearchBox.Text = string.Empty;
            AppLog.Debug("搜索窗已隐藏");
            return;
        }

        AppWindow.Hide();
    }

    public void SetServiceStatus(string text, bool connected)
    {
        _lpServiceStatus = text;
        _lpServiceConnected = connected;
        ServiceStatusText.Text = text;
        UpdateStatusDot(connected);
    }

    /// <summary>底栏状态点：实心 5px 圆点 + 同色 11px 低透明度光晕，与设计稿对齐；颜色随连接状态变化。</summary>
    private void UpdateStatusDot(bool connected)
    {
        var lpColor = connected
            ? Color.FromArgb(255, 46, 158, 107)
            : Color.FromArgb(255, 199, 122, 24);
        StatusDot.Fill = new SolidColorBrush(lpColor);
        StatusGlow.Fill = new SolidColorBrush(lpColor);
        StatusGlow.Opacity = connected ? 0.18 : 0.13;
    }

    /// <summary>窗口可见期间每秒取一次 INDEX_STATUS：构建中显示已扫描条目，就绪或失败后停掉轮询。</summary>
    private void StartIndexPolling()
    {
        _lpIndexPoll ??= CreateIndexPollTimer();
        _lpIndexPoll.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateIndexPollTimer()
    {
        var lpTimer = _lpDispatcher.CreateTimer();
        lpTimer.Interval = TimeSpan.FromMilliseconds(IndexPollMs);
        lpTimer.IsRepeating = true;
        lpTimer.Tick += async (_, _) => await PollIndexStatusAsync();
        return lpTimer;
    }

    private async Task PollIndexStatusAsync()
    {
        try
        {
            var lpReply = await PipeClient.SendAsync(
                Branding.ServicePipeName,
                new IpcRequest { Command = "INDEX_STATUS" },
                TimeSpan.FromSeconds(2));
            if (!lpReply.Ok || lpReply.Index is null)
            {
                return;
            }

            ApplyIndexStatus(lpReply.Index);
            if (lpReply.Index.State != "building")
            {
                _lpIndexPoll?.Stop();
            }
        }
        catch (Exception ex)
        {
            // 服务未连接时不再空转，下次唤出窗口会重新开始轮询。
            AppLog.Debug($"索引状态轮询失败: {ex.Message}");
            _lpIndexPoll?.Stop();
        }
    }

    private void ApplyIndexStatus(IndexStatusDto status)
    {
        var lpWasBuilding = _lpIndexStatus?.State == "building";
        _lpIndexStatus = status;
        _lpServiceConnected = status.State is "ready" or "building";
        switch (status.State)
        {
            case "building":
                SetServiceStatus("索引构建中 · " + DescribeBuildProgress(status), false);
                if (_lpHomeMode && _lpRows.Count == 0)
                {
                    ShowEmpty("\uE895", "索引正在构建", DescribeBuildProgress(status) + "；完成后自动可搜");
                }

                break;

            case "ready":
                SetServiceStatus(
                    $"索引就绪 · {status.EntryCount:N0} 条" +
                    (string.IsNullOrEmpty(status.Warning) ? string.Empty : " · 降级模式"),
                    true);
                if (_lpHomeMode && _lpRows.Count == 0)
                {
                    UpdateEmptyState();
                }

                // 构建期搜到的结果是上一次索引的，构建一完成就用当前关键词重搜一遍。
                if (lpWasBuilding && SearchBox.Text.Length > 0)
                {
                    ScheduleSearch();
                }

                break;

            case "error":
                SetServiceStatus("索引不可用 · 打开设置查看原因", false);
                break;
        }
    }

    private static string DescribeBuildProgress(IndexStatusDto status)
    {
        var lpPercent = status.Percent >= 0 ? $"{status.Percent:0.#}%，" : string.Empty;
        return status.Phase == "write"
            ? $"{lpPercent}正在写入 {status.Scanned:N0} 条"
            : $"{lpPercent}已扫描 {status.Scanned:N0} 条";
    }

    private void PositionWindow()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var lpCursor))
            {
                return;
            }

            var lpMonitor = NativeMethods.MonitorFromPoint(lpCursor, NativeMethods.MonitorDefaultToNearest);
            if (IntPtr.Zero.Equals(lpMonitor))
            {
                return;
            }

            var lpInfo = new NativeMethods.MonitorInfo
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfo>()
            };
            if (!NativeMethods.GetMonitorInfoW(lpMonitor, ref lpInfo))
            {
                AppLog.Warn($"GetMonitorInfoW 失败（Win32={Marshal.GetLastWin32Error()}），沿用上次位置。");
                return;
            }

            var lpDpi = 96u;
            if (NativeMethods.GetDpiForMonitor(lpMonitor, NativeMethods.MdtEffectiveDpi, out var lpDpiX, out _) == 0)
            {
                lpDpi = lpDpiX;
            }

            var lpScale = lpDpi / 96.0;
            var lpWork = lpInfo.Work;
            // 宽度取工作区一半、顶边落在工作区 1/4 处；高度沿用配置值不变。
            var lpFrame = MainWindowLayout.Compute(
                lpWork.Left, lpWork.Top, lpWork.Right, lpWork.Bottom, lpScale, _lpConfig.WindowHeight);
            AppWindow.MoveAndResize(new RectInt32(lpFrame.X, lpFrame.Y, lpFrame.Width, lpFrame.Height));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"窗口定位失败，沿用上次位置: {ex.Message}");
        }
    }

    private void OnSearchGotFocus(object sender, RoutedEventArgs e)
    {
        if (_lpActiveSample is null)
        {
            return;
        }

        _lpActiveSample.MarkFocus(Stopwatch.GetTimestamp());
        _lpActiveSample.RecordWindowState(_lpHwnd);
    }

    /// <summary>
    /// Tab：在 全部 → 文件 → 文件夹 之间循环。焦点导航已先把焦点移走，
    /// 这里拦下事件并把焦点还给搜索框；空输入只更新提示，不触发搜索。
    /// </summary>
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab)
        {
            return;
        }

        e.Handled = true;
        SearchBox.Focus(FocusState.Programmatic);
        _lpScope = _lpScope switch
        {
            "all" => "file",
            "file" => "folder",
            _ => "all"
        };
        ScopeHintText.Text = _lpScope switch
        {
            "file" => "范围：文件",
            "folder" => "范围：文件夹",
            _ => "范围：全部"
        };
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            ShowHome();
            return;
        }

        _ = RunSearchAsync(SearchBox.Text);
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_lpActiveSample is not null && !_lpActiveSample.InputDelivered)
        {
            _lpActiveSample.MarkKey(Stopwatch.GetTimestamp());
            _lpActiveSample.RecordWindowState(_lpHwnd);
        }

        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                HideCore();
                return;

            case VirtualKey.Up:
                e.Handled = true;
                MoveSelection(-1);
                return;

            case VirtualKey.Down:
                e.Handled = true;
                MoveSelection(1);
                return;

            case VirtualKey.PageUp:
                e.Handled = true;
                MoveSelection(-PageStep);
                return;

            case VirtualKey.PageDown:
                e.Handled = true;
                MoveSelection(PageStep);
                return;

            case VirtualKey.Enter:
                e.Handled = true;
                if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                {
                    RevealSelected();
                }
                else
                {
                    OpenSelected();
                }

                return;
        }
    }

    /// <summary>Ctrl+↵：在资源管理器中选中该条目，不加入最近访问。</summary>
    private void RevealSelected()
    {
        var lpIndex = _lpSelectedIndex;
        if (lpIndex < 0 || lpIndex >= _lpRows.Count)
        {
            AppLog.Info("没有可在资源管理器中显示的条目。");
            return;
        }

        var lpRow = _lpRows[lpIndex];
        if (!ShellReveal.TryReveal(lpRow.FullPath))
        {
            ShowEmpty("\uE783", "无法在资源管理器中显示", lpRow.FullPath);
            return;
        }

        HideCore();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_lpActiveSample is not null && SearchBox.Text.Length > 0)
        {
            _lpActiveSample.InputText = SearchBox.Text;
            _lpActiveSample.MarkText(Stopwatch.GetTimestamp());
        }

        PinButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        MoreButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        ScheduleSearch();
    }

    private void ScheduleSearch()
    {
        if (_lpSuppressAutoSearch)
        {
            return;
        }

        _lpSearchTimer ??= CreateSearchTimer();
        _lpSearchTimer.Stop();
        AppLog.Debug($"输入变化: '{SearchBox.Text}'");

        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            _lpQueryGeneration++;
            _lpLastQueryMs = -1;
            ShowHome();
            return;
        }

        _lpSearchTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateSearchTimer()
    {
        var lpTimer = _lpDispatcher.CreateTimer();
        lpTimer.Interval = TimeSpan.FromMilliseconds(SearchDebounceMs);
        lpTimer.IsRepeating = false;
        lpTimer.Tick += (_, _) =>
        {
            lpTimer.Stop();
            _ = RunSearchAsync(SearchBox.Text);
        };
        return lpTimer;
    }

    private async Task<IpcReply> RunSearchAsync(string lpQuery)
    {
        var lpGeneration = ++_lpQueryGeneration;
        try
        {
            var lpRequest = new IpcRequest
            {
                Command = "SEARCH",
                Query = lpQuery,
                Limit = SearchLimit,
                Scope = _lpScope == "all" ? string.Empty : _lpScope
            };
            var lpReply = await PipeClient.SendAsync(Branding.ServicePipeName, lpRequest, TimeSpan.FromSeconds(5));

            if (lpGeneration != _lpQueryGeneration)
            {
                AppLog.Debug($"丢弃过期搜索结果: gen={lpGeneration} current={_lpQueryGeneration}");
                return lpReply;
            }

            if (!lpReply.Ok)
            {
                ApplyResults(null);
                if (lpReply.Index is { State: "building" } lpBuilding)
                {
                    // 冷启动/写入索引阶段暂无可搜快照：显示构建进度，而不是只说「尚未就绪」。
                    _lpIndexStatus = lpBuilding;
                    ShowEmpty("\uE895", "索引正在构建", lpReply.Message);
                    SetServiceStatus("索引构建中 · " + DescribeBuildProgress(lpBuilding), false);
                    StartIndexPolling();
                }
                else
                {
                    ShowEmpty("\uE783", "索引尚未就绪", lpReply.Message);
                }

                return lpReply;
            }

            _lpLastQueryMs = lpReply.QueryMs;
            ApplyResults(lpReply.Hits, lpQuery);
            AppLog.Debug($"结果已应用: q={lpQuery} hits={_lpRows.Count} queryMs={lpReply.QueryMs:0.0}");
            if (_lpRows.Count == 0)
            {
                ShowEmpty("\uE721", "没有匹配的结果", "试试更短的关键词，或文件名中的一段");
            }

            if (lpReply.Index is { State: "building" } lpBuildingStatus)
            {
                // 旧索引仍在服务查询：结果可用，但要在底栏说明「构建中」，并继续刷新进度。
                _lpIndexStatus = lpBuildingStatus;
                SetServiceStatus($"索引构建中 · 结果来自上一次索引（{lpBuildingStatus.EntryCount:N0} 条）", false);
                StartIndexPolling();
            }

            return lpReply;
        }
        catch (Exception ex)
        {
            if (lpGeneration != _lpQueryGeneration)
            {
                return IpcReply.Failure(ex.Message);
            }

            AppLog.Warn($"搜索失败（{lpQuery}）: {ex.Message}");
            ApplyResults(null);
            ShowEmpty("\uE783", "索引服务未连接", "请从托盘菜单检查服务状态");
            return IpcReply.Failure(ex.Message);
        }
    }

    private void ApplyResults(IReadOnlyList<SearchHitDto>? lpPayloadHits, string lpQuery = "")
    {
        _lpHomeMode = false;
        _lpRows.Clear();
        if (lpPayloadHits is not null)
        {
            foreach (var lpHit in lpPayloadHits)
            {
                _lpRows.Add(LpResultRow.FromDto(lpHit, lpQuery));
            }
        }

        GroupLabelText.Text = "搜索结果";
        GroupIcon.Glyph = "\uE721";
        ClearHistoryButton.Visibility = Visibility.Collapsed;
        ResultsCountText.Visibility = Visibility.Visible;
        ResultsCountText.Text = _lpRows.Count == 0
            ? string.Empty
            : (_lpLastQueryMs >= 0 ? $"{_lpRows.Count} 个结果 · {_lpLastQueryMs:0.0} ms" : $"{_lpRows.Count} 个结果");

        _lpSelectedIndex = _lpRows.Count > 0 ? 0 : -1;
        RefreshSelection();
        UpdateEmptyState();
        ServiceStatusText.Text = _lpLastQueryMs >= 0 ? $"{_lpLastQueryMs:0.0} ms" : _lpServiceStatus;
        UpdateStatusDot(_lpServiceConnected);
        if (_lpSelectedIndex >= 0)
        {
            ScrollSelectionIntoView();
        }
    }

    private void ShowHome()
    {
        _lpHomeMode = true;
        _lpRows.Clear();
        var lpRecent = RecentStore.Load().Take(6).ToList();
        foreach (var lpEntry in lpRecent)
        {
            _lpRows.Add(LpResultRow.FromRecent(lpEntry));
        }

        GroupLabelText.Text = "最近访问";
        GroupIcon.Glyph = "\uE81C";
        ClearHistoryButton.Visibility = lpRecent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsCountText.Visibility = Visibility.Collapsed;
        // 设计稿的空输入首页不预选任何一行；按 ↓ 才出现选中态。
        _lpSelectedIndex = -1;
        RefreshSelection();
        UpdateEmptyState();
        ServiceStatusText.Text = _lpServiceStatus;
        UpdateStatusDot(_lpServiceConnected);
    }

    private void OnRecentUpdated()
    {
        if (_lpHomeMode)
        {
            ShowHome();
        }
    }

    private void UpdateEmptyState()
    {
        if (_lpRows.Count > 0)
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            ResultsScroller.Visibility = Visibility.Visible;
            return;
        }

        ResultsScroller.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
        if (_lpHomeMode)
        {
            ShowEmpty("\uE81C", "还没有最近访问", "打开任意搜索结果后，会出现在这里");
        }
        else
        {
            ShowEmpty("\uE721", "没有匹配的结果", "试试更短的关键词，或文件名中的一段");
        }
    }

    private void ShowEmpty(string lpGlyph, string lpTitle, string lpHint)
    {
        EmptyIcon.Glyph = lpGlyph;
        EmptyTitleText.Text = lpTitle;
        EmptyHintText.Text = lpHint;
        EmptyPanel.Visibility = Visibility.Visible;
        ResultsScroller.Visibility = Visibility.Collapsed;
    }

    private void MoveSelection(int lpDelta)
    {
        if (_lpRows.Count == 0)
        {
            return;
        }

        _lpSelectedIndex = Math.Clamp(_lpSelectedIndex + lpDelta, 0, _lpRows.Count - 1);
        RefreshSelection();
        ScrollSelectionIntoView();
    }

    private void RefreshSelection()
    {
        for (var lpIndex = 0; lpIndex < _lpRows.Count; lpIndex++)
        {
            _lpRows[lpIndex].IsSelected = lpIndex == _lpSelectedIndex;
        }
    }

    private void ScrollSelectionIntoView()
    {
        if (_lpSelectedIndex < 0)
        {
            return;
        }

        var lpRowHeight = _lpTheme.RowHeight + 1;
        var lpOffset = Math.Max(0, _lpSelectedIndex * lpRowHeight - 24);
        ResultsScroller.ChangeView(null, lpOffset, null, disableAnimation: true);
    }

    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement lpElement && lpElement.DataContext is LpResultRow lpRow)
        {
            lpRow.IsHovered = true;
        }
    }

    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement lpElement && lpElement.DataContext is LpResultRow lpRow)
        {
            lpRow.IsHovered = false;
        }
    }

    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement lpElement && lpElement.DataContext is LpResultRow lpRow)
        {
            var lpIndex = _lpRows.IndexOf(lpRow);
            if (lpIndex >= 0)
            {
                _lpSelectedIndex = lpIndex;
                RefreshSelection();
            }

            OpenSelected();
        }
    }

    private void OnRowMenuOpen(object sender, RoutedEventArgs e)
    {
        if (SelectMenuRow(sender) is not null)
        {
            OpenSelected();
        }
    }

    private void OnRowMenuReveal(object sender, RoutedEventArgs e)
    {
        var lpRow = SelectMenuRow(sender);
        if (lpRow is null)
        {
            return;
        }

        if (!ShellReveal.TryReveal(lpRow.FullPath))
        {
            ShowEmpty("\uE783", "无法在资源管理器中显示", lpRow.FullPath);
            return;
        }

        HideCore();
    }

    private void OnRowMenuCopyPath(object sender, RoutedEventArgs e)
    {
        var lpRow = SelectMenuRow(sender);
        if (lpRow is null)
        {
            return;
        }

        try
        {
            var lpPackage = new DataPackage();
            lpPackage.SetText(lpRow.FullPath);
            Clipboard.SetContent(lpPackage);
            try
            {
                Clipboard.Flush();
            }
            catch (Exception lpFlushEx)
            {
                // 部分环境下 Flush 拿不到剪贴板所有权，但内容已在 SetContent 时写入
                AppLog.Warn($"剪贴板 Flush 失败（内容已写入）: {lpFlushEx.Message}");
            }

            AppLog.Info($"已复制路径: {lpRow.FullPath}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"复制路径失败 {lpRow.FullPath}: {ex.Message}");
            ShowEmpty("\uE783", "无法复制路径", ex.Message);
        }
    }

    /// <summary>右键菜单命中的行；菜单项拿不到 DataContext 时退回当前选中行。</summary>
    private LpResultRow? SelectMenuRow(object sender)
    {
        if (sender is FrameworkElement lpElement && lpElement.DataContext is LpResultRow lpRow)
        {
            var lpIndex = _lpRows.IndexOf(lpRow);
            if (lpIndex >= 0)
            {
                _lpSelectedIndex = lpIndex;
                RefreshSelection();
            }

            return lpRow;
        }

        return _lpSelectedIndex >= 0 && _lpSelectedIndex < _lpRows.Count ? _lpRows[_lpSelectedIndex] : null;
    }

    /// <summary>↵/单击：目录用资源管理器打开，文件交给系统默认程序；记录最近访问后隐藏窗口。</summary>
    private void OpenSelected()
    {
        var lpIndex = _lpSelectedIndex;
        if (lpIndex < 0 || lpIndex >= _lpRows.Count)
        {
            AppLog.Info("没有可打开的搜索结果。");
            return;
        }

        var lpRow = _lpRows[lpIndex];
        try
        {
            var lpStartInfo = lpRow.IsDirectory
                ? new ProcessStartInfo("explorer.exe", $"\"{lpRow.FullPath}\"") { UseShellExecute = true }
                : new ProcessStartInfo(lpRow.FullPath) { UseShellExecute = true };
            Process.Start(lpStartInfo);
            RecentStore.Record(lpRow.FullPath, lpRow.Name, lpRow.IsDirectory);
            AppLog.Info($"已打开 {(lpRow.IsBrowser ? "网页" : lpRow.IsDirectory ? "目录" : "文件")}: {lpRow.FullPath}");
            HideCore();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开失败 {lpRow.FullPath}: {ex.Message}");
            ShowEmpty("\uE783", "无法打开该条目", ex.Message);
        }
    }

    internal async Task<IpcReply> QueryForTestAsync(string lpQuery)
    {
        _lpSuppressAutoSearch = true;
        try
        {
            _lpSearchTimer?.Stop();
            SearchBox.Text = lpQuery;
            var lpReply = await RunSearchAsync(lpQuery);
            return BuildSelectionReply(lpReply.Message);
        }
        finally
        {
            _lpSearchTimer?.Stop();
            _lpSuppressAutoSearch = false;
        }
    }

    internal IpcReply SelectForTest(int lpDelta)
    {
        MoveSelection(lpDelta);
        return BuildSelectionReply("select");
    }

    private IpcReply BuildSelectionReply(string lpMessage)
    {
        return new IpcReply
        {
            Ok = true,
            Message = $"{lpMessage} results={_lpRows.Count} selected={_lpSelectedIndex}",
            QueryMs = _lpLastQueryMs,
            CandidateCount = _lpRows.Count,
            Hits = _lpRows.Select(static lpRow => lpRow.ToDto()).ToList()
        };
    }

    private void OnRendering(object? sender, object e)
    {
        if (_lpActiveSample is not null && !_lpActiveSample.FirstFrameTick.HasValue)
        {
            _lpActiveSample.FirstFrameTick = Stopwatch.GetTimestamp();
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated
            && _lpConfig.HideOnDeactivate
            && _lpVisible)
        {
            var lpElapsedMs = (Stopwatch.GetTimestamp() - _lpShownTick) * 1000.0 / Stopwatch.Frequency;
            if (lpElapsedMs < DeactivateGraceMs)
            {
                AppLog.Debug($"忽略唤出后 {lpElapsedMs:0.0}ms 的短暂失焦");
                return;
            }

            HideCore();
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_lpAllowClose)
        {
            return;
        }

        args.Cancel = true;
        HideCore();
    }

    public void PrepareShutdown()
    {
        _lpAllowClose = true;
        _lpTheme.Changed -= OnThemeChanged;
        RecentStore.Updated -= OnRecentUpdated;
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _lpConfig.PinWindow = !_lpConfig.PinWindow;
        _lpTheme.SaveConfig();
        if (AppWindow.Presenter is OverlappedPresenter lpPresenter)
        {
            lpPresenter.IsAlwaysOnTop = _lpConfig.PinWindow;
        }

        PinButton.Opacity = _lpConfig.PinWindow ? 1.0 : 0.45;
        AppLog.Info($"窗口置顶: {_lpConfig.PinWindow}");
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        RecentStore.Clear();
        ShowHome();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnOpenQuickSwitchClick(object sender, RoutedEventArgs e) => QuickSwitchRequested?.Invoke();

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e) => UpdateRequested?.Invoke();

    private void OnAboutClick(object sender, RoutedEventArgs e) => AboutRequested?.Invoke();
}

/// <summary>结果 / 最近访问一行的展示模型，选中态与主题/密度变化时刷新自身画刷。</summary>
public sealed class LpResultRow : INotifyPropertyChanged
{
    private static readonly Color FileColor = Color.FromArgb(255, 76, 134, 240);
    private static readonly Color FolderColor = Color.FromArgb(255, 215, 160, 58);
    private static readonly Color AppColor = Color.FromArgb(255, 151, 121, 242);
    private static readonly Color BrowserColor = Color.FromArgb(255, 63, 174, 149);

    private bool _lpIsSelected;
    private bool _lpIsHovered;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; init; } = string.Empty;

    public string FullPath { get; init; } = string.Empty;

    public string FolderPath { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;

    public string Glyph { get; init; } = "\uE7C3";

    public string Tag { get; init; } = "文件";

    public string TimeText { get; init; } = string.Empty;

    public bool IsDirectory { get; init; }

    /// <summary>浏览器书签 / 历史条目：Path 是 URL，结果行显示「网页」。</summary>
    public bool IsBrowser { get; init; }

    public long Size { get; init; }

    public string? ModifiedAt { get; init; }

    public int Tier { get; init; }

    public double Score { get; init; }

    public bool IsSelected
    {
        get => _lpIsSelected;
        set
        {
            if (_lpIsSelected == value)
            {
                return;
            }

            _lpIsSelected = value;
            RefreshVisuals();
        }
    }

    public bool IsHovered
    {
        get => _lpIsHovered;
        set
        {
            if (_lpIsHovered == value)
            {
                return;
            }

            _lpIsHovered = value;
            Raise(nameof(RowBackground));
        }
    }

    public double RowHeight => ThemeService.Instance.RowHeight;

    public double NameFontSize => ThemeService.Instance.RowNameFontSize;

    public Windows.UI.Text.FontWeight NameWeight => IsSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public Visibility PathVisibility => ThemeService.Instance.ShowRowPath ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EnterVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TagVisibility => string.IsNullOrEmpty(TimeText) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TimeVisibility => string.IsNullOrEmpty(TimeText) ? Visibility.Collapsed : Visibility.Visible;

    public Brush RowBackground =>
        IsSelected ? ThemeService.Instance.SelectionBrush :
        IsHovered ? ThemeService.Instance.RowHoverBrush :
        ThemeService.Instance.TransparentBrush;

    public Brush RowBorderBrush =>
        IsSelected ? ThemeService.Instance.SelectionBorderBrush : ThemeService.Instance.TransparentBrush;

    public Brush EnterBackground => ThemeService.Instance.AccentBrush;

    public Brush EnterForeground => ThemeService.Instance.AccentInkBrush;

    public Brush IconBackground { get; init; } = ThemeService.Instance.TransparentBrush;

    public Brush IconBorderBrush { get; init; } = ThemeService.Instance.TransparentBrush;

    public Brush IconForeground { get; init; } = ThemeService.Instance.TransparentBrush;

    public static LpResultRow FromDto(SearchHitDto lpDto, string lpQuery)
    {
        var lpBrowser = lpDto.IsBrowser;
        var lpFolder = lpBrowser
            ? lpDto.Path
            : System.IO.Path.GetDirectoryName(lpDto.Path) ?? string.Empty;
        var lpIsApp = !lpDto.IsDirectory && !lpBrowser && (lpDto.IsProgram || IsProgram(lpDto.Name));
        var lpColor = lpBrowser ? BrowserColor : lpDto.IsDirectory ? FolderColor : (lpIsApp ? AppColor : FileColor);
        return new LpResultRow
        {
            Name = lpDto.Name,
            FullPath = lpDto.Path,
            FolderPath = lpFolder,
            Query = lpQuery,
            Glyph = lpBrowser ? "\uE774" : lpDto.IsDirectory ? "\uE8B7" : (lpIsApp ? "\uE71D" : "\uE7C3"),
            Tag = lpBrowser ? "网页" : lpDto.IsDirectory ? "文件夹" : (lpIsApp ? "程序" : "文件"),
            IsDirectory = lpDto.IsDirectory,
            IsBrowser = lpBrowser,
            Size = lpDto.Size,
            ModifiedAt = lpDto.ModifiedAt,
            Tier = lpDto.Tier,
            Score = lpDto.Score,
            IconForeground = new SolidColorBrush(lpColor),
            IconBackground = new SolidColorBrush(Color.FromArgb(26, lpColor.R, lpColor.G, lpColor.B)),
            IconBorderBrush = new SolidColorBrush(Color.FromArgb(48, lpColor.R, lpColor.G, lpColor.B))
        };
    }

    public static LpResultRow FromRecent(RecentEntry lpEntry)
    {
        var lpBrowser = !lpEntry.IsDirectory
            && (lpEntry.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || lpEntry.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        var lpIsApp = !lpEntry.IsDirectory && !lpBrowser && IsProgram(lpEntry.Name);
        var lpColor = lpBrowser ? BrowserColor : lpEntry.IsDirectory ? FolderColor : (lpIsApp ? AppColor : FileColor);
        return new LpResultRow
        {
            Name = lpEntry.Name,
            FullPath = lpEntry.Path,
            FolderPath = lpBrowser
                ? lpEntry.Path
                : lpEntry.IsDirectory
                    ? System.IO.Path.GetDirectoryName(lpEntry.Path) ?? lpEntry.Path
                    : System.IO.Path.GetDirectoryName(lpEntry.Path) ?? string.Empty,
            Glyph = lpBrowser ? "\uE774" : lpEntry.IsDirectory ? "\uE8B7" : (lpIsApp ? "\uE71D" : "\uE7C3"),
            Tag = lpBrowser ? "网页" : lpEntry.IsDirectory ? "文件夹" : (lpIsApp ? "程序" : "文件"),
            IsDirectory = lpEntry.IsDirectory,
            IsBrowser = lpBrowser,
            TimeText = RelativeTime(lpEntry.AccessedAt),
            IconForeground = new SolidColorBrush(lpColor),
            IconBackground = new SolidColorBrush(Color.FromArgb(26, lpColor.R, lpColor.G, lpColor.B)),
            IconBorderBrush = new SolidColorBrush(Color.FromArgb(48, lpColor.R, lpColor.G, lpColor.B))
        };
    }

    public SearchHitDto ToDto() => new()
    {
        Name = Name,
        Path = FullPath,
        IsDirectory = IsDirectory,
        IsBrowser = IsBrowser,
        Size = Size,
        ModifiedAt = ModifiedAt,
        Tier = Tier,
        Score = Score
    };

    public void RefreshVisuals()
    {
        Raise(nameof(RowBackground));
        Raise(nameof(RowBorderBrush));
        Raise(nameof(RowHeight));
        Raise(nameof(NameFontSize));
        Raise(nameof(NameWeight));
        Raise(nameof(PathVisibility));
        Raise(nameof(EnterVisibility));
        Raise(nameof(EnterBackground));
        Raise(nameof(TagVisibility));
        Raise(nameof(TimeVisibility));
    }

    private void Raise(string lpName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(lpName));
    }

    private static bool IsProgram(string lpName)
    {
        var lpExtension = System.IO.Path.GetExtension(lpName);
        return lpExtension is ".exe" or ".lnk" or ".bat" or ".cmd" or ".msi" or ".com" or ".ps1";
    }

    private static string RelativeTime(DateTimeOffset lpTime)
    {
        var lpDelta = DateTimeOffset.Now - lpTime;
        if (lpDelta < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (lpDelta < TimeSpan.FromHours(1))
        {
            return $"{lpDelta.Minutes} 分钟前";
        }

        if (lpDelta < TimeSpan.FromDays(1))
        {
            return $"{lpDelta.Hours} 小时前";
        }

        if (lpDelta < TimeSpan.FromDays(30))
        {
            return $"{lpDelta.Days} 天前";
        }

        return lpTime.ToString("yyyy-MM-dd");
    }
}
