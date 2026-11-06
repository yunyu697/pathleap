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
using Pathleap.UI.Perf;
using Pathleap.UI.Platform;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace Pathleap.UI;

/// <summary>
/// M1 空搜索窗：无标题栏、置顶、失焦自动隐藏。
/// 预创建且默认不 Activate，保证首帧不在启动阶段渲染；每次唤出时记录 QPC 打点供基准测试使用。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>唤出后的失焦宽限期，避免前台锁竞争导致的闪隐。</summary>
    private const double DeactivateGraceMs = 250;

    private readonly AppConfig _lpConfig;
    private readonly IntPtr _lpHwnd;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _lpDispatcher;
    private WakeSample? _lpPendingSample;
    private WakeSample? _lpActiveSample;
    private bool _lpVisible;
    private bool _lpAllowClose;
    private long _lpShownTick;

    public MainWindow(AppConfig config)
    {
        _lpConfig = config;
        InitializeComponent();

        _lpHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _lpDispatcher = DispatcherQueue;
        Title = $"{Branding.DisplayName} · {Branding.ChineseName}";
        BrandMarkText.Text = Branding.Mark;
        VersionText.Text = $"M1 · {Branding.Version}";

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
        _lpShownTick = Stopwatch.GetTimestamp();
        Activate();
        NativeMethods.SetForegroundWindow(_lpHwnd);
        SearchBox.Focus(FocusState.Programmatic);
        AppLog.Debug("搜索窗已显示并请求焦点");
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
        if (_lpVisible)
        {
            _lpVisible = false;
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

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            HideCore();
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            AppLog.Info("M1 空壳：无搜索结果，Enter 不执行任何动作。");
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_lpActiveSample is not null && SearchBox.Text.Length > 0)
        {
            _lpActiveSample.InputText = SearchBox.Text;
            _lpActiveSample.MarkText(Stopwatch.GetTimestamp());
        }
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
