using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pathleap.Shared;
using Pathleap.Shared.Config;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Ipc;
using Pathleap.UI.Data;
using Pathleap.UI.Platform;
using Pathleap.UI.Theme;
using Windows.Graphics;
using Windows.UI;

namespace Pathleap.UI;

/// <summary>
/// 快速切换栏：吸附在文件对话框底边、宽度为对话框的一半，每个对话框各有一条、互不串位。
///
/// 技术选择（实测验证见 docs/quick-switch-attach.md）：SetParent 子窗口在层级/裁剪/随动上天然正确，
/// 实测也能渲染内容，但文件对话框客户区底部就是「文件名 + 打开/取消」操作行，任何嵌进客户区的栏
/// 都会遮挡这些控件，与「任何时候不遮挡内容区」冲突；因此改用 owner 窗口：GWLP_HWNDPARENT 指向对话框，
/// 窗口贴在其底边外侧 2px（下方空间不足时翻到上方），以 WinEvent 事件随动。
/// owner 关系保证随对话框最小化隐藏、随其销毁而销毁、z 序只排在对话框之上，不会漂到其他应用前面；
/// 多个对话框各有一个 owner，天然隔离，键盘只路由给前台对话框对应的那一条。
/// </summary>
public sealed partial class QuickSwitchWindow : Window
{
    private const int MaxRows = QuickSwitchLayout.MaxRows;
    private const int SearchLimit = 6;
    private static readonly Color FolderColor = Color.FromArgb(255, 215, 160, 58);
    private static readonly Color FileColor = Color.FromArgb(255, 76, 134, 240);

    private readonly AppConfig _lpConfig;
    private readonly List<QuickFolder> _lpFolders = new();
    private readonly List<SearchHitDto> _lpResults = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _lpSearchTimer;
    private FileDialogInfo _lpDialog = FileDialogInfo.None;
    private IntPtr _lpHandle = IntPtr.Zero;
    private bool _lpDetached;
    private IntPtr _lpOwner;
    private bool _lpKeyboardActive;
    private bool _lpInputMode;
    private bool _lpShown;
    private int _lpSelected;
    private int _lpResultSelected;
    private int _lpColumns = 6;
    private int _lpBarWidthPx = 426;
    private int _lpBarHeightPx = 78;
    private double _lpScale = 1.0;
    private bool _lpForegroundActive = true;
    private bool _lpSearchMode;
    private bool _lpSearching;
    private int _lpSearchGeneration;
    private string _lpBodyKey = string.Empty;

    private readonly record struct QuickFolder(string Name, string Path, bool IsCurrent);

    /// <summary>键盘模式结束（Esc、选中、失活），宿主据此清空「当前响应键盘的对话框」。</summary>
    public event Action? KeyboardEnded;

    /// <summary>窗口确实关闭（含对话框销毁导致的关闭），宿主据此从映射表移除。</summary>
    public event Action? BarClosed;

    public QuickSwitchWindow(AppConfig config)
    {
        _lpConfig = config;
        InitializeComponent();

        Title = $"{Branding.DisplayName} 快速切换";
        _lpHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ConfigureWindow();

        _lpSearchTimer = DispatcherQueue.CreateTimer();
        _lpSearchTimer.Interval = TimeSpan.FromMilliseconds(140);
        _lpSearchTimer.IsRepeating = false;
        _lpSearchTimer.Tick += (_, _) => _ = RunSearchAsync(SearchBox.Text);
        SearchBox.TextChanged += OnSearchTextChanged;
        SearchBox.PreviewKeyDown += OnSearchPreviewKeyDown;
        SearchBox.GotFocus += OnSearchGotFocus;

        ApplyTheme();
        LoadFolders();
        RefreshBody(true);
        AppWindow.Changed += OnAppWindowChanged;
        Activated += OnActivated;
        Closed += OnClosed;
    }

