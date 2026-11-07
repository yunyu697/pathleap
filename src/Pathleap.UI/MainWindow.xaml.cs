using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pathleap.Shared;
using Pathleap.Shared.Config;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Ipc;
using Pathleap.UI.Perf;
using Pathleap.UI.Platform;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace Pathleap.UI;

/// <summary>
/// 搜索窗：无标题栏、置顶、失焦自动隐藏。
/// 输入经防抖后走命名管道向服务查询，结果列表支持 ↑↓/PgUp/PgDn 选择、↵ 打开、Esc 隐藏。
/// 预创建且默认不 Activate，保证首帧不在启动阶段渲染；每次唤出时记录 QPC 打点供基准测试使用。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>唤出后的失焦宽限期，避免前台锁竞争导致的闪隐。</summary>
    private const double DeactivateGraceMs = 250;

    /// <summary>前台锁重试间隔与次数：Windows 允许前台进程抢焦点，非前台进程会被静默拒绝。</summary>
    private const int ForegroundRetryDelayMs = 120;
    private const int ForegroundRetryAttempts = 2;

    /// <summary>输入防抖：连续敲键时只在停手后发一次查询。</summary>
    private const int SearchDebounceMs = 120;

    /// <summary>结果条数上限（与服务端默认 30 条对齐但略小，一屏内可扫完）。</summary>
    private const int SearchLimit = 24;

    /// <summary>PgUp/PgDn 每次跨过的行数。</summary>
    private const int PageStep = 6;

    private readonly AppConfig _lpConfig;
    private readonly IntPtr _lpHwnd;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _lpDispatcher;
    private readonly ObservableCollection<ResultRow> _lpRows = new();
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
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _lpForegroundRetry;

    public MainWindow(AppConfig config)
    {
        _lpConfig = config;
        InitializeComponent();

        _lpHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _lpDispatcher = DispatcherQueue;
        Title = $"{Branding.DisplayName} · {Branding.ChineseName}";
        BrandMarkText.Text = Branding.Mark;
        VersionText.Text = Branding.Version;
        BadgeText.Text = Branding.Version;
        ResultsList.ItemsSource = _lpRows;

        ConfigureWindow();

        SearchBox.KeyDown += OnSearchKeyDown;
        SearchBox.GotFocus += OnSearchGotFocus;
        SearchBox.TextChanged += OnSearchTextChanged;
        CompositionTarget.Rendering += OnRendering;
        Activated += OnActivated;
        AppWindow.Closing += OnAppWindowClosing;

        AppLog.Info($"主窗口已创建（未显示），HWND=0x{_lpHwnd.ToInt64():X}");
    }

    public Microsoft.UI.Dispatching.DispatcherQueue UiDispatcher => _lpDispatcher;

    /// <summary>
    /// 当前是否确实处于前台且可见。基准测试在注入探测键前必须校验这一条，
    /// 否则探测键会打到用户当前正在使用的窗口上。
    /// </summary>
    public bool IsForegroundAndVisible()
    {
        return NativeMethods.GetForegroundWindow().Equals(_lpHwnd) && NativeMethods.IsWindowVisible(_lpHwnd);
    }

    private void ConfigureWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter lpPresenter)
        {
            lpPresenter.IsAlwaysOnTop = true;
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

        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Acrylic 背景不可用，回退纯色: {ex.Message}");
        }
    }

    /// <summary>基准测试预置一次采样；随后注入热键，由 WM_HOTKEY 的 T0 触发显示。</summary>
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

    /// <summary>由热键路径调用：tick 必须是 WM_HOTKEY 处理现场取得的 QPC 时间戳。</summary>
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

    /// <summary>托盘 / 命令行唤出，不做采样。</summary>
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
        ScheduleForegroundRetry(_lpVisibleSession, ForegroundRetryAttempts);
    }

    /// <summary>
    /// 前台锁重试：非前台进程调用 SetForegroundWindow 会被静默拒绝（返回 false 且不报错），
    /// 表现为窗口已显示但没有焦点。这里在 120ms 后重试，并用 AttachThreadInput 把输入队列
    /// 挂到当前前台线程上，使抢占前台能够成功。
    /// </summary>
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
        ServiceStatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(connected
            ? Color.FromArgb(255, 71, 191, 122)
            : Color.FromArgb(255, 199, 122, 24));
    }

    /// <summary>
    /// 定位到鼠标所在显示器的上部 20% 居中位置。
    /// 坐标全部来自 Win32（GetMonitorInfoW），避免 WinRT DisplayArea 在部分环境下的调用失败。
    /// </summary>
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
            var lpWidth = (int)Math.Round(_lpConfig.WindowWidth * lpScale);
            var lpHeight = (int)Math.Round(_lpConfig.WindowHeight * lpScale);
            var lpWork = lpInfo.Work;
            var lpX = lpWork.Left + (lpWork.Right - lpWork.Left - lpWidth) / 2;
            var lpY = lpWork.Top + (int)Math.Round((lpWork.Bottom - lpWork.Top) * 0.2);
            AppWindow.MoveAndResize(new RectInt32(lpX, lpY, lpWidth, lpHeight));
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
                OpenSelected();
                return;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_lpActiveSample is not null && SearchBox.Text.Length > 0)
        {
            _lpActiveSample.InputText = SearchBox.Text;
            _lpActiveSample.MarkText(Stopwatch.GetTimestamp());
        }

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
            ApplyResults(null);
            ShowEmpty("\uE721", "输入关键词开始搜索", "索引由后台服务提供 · ↑↓ 选择 · ↵ 打开 · Esc 隐藏");
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

    /// <summary>把查询发到服务管道，并在结果回来时更新列表（过期的响应直接丢弃）。</summary>
    private async Task<IpcReply> RunSearchAsync(string lpQuery)
    {
        var lpGeneration = ++_lpQueryGeneration;
        try
        {
            var lpRequest = new IpcRequest { Command = "SEARCH", Query = lpQuery, Limit = SearchLimit };
            var lpReply = await PipeClient.SendAsync(Branding.ServicePipeName, lpRequest, TimeSpan.FromSeconds(5));

            if (lpGeneration != _lpQueryGeneration)
            {
                AppLog.Debug($"丢弃过期搜索结果: gen={lpGeneration} current={_lpQueryGeneration}");
                return lpReply;
            }

            if (!lpReply.Ok)
            {
                ApplyResults(null);
                ShowEmpty("\uE783", "索引尚未就绪", lpReply.Message);
                return lpReply;
            }

            _lpLastQueryMs = lpReply.QueryMs;
            ApplyResults(lpReply.Hits);
            AppLog.Debug($"结果已应用: q={lpQuery} hits={_lpRows.Count} queryMs={lpReply.QueryMs:0.0}");
            if (_lpRows.Count == 0)
            {
                ShowEmpty("\uE721", "没有匹配的结果", "试试更短的关键词，或文件名中的一段");
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

    private void ApplyResults(IReadOnlyList<SearchHitDto>? lpPayloadHits)
    {
        _lpRows.Clear();
        if (lpPayloadHits is not null)
        {
            foreach (var lpHit in lpPayloadHits)
            {
                _lpRows.Add(ResultRow.FromDto(lpHit));
            }
        }

        if (_lpRows.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
            EmptyPanel.Visibility = Visibility.Collapsed;
            ResultsList.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyPanel.Visibility = Visibility.Visible;
            ResultsList.Visibility = Visibility.Collapsed;
        }

        ResultsCountText.Text = _lpRows.Count == 0
            ? string.Empty
            : (_lpLastQueryMs >= 0
                ? $"{_lpRows.Count} 条 · {_lpLastQueryMs:0.0} ms"
                : $"{_lpRows.Count} 条");
    }

    private void ShowEmpty(string lpGlyph, string lpTitle, string lpHint)
    {
        EmptyIcon.Glyph = lpGlyph;
        EmptyTitleText.Text = lpTitle;
        EmptyHintText.Text = lpHint;
        EmptyPanel.Visibility = Visibility.Visible;
        ResultsList.Visibility = Visibility.Collapsed;
    }

    private void MoveSelection(int lpDelta)
    {
        if (_lpRows.Count == 0)
        {
            return;
        }

        var lpNext = ResultsList.SelectedIndex + lpDelta;
        lpNext = Math.Clamp(lpNext, 0, _lpRows.Count - 1);
        ResultsList.SelectedIndex = lpNext;
        ResultsList.ScrollIntoView(_lpRows[lpNext]);
    }

    /// <summary>↵：目录用资源管理器打开，文件交给系统默认程序；打开后立即隐藏窗口。</summary>
    private void OpenSelected()
    {
        var lpIndex = ResultsList.SelectedIndex;
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
            AppLog.Info($"已打开 {(lpRow.IsDirectory ? "目录" : "文件")}: {lpRow.FullPath}");
            HideCore();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开失败 {lpRow.FullPath}: {ex.Message}");
            ShowEmpty("\uE783", "无法打开该条目", ex.Message);
        }
    }

    /// <summary>
    /// 自动化/后台验证入口：等价于用户输入这段文本并等结果返回（不注入真实键鼠、不抢前台）。
    /// </summary>
    internal async Task<IpcReply> QueryForTestAsync(string lpQuery)
    {
        // TextBox.TextChanged 是异步派发的：置文本后它可能在 await 期间才触发并启动防抖定时器，
        // 导致同一查询被跑两次、先到的响应被当成过期结果丢弃。自动化路径全程抑制防抖。
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

    /// <summary>自动化/后台验证入口：等价于按 ↑/↓ 移动选择。</summary>
    internal IpcReply SelectForTest(int lpDelta)
    {
        MoveSelection(lpDelta);
        return BuildSelectionReply("select");
    }

    private IpcReply BuildSelectionReply(string lpMessage)
    {
        var lpIndex = ResultsList.SelectedIndex;
        return new IpcReply
        {
            Ok = true,
            Message = $"{lpMessage} results={_lpRows.Count} selected={lpIndex}",
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
            // 刚唤出时 Windows 前台锁可能造成一次短暂失焦，不应让窗口闪一下就消失。
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
    }
}

/// <summary>结果列表一行的展示模型（名字、所在目录、大小文本、图标字形）。</summary>
public sealed class ResultRow
{
    private const string FolderGlyph = "\uE8B7";
    private const string FileGlyph = "\uE7C3";

    public string Name { get; init; } = string.Empty;

    public string FullPath { get; init; } = string.Empty;

    public string FolderPath { get; init; } = string.Empty;

    public string SizeText { get; init; } = string.Empty;

    public string Glyph { get; init; } = FileGlyph;

    public bool IsDirectory { get; init; }

    public long Size { get; init; }

    public string? ModifiedAt { get; init; }

    public int Tier { get; init; }

    public double Score { get; init; }

    public static ResultRow FromDto(SearchHitDto lpDto)
    {
        var lpFolder = System.IO.Path.GetDirectoryName(lpDto.Path) ?? string.Empty;
        return new ResultRow
        {
            Name = lpDto.Name,
            FullPath = lpDto.Path,
            FolderPath = lpFolder,
            SizeText = lpDto.IsDirectory ? string.Empty : FormatSize(lpDto.Size),
            Glyph = lpDto.IsDirectory ? FolderGlyph : FileGlyph,
            IsDirectory = lpDto.IsDirectory,
            Size = lpDto.Size,
            ModifiedAt = lpDto.ModifiedAt,
            Tier = lpDto.Tier,
            Score = lpDto.Score
        };
    }

    public SearchHitDto ToDto() => new()
    {
        Name = Name,
        Path = FullPath,
        IsDirectory = IsDirectory,
        Size = Size,
        ModifiedAt = ModifiedAt,
        Tier = Tier,
        Score = Score
    };

    private static string FormatSize(long lpBytes)
    {
        if (lpBytes < 1024)
        {
            return $"{lpBytes} B";
        }

        var lpKb = lpBytes / 1024.0;
        if (lpKb < 1024)
        {
            return $"{lpKb:0.#} KB";
        }

        var lpMb = lpKb / 1024.0;
        return lpMb < 1024 ? $"{lpMb:0.#} MB" : $"{lpMb / 1024.0:0.#} GB";
    }
}
