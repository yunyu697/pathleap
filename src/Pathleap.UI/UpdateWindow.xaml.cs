using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pathleap.Shared;
using Pathleap.Shared.Config;
using Pathleap.Shared.Diagnostics;
using Pathleap.UI.Platform;
using Pathleap.UI.Update;
using Windows.Graphics;
using Windows.UI;

namespace Pathleap.UI;

/// <summary>
/// 检查更新窗（设计稿屏 4，440 × 300）：常驻「打开主页 / 检查更新」；发现版本时增加
/// 「检查更新 / 仅下载 / 更新」与版本号、更新说明、镜像下拉；下载中只多一条进度；
/// 仅下载完成后换成「打开下载目录 / 安装」。检查与下载都走真实 GitHub Releases；
/// 镜像仅有一个可用源，其余在菜单标注未接入。
/// </summary>
public sealed partial class UpdateWindow : Window
{
    private readonly AppConfig _lpConfig;
    private UpdateCheckResult? _lpInfo;
    private string _lpDownloadedPath = string.Empty;
    private bool _lpBusy;
    private bool _lpPreview;

    public UpdateWindow(AppConfig config)
    {
        _lpConfig = config;
        InitializeComponent();

        // 打开时不要在关闭按钮上留下键盘焦点框：把焦点放到 1x1 的隐藏接管点。
        var lpKeySink = new Button { Width = 1, Height = 1, Opacity = 0 };
        RootGrid.Children.Add(lpKeySink);
        RootGrid.Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => lpKeySink.Focus(FocusState.Programmatic));

        Title = $"检查更新 · {Branding.DisplayName}";
        CaptionMarkText.Text = Branding.Mark;
        IdleMarkText.Text = Branding.Mark;
        FoundMarkText.Text = Branding.Mark;
        IdleProductText.Text = Branding.DisplayName;
        IdleVersionText.Text = $"当前版本 {Branding.Version}";

        ConfigureWindow();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(CaptionBar);