    /// <summary>
    /// 用户点到别处（栏失去激活）时结束输入态：否则下一次 Ctrl+G 会以为已经在输入态而不再取焦点。
    /// </summary>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated || !_lpInputMode)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_lpInputMode && QuickSwitchNative.GetForegroundWindow() != _lpHandle)
            {
                EndKeyboardMode(false);
            }
        });
    }

    /// <summary>
    /// owner 被还原时系统会把它的附属窗口一并自动显示，这一步可能发生在几何事件之后。
    /// 后台对话框的栏一旦被系统显示出来，立即收回，保证「只显示前台那条」在任何时点都成立。
    /// </summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange && sender.IsVisible && !_lpDetached && !_lpForegroundActive)
        {
            QuickSwitchNative.ShowWindow(_lpHandle, QuickSwitchNative.SwHide);
        }
    }

    public IntPtr DialogHandle => _lpDialog.Handle;

    /// <summary>栏自身的窗口句柄；宿主据此判断前台是否已经落在栏上（输入态）。</summary>
    public IntPtr Handle => _lpHandle;

    public bool IsAttached => _lpDialog.IsFileDialog;

    public bool IsKeyboardActive => _lpKeyboardActive;

    /// <summary>
    /// 只让前台对话框对应的那条栏可见。栏在对话框矩形之外，两个对话框互相重叠时，
    /// 后台那条必然压在更前面或更后面窗口上；直接隐藏即可彻底避免遮挡，切回时再显示。
    /// </summary>
    public void SetForegroundActive(bool active)
    {
        if (_lpDetached || !IsAttached || _lpForegroundActive == active)
        {
            return;
        }

        _lpForegroundActive = active;

        // 最小化的对话框即使仍处于前台也不显示栏：栏会停在最小化前的坐标上，浮在桌面。
        var lpShow = active && !QuickSwitchNative.IsIconic(_lpDialog.Handle);
        QuickSwitchNative.ShowWindow(_lpHandle, lpShow ? QuickSwitchNative.SwShowNoActivate : QuickSwitchNative.SwHide);
    }

    private void ConfigureWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter lpPresenter)
        {
            lpPresenter.IsResizable = false;
            lpPresenter.IsMaximizable = false;
            lpPresenter.IsMinimizable = false;
            try
            {
                lpPresenter.SetBorderAndTitleBar(false, false);
            }
            catch (Exception ex)
            {
                AppLog.Debug($"快速切换无边框设置跳过: {ex.Message}");
            }
        }

        AppWindow.IsShownInSwitchers = false;

        // 工具窗口 + 永不激活：显示/点击都不抢走文件对话框的焦点。
        var lpExStyle = QuickSwitchNative.GetWindowLongPtr(_lpHandle, QuickSwitchNative.GwlExstyle).ToInt64();
        lpExStyle |= QuickSwitchNative.WsExToolWindow | QuickSwitchNative.WsExNoActivate;
        QuickSwitchNative.SetWindowLongPtr(
            _lpHandle,
            QuickSwitchNative.GwlExstyle,
            (IntPtr)lpExStyle);
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = ThemeService.Instance.ElementTheme;
        RootBorder.Background = ThemeService.Instance.SurfaceBrush;
        RootBorder.BorderBrush = _lpKeyboardActive
            ? ThemeService.Instance.AccentBrush
            : ThemeService.Instance.Border1Brush;
        RootBorder.BorderThickness = new Thickness(_lpKeyboardActive ? 1.5 : 1);

        BrandDot.Background = ThemeService.Instance.BrandMarkBrush;
        TitleText.Foreground = ThemeService.Instance.Text1Brush;
        SubtitleText.Foreground = ThemeService.Instance.Text3Brush;
        CountText.Foreground = ThemeService.Instance.Text3Brush;
        ModeHintText.Foreground = ThemeService.Instance.Text3Brush;
        HintChip.Background = ThemeService.Instance.TrackOffBrush;
        HintChip.BorderBrush = ThemeService.Instance.TrackOffBorderBrush;

        SearchBox.Background = ThemeService.Instance.Surface2Brush;
        SearchBox.BorderBrush = ThemeService.Instance.Border2Brush;
        SearchBox.Foreground = ThemeService.Instance.Text1Brush;
        SearchBox.PlaceholderForeground = ThemeService.Instance.Text3Brush;
        EmptyText.Foreground = ThemeService.Instance.Text3Brush;

        // 文件夹卡片与结果行是在代码里建的一次性控件，配色变了要重建才能换掉已写入的画刷实例。
        _lpBodyKey = string.Empty;
        RefreshBody();
    }

    private void UpdateChrome()
    {
        if (_lpDetached)
        {
            SubtitleText.Text = "未检测到文件对话框 · 选中后在资源管理器打开";
        }
        else if (_lpDialog.IsFileDialog)
        {
            SubtitleText.Text = $"对话框：{_lpDialog.Kind} · 选中后注入路径";
        }
        else
        {
            SubtitleText.Text = "未检测到文件对话框";
        }

        CountText.Text = _lpSearchMode
            ? (_lpSearching ? "搜索中…" : $"{_lpResults.Count} 个结果")
            : $"{_lpFolders.Count} 个文件夹";
        ModeHintText.Text = _lpKeyboardActive ? "↑↓ · ↵ · Esc" : "Ctrl+G 搜索";
        ApplyTheme();
    }

    /// <summary>把一个文件对话框挂到本栏；重复调用只做重新定位。</summary>
    public void Attach(FileDialogInfo dialog)
    {
        _lpDialog = dialog;
        _lpDetached = false;
        _lpSelected = 0;
        LoadFolders();
        RefreshBody(true);
        UpdateChrome();
        SetOwner(dialog.Handle);
        PositionAttached();

        if (!_lpShown)
        {
            QuickSwitchNative.ShowWindow(_lpHandle, QuickSwitchNative.SwShowNoActivate);
            _lpShown = true;
            // 首次显示后 WinUI 会按内容重排一次，再贴一次保证尺寸/位置稳定。
            PositionAttached();
        }
    }

    /// <summary>没有文件对话框时的预览形态：挂在主窗口下，贴在屏幕底部中间。</summary>
    public void ShowDetached(IntPtr ownerHwnd)
    {
        _lpDialog = FileDialogInfo.None;
        _lpDetached = true;
        _lpSelected = 0;
        LoadFolders();
        RefreshBody(true);
        UpdateChrome();
        SetOwner(ownerHwnd);
        PositionDetached();

        if (!_lpShown)
        {
            QuickSwitchNative.ShowWindow(_lpHandle, QuickSwitchNative.SwShowNoActivate);
            _lpShown = true;
            PositionDetached();
        }

        BringToFront();
    }

    /// <summary>对话框移动/缩放/最小化/还原后同步栏的可见性与位置。</summary>
    public void Reposition()
    {
        if (_lpDetached)
        {
            PositionDetached();
            return;
        }

        if (!_lpDialog.IsFileDialog || !QuickSwitchNative.IsWindow(_lpDialog.Handle))
        {
            return;
        }

        // 对话框最小化时，栏随其一起收起；还原后再贴回底边。
        if (QuickSwitchNative.IsIconic(_lpDialog.Handle))
        {
            if (_lpShown)
            {
                QuickSwitchNative.ShowWindow(_lpHandle, QuickSwitchNative.SwHide);
                _lpShown = false;
            }

            return;
        }

        _lpShown = true;

        // 还原时系统会把 owner 的附属窗口一并自动显示出来，所以这里要按前台状态再套用一次可见性，
        // 而不是只在「从隐藏转为显示」时判断，否则后台对话框被还原（不激活）会把栏也带出来，
        // 绕过「只显示前台那条」的约定。
        if (!_lpForegroundActive)
        {
            QuickSwitchNative.ShowWindow(_lpHandle, QuickSwitchNative.SwHide);
        }

        PositionAttached();
    }

    private void SetOwner(IntPtr owner)
    {
        if (owner == IntPtr.Zero || _lpOwner == owner)
        {
            return;
        }

        // 顶层窗口设置 GWLP_HWNDPARENT 即建立 owner 关系：随 owner 最小化隐藏、随其销毁而销毁，
        // 且始终位于 owner 之上，不会漂到其他窗口前面。
        Marshal.SetLastSystemError(0);
        var lpPrev = QuickSwitchNative.SetWindowLongPtr(_lpHandle, QuickSwitchNative.GwlHwndParent, owner);
        var lpError = Marshal.GetLastWin32Error();
        var lpNow = QuickSwitchNative.GetParent(_lpHandle);
        AppLog.Info($"快速切换栏 owner 绑定: dialog=0x{owner.ToInt64():X}, prev=0x{lpPrev.ToInt64():X}, " +
                    $"getParent=0x{lpNow.ToInt64():X}, error={lpError}");
        _lpOwner = owner;
    }

    /// <summary>
    /// 取栏所在显示器的 DPI 缩放。文件对话框可能是 DPI 不感知的进程（GetDpiForWindow 恒为 96），
    /// 因此优先按显示器 DPI 计算，保证栏内容按自己的渲染比例排满。
    /// </summary>
    private double ResolveScale(IntPtr reference)
    {
        try
        {
            var lpMonitor = QuickSwitchNative.MonitorFromWindow(reference, QuickSwitchNative.MonitorDefaultToNearest);
            if (lpMonitor != IntPtr.Zero &&
                NativeMethods.GetDpiForMonitor(lpMonitor, 0, out var lpDpiX, out _) == 0 &&
                lpDpiX > 0)
            {
                return Math.Max(1.0, lpDpiX / 96.0);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug($"读取显示器 DPI 失败: {ex.Message}");
        }

        var lpWindowDpi = QuickSwitchNative.GetDpiForWindow(_lpHandle);
        return Math.Max(1.0, (lpWindowDpi == 0 ? 96 : lpWindowDpi) / 96.0);
    }

    /// <summary>贴在对话框底边外侧、半宽居中：与对话框咬合，但不侵入客户区、不遮挡任何控件。</summary>
    private void PositionAttached()
    {
        if (!_lpDialog.IsFileDialog || !QuickSwitchNative.IsWindow(_lpDialog.Handle))
        {
            return;
        }

        try
        {
            // 栏自身的窗口按 PerMonitorV2 渲染，尺寸必须按「栏所在显示器」的 DPI 折算；
            // 文件对话框可能是 DPI 不感知的进程（GetDpiForWindow 恒为 96），不能拿它算比例。
            _lpScale = ResolveScale(_lpDialog.Handle);
            var lpFrame = FileDialogInjector.GetFrameBounds(_lpDialog.Handle);

            // 视觉边界（DWM 扩展外框）比窗口矩形小一圈不可见的缩放边框；
            // 贴到「窗口矩形」底边之外，避免压住底边的拖拽缩放热区。
            var lpWindowRect = default(NativeMethods.Rect);
            var lpHasWindowRect = QuickSwitchNative.GetWindowRect(_lpDialog.Handle, out lpWindowRect);

            var lpMonitor = QuickSwitchNative.MonitorFromWindow(_lpDialog.Handle, QuickSwitchNative.MonitorDefaultToNearest);
            var lpMonitorInfo = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            var lpHasWork = lpMonitor != IntPtr.Zero && NativeMethods.GetMonitorInfoW(lpMonitor, ref lpMonitorInfo);

            var lpBar = QuickSwitchLayout.Attach(
                lpFrame,
                lpWindowRect,
                lpHasWindowRect,
                lpMonitorInfo.Work,
                lpHasWork,
                _lpScale,
                _lpSearchMode ? Math.Min(MaxRows, Math.Max(1, _lpResults.Count)) : _lpFolders.Count,
                _lpSearchMode);

            _lpBarWidthPx = lpBar.Width;
            _lpBarHeightPx = lpBar.Height;
            _lpColumns = lpBar.Columns;
            RefreshBody();

            QuickSwitchNative.SetWindowPos(
                _lpHandle,
                _lpDialog.Handle,
                lpBar.X,
                lpBar.Y,
                lpBar.Width,
                lpBar.Height,
                QuickSwitchNative.SwpNoActivate | (_lpForegroundActive ? QuickSwitchNative.SwpShowWindow : 0));

            AppLog.Info($"快速切换栏贴边: dialog=({lpFrame.Left},{lpFrame.Top},{lpFrame.Width}x{lpFrame.Height}) " +
                        $"bar=({lpBar.X},{lpBar.Y},{lpBar.Width}x{lpBar.Height}) scale={_lpScale:0.##} folders={_lpFolders.Count} " +
                        $"columns={lpBar.Columns} " +
                        $"work=({lpMonitorInfo.Work.Left},{lpMonitorInfo.Work.Top},{lpMonitorInfo.Work.Right},{lpMonitorInfo.Work.Bottom})");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"快速切换栏定位失败: {ex.Message}");
        }
    }

    private void PositionDetached()
    {
        try
        {
            _lpScale = DpiUtil.Scale(this);
            var lpWork = DisplayArea.Primary.WorkArea;
            var lpWidth = (int)Math.Round(480 * _lpScale);
            var lpHeight = QuickSwitchLayout.Height(
                lpWidth,
                _lpScale,
                _lpSearchMode ? Math.Min(MaxRows, Math.Max(1, _lpResults.Count)) : _lpFolders.Count,
                _lpSearchMode);
            _lpBarWidthPx = lpWidth;
            _lpBarHeightPx = lpHeight;
            ReflowChips(lpWidth);

            var lpX = lpWork.X + (lpWork.Width - lpWidth) / 2;
            var lpY = lpWork.Y + lpWork.Height - lpHeight - (int)Math.Round(24 * _lpScale);
            AppWindow.MoveAndResize(new RectInt32(lpX, lpY, lpWidth, lpHeight));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"快速切换栏（预览）定位失败: {ex.Message}");
        }
    }

    /// <summary>把栏放到 owner（对话框/主窗）之后，保证只在它上方一层，不盖到别的应用。</summary>
    private void BringToFront()
    {
        if (_lpOwner == IntPtr.Zero)
        {
            return;
        }

        QuickSwitchNative.SetWindowPos(
            _lpHandle,
            _lpOwner,
            0,
            0,
            0,
            0,
            QuickSwitchNative.SwpNoActivate
            | QuickSwitchNative.SwpShowWindow
            | 0x0001 /* SWP_NOSIZE */
            | 0x0002 /* SWP_NOMOVE */);
    }

    public void SetKeyboardActive(bool active)
    {
        if (active)
        {
            if (!_lpKeyboardActive || !_lpInputMode)
            {
                EnterInputMode();
            }

            return;
        }

        if (_lpKeyboardActive || _lpInputMode)
        {
            EndKeyboardMode(false);
        }
    }

    /// <summary>处理来自全局键盘钩子的按键（只在该对话框为前台、栏本身未取得焦点时调用）。</summary>
    public void HandleKey(int virtualKey)
    {
        switch (virtualKey)
        {
            case 0x1B: // Esc：先清空搜索，再退出键盘模式
                HandleEscape();
                return;
            case 0x0D: // Enter
                CommitSelection();
                return;
            case 0x26: // Up
                MoveSelection(-1);
                return;
            case 0x28: // Down
                MoveSelection(1);
                return;
        }

        if (_lpSearchMode)
        {
            return;
        }

        var lpIndex = virtualKey switch
        {
            >= 0x31 and <= 0x36 => virtualKey - 0x31,
            >= 0x61 and <= 0x66 => virtualKey - 0x61,
            _ => -1
        };

        if (lpIndex >= 0 && lpIndex < _lpFolders.Count)
        {
            Pick(lpIndex);
        }
    }

    private void HandleEscape()
    {
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = string.Empty;
            return;
        }

        ExitKeyboardMode();
    }

    private void CommitSelection()
    {
        if (_lpSearchMode)
        {
            if (_lpResults.Count > 0)
            {
                PickResult(Math.Clamp(_lpResultSelected, 0, _lpResults.Count - 1));
            }

            return;
        }

        if (_lpFolders.Count > 0)
        {
            Pick(Math.Clamp(_lpSelected, 0, _lpFolders.Count - 1));
        }
    }

    private void MoveSelection(int delta)
    {
        if (_lpSearchMode)
        {
            if (_lpResults.Count == 0)
            {
                return;
            }

            _lpResultSelected = (_lpResultSelected + delta + _lpResults.Count) % _lpResults.Count;
            RefreshBody(true);
            return;
        }

        if (_lpFolders.Count == 0)
        {
            return;
        }

        _lpSelected = (_lpSelected + delta + _lpFolders.Count) % _lpFolders.Count;
        RefreshBody(true);
    }

    /// <summary>预览/截图模式下按序号选择（不依赖键盘模式）。</summary>
    public void PickByIndex(int index)
    {
        Pick(index);
    }

    /// <summary>截图/自动化用：进入输入态后用系统级按键真实敲入一段文本。</summary>
    public void TypeForTest(string text)
    {
        if (!_lpInputMode)
        {
            EnterInputMode();
        }

        foreach (var lpChar in text)
        {
            var lpScan = VkKeyScanW(lpChar);
            if (lpScan == -1)
            {
                AppLog.Warn($"测试键入跳过无法映射的字符: {lpChar}");
                continue;
            }

            var lpVirtual = (byte)(lpScan & 0xFF);
            var lpShift = (lpScan & 0x0100) != 0;
            if (lpShift)
            {
                keybd_event(0x10, 0, 0, UIntPtr.Zero);
            }

            keybd_event(lpVirtual, 0, 0, UIntPtr.Zero);
            keybd_event(lpVirtual, 0, 2, UIntPtr.Zero);
            if (lpShift)
            {
                keybd_event(0x10, 0, 2, UIntPtr.Zero);
            }

            Thread.Sleep(45);
        }

        AppLog.Info($"快速切换栏测试键入完成: 目标={text} 实际={SearchBox.Text}");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>退出键盘模式，栏继续吸附在对话框上；焦点交还对话框。</summary>
    public void ExitKeyboardMode()
    {
        if (!_lpKeyboardActive && !_lpInputMode)
        {
            return;
        }

        EndKeyboardMode(true);
    }

    /// <summary>隐藏本栏并结束键盘模式；对话框本身不受影响。</summary>
    public void Dismiss()
    {
        _lpShown = false;
        AppWindow.Hide();

        if (_lpKeyboardActive || _lpInputMode)
        {
            EndKeyboardMode(false);
        }
    }

    private void EndKeyboardMode(bool returnFocus)
    {
        _lpKeyboardActive = false;
        ExitInputMode(returnFocus);
        _lpBodyKey = string.Empty;
        UpdateChrome();
        KeyboardEnded?.Invoke();
    }

    private void Pick(int index)
    {
        if (index < 0 || index >= _lpFolders.Count)
        {
            return;
        }

        var lpPath = _lpFolders[index].Path;
        var lpInjected = false;
        if (_lpDialog.IsFileDialog)
        {
            lpInjected = FileDialogInjector.TryInject(_lpDialog.Handle, lpPath);
        }

        if (!lpInjected)
        {
            OpenWithShell(lpPath);
        }

        if (_lpKeyboardActive || _lpInputMode)
        {
            EndKeyboardMode(true);
        }
    }

    private void PickResult(int index)
    {
        if (index < 0 || index >= _lpResults.Count)
        {
            return;
        }

        var lpPath = _lpResults[index].Path;
        var lpInjected = false;
        if (_lpDialog.IsFileDialog)
        {
            lpInjected = FileDialogInjector.TryInject(_lpDialog.Handle, lpPath);
        }

        if (!lpInjected)
        {
            OpenWithShell(lpPath);
        }

        if (_lpKeyboardActive || _lpInputMode)
        {
            EndKeyboardMode(true);
        }
    }

    private static void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开失败 {path}: {ex.Message}");
        }
    }

    /// <summary>进入可输入状态：去掉 WS_EX_NOACTIVATE、把栏切到前台，并把焦点交给搜索框。</summary>
    private void EnterInputMode()
    {
        if (_lpInputMode)
        {
            return;
        }

        _lpInputMode = true;
        _lpKeyboardActive = true;
        ApplyNoActivate(false);
        ActivateForInput();
        _lpBodyKey = string.Empty;
        UpdateChrome();
    }

    private void ExitInputMode(bool returnFocus)
    {
        if (!_lpInputMode)
        {
            return;
        }

        _lpInputMode = false;
        if (returnFocus && QuickSwitchNative.GetForegroundWindow() == _lpHandle)
        {
            var lpTarget = _lpDialog.IsFileDialog ? _lpDialog.Handle : _lpOwner;
            if (lpTarget != IntPtr.Zero && QuickSwitchNative.IsWindow(lpTarget))
            {
                QuickSwitchNative.SetForegroundWindow(lpTarget);
            }
        }

        ApplyNoActivate(true);
    }

    private void ApplyNoActivate(bool enabled)
    {
        if (!QuickSwitchNative.IsWindow(_lpHandle))
        {
            return;
        }

        var lpExStyle = QuickSwitchNative.GetWindowLongPtr(_lpHandle, QuickSwitchNative.GwlExstyle).ToInt64();
        var lpNext = enabled
            ? lpExStyle | QuickSwitchNative.WsExNoActivate
            : lpExStyle & ~QuickSwitchNative.WsExNoActivate;
        if (lpNext != lpExStyle)
        {
            QuickSwitchNative.SetWindowLongPtr(_lpHandle, QuickSwitchNative.GwlExstyle, (IntPtr)lpNext);
        }
    }

    private void ActivateForInput()
    {
        try
        {
            Activate();
        }
        catch (Exception ex)
        {
            AppLog.Debug($"快速切换栏激活失败: {ex.Message}");
        }

        if (QuickSwitchNative.GetForegroundWindow() != _lpHandle)
        {
            var lpCurrent = QuickSwitchNative.GetCurrentThreadId();
            var lpForeground = QuickSwitchNative.GetForegroundWindow();
            var lpForegroundThread = QuickSwitchNative.GetWindowThreadProcessId(lpForeground, IntPtr.Zero);
            var lpAttached = lpCurrent != lpForegroundThread
                             && QuickSwitchNative.AttachThreadInput(lpCurrent, lpForegroundThread, true);
            try
            {
                QuickSwitchNative.SetForegroundWindow(_lpHandle);
            }
            finally
            {
                if (lpAttached)
                {
                    QuickSwitchNative.AttachThreadInput(lpCurrent, lpForegroundThread, false);
                }
            }
        }

        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
        AppLog.Info($"快速切换栏进入输入模式: foreground=0x{QuickSwitchNative.GetForegroundWindow().ToInt64():X}");
    }

    private void OnSearchGotFocus(object sender, RoutedEventArgs e)
    {
        if (!_lpInputMode)
        {
            EnterInputMode();
        }
    }

    private void OnSearchPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                HandleEscape();
                break;
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                CommitSelection();
                break;
            case Windows.System.VirtualKey.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
            case Windows.System.VirtualKey.Down:
                e.Handled = true;
                MoveSelection(1);
                break;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var lpQuery = SearchBox.Text.Trim();
        _lpSearchMode = lpQuery.Length > 0;
        _lpSearchGeneration++;
        _lpResultSelected = -1;

        if (!_lpSearchMode)
        {
            _lpSearchTimer.Stop();
            _lpResults.Clear();
            _lpSearching = false;
            _lpBodyKey = string.Empty;
            UpdateChrome();
            PositionAfterModeChange();
            return;
        }

        _lpResults.Clear();
        _lpSearching = true;
        _lpBodyKey = string.Empty;
        UpdateChrome();
        PositionAfterModeChange();
        _lpSearchTimer.Stop();
        _lpSearchTimer.Start();
    }

    private async Task RunSearchAsync(string rawQuery)
    {
        var lpQuery = rawQuery.Trim();
        if (lpQuery.Length == 0)
        {
            return;
        }

        var lpGeneration = _lpSearchGeneration;
        IpcReply lpReply;
        try
        {
            lpReply = await PipeClient.SendAsync(
                Branding.ServicePipeName,
                new IpcRequest { Command = "SEARCH", Query = lpQuery, Limit = SearchLimit },
                TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            if (lpGeneration != _lpSearchGeneration)
            {
                return;
            }

            AppLog.Warn($"快速切换搜索失败（{lpQuery}）: {ex.Message}");
            _lpSearching = false;
            _lpResults.Clear();
            _lpBodyKey = string.Empty;
            UpdateChrome();
            PositionAfterModeChange();
            return;
        }

        if (lpGeneration != _lpSearchGeneration)
        {
            return;
        }

        _lpSearching = false;
        _lpResults.Clear();
        if (lpReply.Ok && lpReply.Hits is not null)
        {
            foreach (var lpHit in lpReply.Hits)
            {
                if (_lpResults.Count >= MaxRows)
                {
                    break;
                }

                _lpResults.Add(lpHit);
            }
        }

        _lpResultSelected = _lpResults.Count > 0 ? 0 : -1;
        _lpBodyKey = string.Empty;
        UpdateChrome();
        PositionAfterModeChange();
        AppLog.Info($"快速切换搜索结果: q={lpQuery} hits={_lpResults.Count} ok={lpReply.Ok} index={lpReply.Index?.State}");
    }

    private void PositionAfterModeChange()
    {
        if (_lpDetached)
        {
            PositionDetached();
        }
        else
        {
            PositionAttached();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        ThemeService.Instance.Changed -= ApplyTheme;
        RecentStore.Updated -= OnRecentUpdated;
        BarClosed?.Invoke();
    }

    public void CloseSafely()
    {
        try
        {
            ThemeService.Instance.Changed -= ApplyTheme;
            RecentStore.Updated -= OnRecentUpdated;
            if (QuickSwitchNative.IsWindow(_lpHandle))
            {
                Close();
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug($"快速切换栏关闭异常（对话框可能已销毁）: {ex.Message}");
        }
    }

    private void OnRecentUpdated()
    {
        LoadFolders();
        RefreshBody(true);
        UpdateChrome();
    }

    private void LoadFolders()
    {
        _lpFolders.Clear();

        var lpOpen = ReadExplorerWindows();
        if (lpOpen.Count > 0)
        {
            for (var lpIndex = 0; lpIndex < lpOpen.Count && _lpFolders.Count < MaxRows; lpIndex++)
            {
                var lpItem = lpOpen[lpIndex];
                _lpFolders.Add(new QuickFolder(lpItem.Name, lpItem.Path, lpItem.IsCurrent));
            }
        }
        else
        {
            var lpRecent = RecentStore.RecentFolders(MaxRows).ToList();
            foreach (var lpEntry in lpRecent)
            {
                _lpFolders.Add(new QuickFolder(lpEntry.Name, lpEntry.Path, false));
            }
        }

        if (_lpFolders.Count == 0)
        {
            var lpProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var lpCandidate in new[]
                     {
                         Path.Combine(lpProfile, "Desktop"),
                         Path.Combine(lpProfile, "Documents"),
                         Path.Combine(lpProfile, "Downloads"),
                         lpProfile
                     })
            {
                if (!Directory.Exists(lpCandidate) || _lpFolders.Count >= MaxRows)
                {
                    continue;
                }

                var lpLeaf = Path.GetFileName(lpCandidate.TrimEnd('\\'));
                _lpFolders.Add(new QuickFolder(
                    string.IsNullOrEmpty(lpLeaf) ? lpCandidate : lpLeaf,
                    lpCandidate,
                    false));
            }
        }

        _lpSelected = Math.Clamp(_lpSelected, 0, Math.Max(0, _lpFolders.Count - 1));
    }

    private void ReflowChips(int widthPx)
    {
        _lpColumns = _lpSearchMode ? 1 : QuickSwitchLayout.Columns(widthPx, _lpScale);
        RefreshBody();
    }

    /// <summary>
    /// 按当前状态刷新内容区：无输入画最近文件夹网格，有输入画搜索结果列表或空态。
    /// 内容签名没变时直接返回，避免几何事件每帧重建 XAML（拖动时的闪烁源之一）。
    /// </summary>
    private void RefreshBody(bool force = false)
    {
        var lpKey = string.Join(
            '|',
            _lpSearchMode ? "s" : "r",
            _lpColumns,
            _lpFolders.Count,
            _lpResults.Count,
            _lpSelected,
            _lpResultSelected,
            _lpKeyboardActive ? 1 : 0,
            _lpSearching ? 1 : 0);
        if (!force && lpKey == _lpBodyKey)
        {
            return;
        }

        _lpBodyKey = lpKey;

        if (_lpSearchMode)
        {
            ChipsPanel.Visibility = Visibility.Collapsed;
            var lpHasResults = _lpResults.Count > 0;
            ResultsPanel.Visibility = lpHasResults ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Visibility = lpHasResults ? Visibility.Collapsed : Visibility.Visible;
            if (!lpHasResults)
            {
                EmptyText.Text = _lpSearching ? "搜索中…" : "没有匹配的结果 · 试试更短的关键词";
            }

            BuildResults();
            return;
        }

        ResultsPanel.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        ChipsPanel.Visibility = Visibility.Visible;
        BuildChips();
    }

    private void BuildResults()
    {
        ResultsPanel.Children.Clear();
        var lpCount = Math.Min(MaxRows, _lpResults.Count);
        for (var lpIndex = 0; lpIndex < lpCount; lpIndex++)
        {
            ResultsPanel.Children.Add(BuildResultRow(lpIndex, _lpResults[lpIndex]));
        }
    }

    private Border BuildResultRow(int index, SearchHitDto hit)
    {
        var lpSelected = index == _lpResultSelected && _lpKeyboardActive;
        var lpColor = hit.IsDirectory ? FolderColor : FileColor;
        var lpRow = new Border
        {
            Height = 28,
            Padding = new Thickness(7, 0, 7, 0),
            CornerRadius = new CornerRadius(7),
            Background = lpSelected ? ThemeService.Instance.SelectionBrush : ThemeService.Instance.TransparentBrush,
            BorderBrush = lpSelected ? ThemeService.Instance.SelectionBorderBrush : ThemeService.Instance.TransparentBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = hit.Path
        };
        ToolTipService.SetToolTip(lpRow, hit.Path);

        var lpGrid = new Grid { ColumnSpacing = 8 };
        lpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lpBadge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(26, lpColor.R, lpColor.G, lpColor.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(48, lpColor.R, lpColor.G, lpColor.B)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = hit.IsDirectory ? "\uE8B7" : "\uE7C3",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 10,
                Foreground = new SolidColorBrush(lpColor)
            }
        };

        var lpName = new TextBlock
        {
            Text = hit.Name,
            FontSize = 10.5,
            FontWeight = lpSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = ThemeService.Instance.Text1Brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };

        var lpTag = new TextBlock
        {
            Text = hit.IsDirectory ? "文件夹" : "文件",
            FontSize = 9,
            Foreground = ThemeService.Instance.Text3Brush,
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(lpName, 1);
        Grid.SetColumn(lpTag, 2);
        lpGrid.Children.Add(lpBadge);
        lpGrid.Children.Add(lpName);
        lpGrid.Children.Add(lpTag);
        lpRow.Child = lpGrid;

        lpRow.Tapped += (_, args) =>
        {
            args.Handled = true;
            PickResult(index);
        };
        if (!lpSelected)
        {
            lpRow.PointerEntered += (_, _) => lpRow.Background = ThemeService.Instance.RowHoverBrush;
            lpRow.PointerExited += (_, _) =>
            {
                lpRow.Background = _lpKeyboardActive && index == _lpResultSelected
                    ? ThemeService.Instance.SelectionBrush
                    : ThemeService.Instance.TransparentBrush;
            };
        }

        return lpRow;
    }

    private void BuildChips()
    {
        ChipsPanel.Children.Clear();
        ChipsPanel.ColumnDefinitions.Clear();
        ChipsPanel.RowDefinitions.Clear();

        if (_lpColumns < 1)
        {
            _lpColumns = QuickSwitchLayout.Columns(_lpBarWidthPx, _lpScale);
        }
        for (var lpColumn = 0; lpColumn < _lpColumns; lpColumn++)
        {
            ChipsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var lpRows = (int)Math.Ceiling(Math.Min(MaxRows, Math.Max(1, _lpFolders.Count)) / (double)_lpColumns);
        for (var lpRow = 0; lpRow < lpRows; lpRow++)
        {
            ChipsPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (var lpIndex = 0; lpIndex < Math.Min(MaxRows, _lpFolders.Count); lpIndex++)
        {
            var lpChip = BuildChip(lpIndex, _lpFolders[lpIndex]);
            Grid.SetColumn(lpChip, lpIndex % _lpColumns);
            Grid.SetRow(lpChip, lpIndex / _lpColumns);
            ChipsPanel.Children.Add(lpChip);
        }
    }

    private Border BuildChip(int index, QuickFolder folder)
    {
        var lpSelected = index == _lpSelected && _lpKeyboardActive;
        var lpChip = new Border
        {
            Height = 28,
            Padding = new Thickness(7, 0, 7, 0),
            CornerRadius = new CornerRadius(7),
            Background = lpSelected ? ThemeService.Instance.SelectionBrush : ThemeService.Instance.TransparentBrush,
            BorderBrush = lpSelected ? ThemeService.Instance.SelectionBorderBrush : ThemeService.Instance.TransparentBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = folder.Path
        };
        ToolTipService.SetToolTip(lpChip, folder.Path);

        var lpGrid = new Grid { ColumnSpacing = 6 };
        lpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lpBadge = new Border
        {
            Width = 17,
            Height = 17,
            CornerRadius = new CornerRadius(4),
            Background = lpSelected ? ThemeService.Instance.AccentBrush : ThemeService.Instance.TrackOffBrush,
            BorderBrush = lpSelected ? ThemeService.Instance.AccentBrush : ThemeService.Instance.TrackOffBorderBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = (index + 1).ToString(),
                FontSize = 9,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = lpSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = lpSelected ? ThemeService.Instance.AccentInkBrush : ThemeService.Instance.Text3Brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var lpNamePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (folder.IsCurrent)
        {
            lpNamePanel.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = new SolidColorBrush(Color.FromArgb(255, 46, 158, 107)),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        lpNamePanel.Children.Add(new TextBlock
        {
            Text = folder.Name,
            FontSize = 10.5,
            FontWeight = lpSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = ThemeService.Instance.Text1Brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        });

        Grid.SetColumn(lpNamePanel, 1);
        lpGrid.Children.Add(lpBadge);
        lpGrid.Children.Add(lpNamePanel);
        lpChip.Child = lpGrid;

        lpChip.Tapped += (_, args) =>
        {
            args.Handled = true;
            Pick(index);
        };
        if (!lpSelected)
        {
            lpChip.PointerEntered += (_, _) => lpChip.Background = ThemeService.Instance.RowHoverBrush;
            lpChip.PointerExited += (_, _) =>
            {
                lpChip.Background = _lpKeyboardActive && index == _lpSelected
                    ? ThemeService.Instance.SelectionBrush
                    : ThemeService.Instance.TransparentBrush;
            };
        }

        return lpChip;
    }

    /// <summary>
    /// 读取资源管理器窗口列表（Shell.Application COM）。返回的 IsCurrent 只在该窗口
    /// 位于最上层时为 true；拿不到前台窗口时不伪造「当前」。
    /// </summary>
    private List<(string Name, string Path, bool IsCurrent)> ReadExplorerWindows()
    {
        var lpResult = new List<(string, string, bool)>();
        try
        {
            var lpShellType = Type.GetTypeFromProgID("Shell.Application");
            if (lpShellType is null)
            {
                AppLog.Warn("未找到 Shell.Application ProgID，跳过打开窗口检测。");
                return lpResult;
            }

            dynamic lpShell = Activator.CreateInstance(lpShellType)!;
            dynamic lpWindows = lpShell.Windows();
            var lpCount = (int)lpWindows.Count;

            var lpFound = new List<(string Name, string Path, uint Process)>();
            for (var lpIndex = 0; lpIndex < lpCount; lpIndex++)
            {
                try
                {
                    dynamic lpWindow = lpWindows.Item(lpIndex);
                    var lpUrl = (string)lpWindow.LocationURL;
                    var lpHwnd = (int)lpWindow.HWND;

                    if (string.IsNullOrEmpty(lpUrl) || !lpUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var lpPath = new Uri(lpUrl).LocalPath;
                    if (!Directory.Exists(lpPath))
                    {
                        continue;
                    }

                    var lpLeaf = Path.GetFileName(lpPath.TrimEnd('\\'));
                    lpFound.Add((
                        string.IsNullOrEmpty(lpLeaf) ? lpPath : lpLeaf,
                        lpPath,
                        (uint)lpHwnd));
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"读取单个资源管理器窗口失败: {ex.Message}");
                }
            }

            var lpCandidates = new HashSet<IntPtr>(lpFound.Select(lpItem => (IntPtr)(long)lpItem.Process));
            var lpActive = DialogWindowFinder.FindTopExplorerWindow(lpCandidates);

            var lpActiveIndex = lpActive == IntPtr.Zero
                ? -1
                : lpFound.FindIndex(lpItem => (IntPtr)(long)lpItem.Process == lpActive);
            if (lpActiveIndex > 0)
            {
                var lpActiveItem = lpFound[lpActiveIndex];
                lpFound.RemoveAt(lpActiveIndex);
                lpFound.Insert(0, lpActiveItem);
            }

            foreach (var lpItem in lpFound)
            {
                if (lpResult.Count >= MaxRows)
                {
                    break;
                }

                lpResult.Add((
                    lpItem.Name,
                    lpItem.Path,
                    lpActive != IntPtr.Zero && (IntPtr)(long)lpItem.Process == lpActive));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取资源管理器窗口失败: {ex.Message}");
        }

        return lpResult;
    }
}