        ApplyTheme();
        Theme.ThemeService.Instance.Changed += ApplyTheme;
        ShowIdle(BuildLastCheckStatus());
    }

    private void ConfigureWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter lpPresenter)
        {
            lpPresenter.IsResizable = false;
            lpPresenter.IsMaximizable = false;
            lpPresenter.IsMinimizable = false;
        }

        AppWindow.IsShownInSwitchers = false;
        ResizeAndCenter();
    }

    private void ResizeAndCenter()
    {
        try
        {
            var lpScale = DpiUtil.Scale(this);
            var lpWidth = (int)Math.Round(440 * lpScale);
            var lpHeight = (int)Math.Round(300 * lpScale);
            var lpWork = DisplayArea.Primary.WorkArea;
            var lpX = lpWork.X + (lpWork.Width - lpWidth) / 2;
            var lpY = lpWork.Y + (lpWork.Height - lpHeight) / 2;
            AppWindow.MoveAndResize(new RectInt32(lpX, lpY, lpWidth, lpHeight));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"更新窗定位失败: {ex.Message}");
        }
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = Theme.ThemeService.Instance.ElementTheme;
        RootGrid.Background = Theme.ThemeService.Instance.SurfaceBrush;
        Platform.WindowChrome.Apply(this, Theme.ThemeService.Instance.WindowBorderColor, Theme.ThemeService.Instance.IsDark);
        RootGrid.Resources["AccentBrush"] = Theme.ThemeService.Instance.AccentBrush;
        RootGrid.Resources["AccentSoftBrush"] = Theme.ThemeService.Instance.AccentSoftBrush;
        RootGrid.Resources["AccentSoft2Brush"] = Theme.ThemeService.Instance.AccentSoft2Brush;
        BuildBrandDots();
    }

    private void BuildBrandDots()
    {
        var lpAccent = Theme.ThemeService.Instance.Accent;
        CaptionDot.Background = MakeGradient(lpAccent, Color.FromArgb(255, 122, 92, 255), 0);
        IdleMark.Background = MakeGradient(lpAccent, Color.FromArgb(255, 122, 92, 255), 8);
        FoundMark.Background = MakeGradient(lpAccent, Color.FromArgb(255, 122, 92, 255), 0);
        DownloadedMark.Background = MakeGradient(
            Color.FromArgb(255, 53, 180, 127),
            Color.FromArgb(255, 30, 138, 98),
            0);
    }

    private static LinearGradientBrush MakeGradient(Color from, Color to, double offsetX)
    {
        var lpGradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(offsetX, 0),
            EndPoint = new Windows.Foundation.Point(1, 1)
        };
        lpGradient.GradientStops.Add(new GradientStop { Color = from, Offset = 0 });
        lpGradient.GradientStops.Add(new GradientStop { Color = to, Offset = 1 });
        return lpGradient;
    }

    private string BuildLastCheckStatus()
    {
        if (string.IsNullOrWhiteSpace(_lpConfig.LastUpdateCheck))
        {
            return "上次检查：尚未检查";
        }

        try
        {
            var lpTime = DateTimeOffset.Parse(_lpConfig.LastUpdateCheck);
            return $"上次检查：今天 {lpTime:HH:mm}";
        }
        catch
        {
            return "上次检查：尚未检查";
        }
    }

    private void ShowPanel(string state)
    {
        IdlePanel.Visibility = state == "idle" ? Visibility.Visible : Visibility.Collapsed;
        FoundPanel.Visibility = state == "found" ? Visibility.Visible : Visibility.Collapsed;
        DownloadPanel.Visibility = state == "downloading" ? Visibility.Visible : Visibility.Collapsed;
        DownloadedPanel.Visibility = state == "downloaded" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowIdle(string status)
    {
        ShowPanel("idle");
        IdleVersionText.Text = $"当前版本 {Branding.Version}";
        IdleLastText.Text = status;
        IdleLastText.Foreground = Theme.ThemeService.Instance.Text4Brush;

        HomeButton.Visibility = Visibility.Visible;
        CheckButton.Visibility = Visibility.Visible;
        CheckButton.IsEnabled = !_lpBusy;
        // 设计稿：空闲态「检查更新」是主按钮；发现新版本后降级为次按钮。
        CheckButton.Style = (Style)RootGrid.Resources["LpPrimaryButtonStyle"];
        DownloadOnlyButton.Visibility = Visibility.Collapsed;
        UpdateButton.Visibility = Visibility.Collapsed;
        OpenFolderButton.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ShowIdle(message);
        IdleLastText.Foreground = new SolidColorBrush(Color.FromArgb(255, 217, 80, 63));
    }

    private void ShowFound(UpdateCheckResult info)
    {
        _lpInfo = info;
        ShowPanel("found");
        FoundVersionText.Text = info.RemoteVersion;
        FoundOldText.Text = Branding.Version;
        FoundChannelText.Text = _lpConfig.UpdateChannel == "stable" ? "稳定通道" : _lpConfig.UpdateChannel;

        var lpAssetLine = string.IsNullOrEmpty(info.AssetName)
            ? "该版本未附带安装包。"
            : $"安装包：{info.AssetName}（{FormatBytes(info.AssetSize)}）";
        AssetInfoText.Text = info.HasUpdate
            ? (string.IsNullOrWhiteSpace(info.Message) ? lpAssetLine : $"{lpAssetLine} · {info.Message}")
            : $"已是最新版本。{lpAssetLine}";
        AssetInfoText.Visibility = Visibility.Visible;

        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes) ? "该版本未提供更新说明。" : info.Notes;
        NotesPanel.Visibility = Visibility.Visible;
        MirrorPanel.Visibility = Visibility.Visible;
        MirrorLabelText.Text = "下载镜像";

        HomeButton.Visibility = Visibility.Visible;
        CheckButton.Visibility = Visibility.Visible;
        CheckButton.IsEnabled = true;
        CheckButton.Style = (Style)RootGrid.Resources["LpGhostButtonStyle"];

        var lpCanDownload = info.HasUpdate && !string.IsNullOrEmpty(info.AssetUrl);
        DownloadOnlyButton.Visibility = info.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Visibility = info.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
        DownloadOnlyButton.IsEnabled = lpCanDownload;
        UpdateButton.IsEnabled = lpCanDownload;
        OpenFolderButton.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Collapsed;
        ResizeAndCenter();
    }

    private void ShowDownloading()
    {
        ShowPanel("downloading");
        DownloadTitleText.Text = _lpInfo is null ? "正在下载" : $"正在下载 {_lpInfo.RemoteVersion}";
        ProgressFill.Width = 0;
        ProgressText.Text = "0%";
        ProgressDetailText.Text = string.Empty;

        HomeButton.Visibility = Visibility.Visible;
        CheckButton.Visibility = Visibility.Collapsed;
        DownloadOnlyButton.Visibility = Visibility.Collapsed;
        UpdateButton.Visibility = Visibility.Visible;
        UpdateButton.IsEnabled = false;
        OpenFolderButton.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Collapsed;
        ResizeAndCenter();
    }

    private void ShowDownloaded(string path)
    {
        _lpDownloadedPath = path;
        ShowPanel("downloaded");
        var lpVersion = _lpInfo?.RemoteVersion ?? Branding.Version;
        DownloadedTitleText.Text = $"{lpVersion} 已下载完成";
        DownloadedFileName.Text = Path.GetFileName(path);
        var lpFolder = Path.GetDirectoryName(path) ?? AppPaths.UpdatesRoot;
        DownloadedFileDetail.Text = $"{lpFolder} · {FormatBytes(SafeLength(path))}";

        HomeButton.Visibility = Visibility.Collapsed;
        CheckButton.Visibility = Visibility.Collapsed;
        DownloadOnlyButton.Visibility = Visibility.Collapsed;
        UpdateButton.Visibility = Visibility.Collapsed;
        OpenFolderButton.Visibility = Visibility.Visible;
        var lpIsSetup = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        InstallButton.Visibility = lpIsSetup ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = lpIsSetup;
        ResizeAndCenter();
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private async Task CheckAsync()
    {
        if (_lpBusy)
        {
            return;
        }

        _lpBusy = true;
        _lpPreview = false;
        PreviewBadge.Visibility = Visibility.Collapsed;
        CheckButton.IsEnabled = false;
        IdleLastText.Text = "正在检查 GitHub 最新版本…";
        IdleLastText.Foreground = Theme.ThemeService.Instance.Text3Brush;

        var lpResult = await UpdateService.CheckAsync();
        _lpBusy = false;
        _lpConfig.LastUpdateCheck = DateTimeOffset.Now.ToString("O");
        Theme.ThemeService.Instance.SaveConfig();

        if (!lpResult.Ok)
        {
            ShowError($"检查失败：{lpResult.Message}");
            return;
        }

        _lpInfo = lpResult;
        if (lpResult.HasUpdate)
        {
            ShowFound(lpResult);
        }
        else
        {
            ShowIdle($"已是最新版本（{Branding.Version}），上次检查 {DateTimeOffset.Now:HH:mm}。");
        }
    }

    private async Task DownloadAsync(bool installAfter)
    {
        if (_lpInfo is null || string.IsNullOrEmpty(_lpInfo.AssetUrl) || _lpBusy)
        {
            return;
        }

        _lpBusy = true;
        ShowDownloading();

        var lpDestination = Path.Combine(AppPaths.UpdatesRoot, _lpInfo.AssetName);
        try
        {
            AppPaths.EnsureDirectory(AppPaths.UpdatesRoot);
            var lpLastUiUpdate = DateTimeOffset.MinValue;
            await UpdateService.DownloadAsync(
                _lpInfo,
                lpDestination,
                (lpReceived, lpTotal) =>
                {
                    var lpNow = DateTimeOffset.Now;
                    if ((lpNow - lpLastUiUpdate).TotalMilliseconds < 120 && lpTotal > 0 && lpReceived < lpTotal)
                    {
                        return;
                    }

                    lpLastUiUpdate = lpNow;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        var lpRatio = lpTotal > 0 ? Math.Clamp((double)lpReceived / lpTotal, 0, 1) : 0;
                        ProgressFill.Width = Math.Max(1, (RootBorder.ActualWidth - 58) * lpRatio);
                        ProgressText.Text = $"{lpRatio * 100:0}%";
                        ProgressDetailText.Text = lpTotal > 0
                            ? $"{FormatBytes(lpReceived)} / {FormatBytes(lpTotal)}"
                            : FormatBytes(lpReceived);
                    });
                });
            _lpBusy = false;
            if (installAfter)
            {
                InstallAsync(lpDestination);
            }
            else
            {
                ShowDownloaded(lpDestination);
            }
        }
        catch (Exception ex)
        {
            _lpBusy = false;
            AppLog.Warn($"更新下载失败: {ex.Message}");
            ShowError($"下载失败：{ex.Message}");
        }
    }

    private void InstallAsync(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            AppLog.Info($"已启动安装程序: {path}");
        }
        catch (Exception ex)
        {
            ShowError($"无法启动安装程序：{ex.Message}");
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        <= 0 => "大小未知",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB"
    };

    /// <summary>供截图与自动化使用：把界面切到某一真实状态，但数据是测试注入并显示「预览状态」徽标。</summary>
    internal void ShowPreview(string state)
    {
        _lpPreview = true;
        _lpBusy = false;
        PreviewBadge.Visibility = Visibility.Visible;

        var lpSample = new UpdateCheckResult
        {
            Ok = true,
            RemoteTag = "v1.0.2",
            RemoteVersion = "1.0.2",
            Notes = "· 搜索结果右键菜单补齐「复制路径」「在资源管理器中显示」\n" +
                    "· 修复高 DPI 下窗口定位偏移\n" +
                    "· 快速切换面板支持最近 6 个文件夹\n" +
                    "· 索引服务增加异常退出自动重启",
            AssetName = "Pathleap-1.0.2-win-x64-setup.exe",
            AssetSize = 36_700_000,
            AssetUrl = "preview",
            HasUpdate = true
        };

        switch (state)
        {
            case "found":
                _lpInfo = lpSample;
                ShowFound(lpSample);
                AssetInfoText.Text = "预览数据：正式检查会读取 GitHub 最新 Release。";
                break;
            case "downloading":
                _lpInfo = lpSample;
                ShowDownloading();
                DownloadTitleText.Text = "正在下载 1.0.2";
                ProgressFill.Width = 148;
                ProgressText.Text = "42%";
                ProgressDetailText.Text = "15.4 MB / 36.7 MB";
                break;
            case "downloaded":
                _lpInfo = lpSample;
                ShowDownloaded(Path.Combine(AppPaths.UpdatesRoot, lpSample.AssetName));
                DownloadedFileDetail.Text = $"{AppPaths.UpdatesRoot} · {FormatBytes(lpSample.AssetSize)}";
                break;
            default:
                ShowIdle("预览状态：检查逻辑会真实请求 GitHub API。");
                break;
        }
    }

    public void PrepareShutdown()
    {
        Theme.ThemeService.Instance.Changed -= ApplyTheme;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnHomeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateService.HomepageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开主页失败: {ex.Message}");
        }
    }

    private void OnCheckClick(object sender, RoutedEventArgs e)
    {
        if (_lpPreview)
        {
            return;
        }

        _ = CheckAsync();
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_lpPreview)
        {
            AssetInfoText.Text = "预览状态：不会真的下载；正式版本会请求 GitHub Release 资源。";
            return;
        }

        _ = DownloadAsync(installAfter: true);
    }

    private void OnDownloadOnlyClick(object sender, RoutedEventArgs e)
    {
        if (_lpPreview)
        {
            AssetInfoText.Text = "预览状态：不会真的下载；正式版本会请求 GitHub Release 资源。";
            return;
        }

        _ = DownloadAsync(installAfter: false);
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var lpFolder = string.IsNullOrEmpty(_lpDownloadedPath)
                ? AppPaths.UpdatesRoot
                : Path.GetDirectoryName(_lpDownloadedPath) ?? AppPaths.UpdatesRoot;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{lpFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开下载目录失败: {ex.Message}");
        }
    }

    private void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (_lpPreview)
        {
            DownloadedFileDetail.Text = "预览状态：没有真实安装包；正式下载完成后此按钮会启动安装程序。";
            return;
        }

        if (!string.IsNullOrEmpty(_lpDownloadedPath))
        {
            InstallAsync(_lpDownloadedPath);
        }
    }
}
