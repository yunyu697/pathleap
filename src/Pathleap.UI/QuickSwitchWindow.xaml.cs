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
using Pathleap.UI.Data;
using Pathleap.UI.Platform;
using Pathleap.UI.Theme;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace Pathleap.UI;

/// <summary>
/// 快速切换浮层（设计稿屏 2）：行首 1–6 直选，绿点标出「当前」文件夹。
/// 打开时若前台是标准文件对话框（#32770 的打开/另存为/选择文件夹），选中文件夹会把路径注入该对话框；
/// 否则退回资源管理器打开。页脚徽标如实显示当前是「注入模式」还是「未检测到对话框」。
/// </summary>
public sealed partial class QuickSwitchWindow : Window
{
    private const int MaxRows = 6;
    private const int RowHeight = 44;

    private readonly AppConfig _lpConfig;
    private readonly List<QuickFolder> _lpFolders = new();
    private FileDialogInfo _lpDialog = FileDialogInfo.None;
    private bool _lpAllowClose;

    private readonly record struct QuickFolder(string Name, string Path, bool IsCurrent);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public QuickSwitchWindow(AppConfig config)
    {
        _lpConfig = config;
        InitializeComponent();

        Title = $"快速切换 · {Branding.DisplayName}";
        ConfigureWindow();
        ApplyTheme();
        LoadFolders();
        BuildRows();
        PositionAndSize();

        KeySink.KeyDown += OnKeyDown;
        Activated += OnActivated;
        AppWindow.Closing += OnClosing;
        RecentStore.Updated += OnRecentUpdated;
        ThemeService.Instance.Changed += ApplyTheme;
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
                AppLog.Warn($"快速切换无边框设置失败: {ex.Message}");
            }
        }

        AppWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = true;
        NcFrame.Apply(this);
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = ThemeService.Instance.ElementTheme;
        RootGrid.Background = ThemeService.Instance.SurfaceBrush;
        WindowChrome.Apply(this, ThemeService.Instance.WindowBorderColor, ThemeService.Instance.IsDark);
        RootGrid.Resources["AccentBrush"] = ThemeService.Instance.AccentBrush;
        BrandDot.Background = ThemeService.Instance.AccentSoftBrush;
    }

    private void LoadFolders()
    {
        _lpFolders.Clear();
        EmptyHintText.Visibility = Visibility.Collapsed;

        // 优先列出真实打开的资源管理器窗口；最上层的那个窗口就是「当前」文件夹。
        var lpOpen = ReadExplorerWindows();
        if (lpOpen.Count > 0)
        {
            foreach (var lpItem in lpOpen)
            {
                _lpFolders.Add(new QuickFolder(lpItem.Name, lpItem.Path, lpItem.IsCurrent));
            }

            // 把「当前」文件夹提到第一行，保证它一定在 6 行可见范围内且默认选中。
            var lpCurrentIndex = _lpFolders.FindIndex(lpFolder => lpFolder.IsCurrent);
            if (lpCurrentIndex > 0)
            {
                var lpCurrent = _lpFolders[lpCurrentIndex];
                _lpFolders.RemoveAt(lpCurrentIndex);
                _lpFolders.Insert(0, lpCurrent);
            }

            SubtitleText.Text = "已打开的文件夹";
            return;
        }

        var lpRecent = RecentStore.RecentFolders(MaxRows).ToList();
        if (lpRecent.Count > 0)
        {
            foreach (var lpEntry in lpRecent)
            {
                _lpFolders.Add(new QuickFolder(lpEntry.Name, lpEntry.Path, false));
            }

            SubtitleText.Text = "最近打开的文件夹";
            return;
        }

        // 没有任何打开记录时，展示系统已知的常用位置，并在副标题里说清楚来源。
        var lpProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var lpCandidate in new[]
                 {
                     Path.Combine(lpProfile, "Desktop"),
                     Path.Combine(lpProfile, "Documents"),
                     Path.Combine(lpProfile, "Downloads"),
                     lpProfile
                 })
        {
            if (Directory.Exists(lpCandidate))
            {
                var lpLeaf = Path.GetFileName(lpCandidate.TrimEnd('\\'));
                _lpFolders.Add(new QuickFolder(
                    string.IsNullOrEmpty(lpLeaf) ? lpCandidate : lpLeaf,
                    lpCandidate,
                    false));
            }
        }

        SubtitleText.Text = "常用位置（尚无打开记录）";
        EmptyHintText.Visibility = _lpFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 读取资源管理器窗口列表（Shell.Application COM）。返回的 IsCurrent 只在
    /// 该窗口就是前台窗口时为 true；拿不到前台窗口时不伪造「当前」。
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

            var lpFound = new List<(string Name, string Path, int Hwnd)>();
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
                        lpHwnd));
                }
                catch (Exception ex)
                {
                    // 单个窗口读取失败不影响其余条目。
                    AppLog.Warn($"读取单个资源管理器窗口失败: {ex.Message}");
                }
            }

            // 只按「Shell 窗口集合内」的 Z 序判定当前，避免把不可见/未列出的窗口当成当前。
            var lpCandidates = new HashSet<IntPtr>(lpFound.Select(lpItem => (IntPtr)lpItem.Hwnd));
            var lpActive = FindTopExplorerWindow(lpCandidates);
            AppLog.Debug($"快速切换：Shell 窗口 {lpCount} 个，可列出 {lpFound.Count} 个，最上层 {lpActive}");

            // 当前文件夹可能排在集合末尾，先提到最前，保证它不会被 6 行上限裁掉。
            var lpActiveIndex = lpActive == IntPtr.Zero
                ? -1
                : lpFound.FindIndex(lpItem => lpItem.Hwnd == (int)lpActive);
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

                lpResult.Add((lpItem.Name, lpItem.Path, lpItem.Hwnd == (int)lpActive && lpActive != IntPtr.Zero));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取资源管理器窗口失败: {ex.Message}");
        }

        return lpResult;
    }

    /// <summary>
    /// EnumWindows 按 Z 序从上到下枚举，第一个可见的资源管理器顶层窗口
    /// （CabinetWClass）就是用户最后一次交互的文件夹窗口。
    /// </summary>
    private static IntPtr FindTopExplorerWindow(HashSet<IntPtr> candidates)
    {
        var lpFound = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || !candidates.Contains(hWnd))
            {
                return true;
            }

            lpFound = hWnd;
            return false;
        }, IntPtr.Zero);
        return lpFound;
    }

    private void OnRecentUpdated()
    {
        LoadFolders();
        BuildRows();
    }

    private void BuildRows()
    {
        RowsPanel.Children.Clear();
        var lpUnit = SubtitleText.Text.StartsWith("常用位置", StringComparison.Ordinal) ? "个常用位置" : "个窗口";
        CountText.Text = $"{_lpFolders.Count} {lpUnit}";
        for (var lpIndex = 0; lpIndex < Math.Min(MaxRows, _lpFolders.Count); lpIndex++)
        {
            RowsPanel.Children.Add(BuildRow(lpIndex, _lpFolders[lpIndex]));
        }
    }

    private Border BuildRow(int index, QuickFolder folder)
    {
        var lpSelected = index == 0;
        var lpRow = new Border
        {
            Height = RowHeight,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(7),
            Background = lpSelected ? ThemeService.Instance.SelectionBrush : ThemeService.Instance.TransparentBrush,
            BorderBrush = lpSelected ? ThemeService.Instance.SelectionBorderBrush : ThemeService.Instance.TransparentBrush,
            BorderThickness = new Thickness(1)
        };

        var lpGrid = new Grid { ColumnSpacing = 10 };
        lpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lpNumber = new Border
        {
            MinWidth = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Background = lpSelected ? ThemeService.Instance.AccentBrush : ThemeService.Instance.TrackOffBrush,
            BorderBrush = lpSelected ? ThemeService.Instance.AccentBrush : ThemeService.Instance.TrackOffBorderBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = (index + 1).ToString(),
                FontSize = 9.5,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = lpSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = lpSelected ? new SolidColorBrush(Microsoft.UI.Colors.White) : ThemeService.Instance.Text3Brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var lpIconBorder = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(26, 215, 160, 58)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = "\uE8B7",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 215, 160, 58)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var lpTexts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        lpTexts.Children.Add(new TextBlock
        {
            Text = folder.Name,
            FontSize = 12,
            FontWeight = lpSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = ThemeService.Instance.Text1Brush,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        lpTexts.Children.Add(new TextBlock
        {
            Text = folder.Path,
            FontSize = 9.5,
            Foreground = ThemeService.Instance.Text3Brush,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(lpTexts, 2);
        lpGrid.Children.Add(lpNumber);
        lpGrid.Children.Add(lpIconBorder);
        Grid.SetColumn(lpIconBorder, 1);
        lpGrid.Children.Add(lpTexts);

        // 绿点「当前」只在真实检测到前台资源管理器窗口时显示，常用位置/最近记录不伪造。
        if (folder.IsCurrent)
        {
            var lpCurrent = new Border
            {
                Padding = new Thickness(6, 2, 6, 2),
                CornerRadius = new CornerRadius(9),
                Background = ThemeService.Instance.TrackOffBrush,
                BorderBrush = ThemeService.Instance.TrackOffBorderBrush,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            var lpCurrentPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4
            };
            lpCurrentPanel.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = new SolidColorBrush(Color.FromArgb(255, 46, 158, 107)),
                VerticalAlignment = VerticalAlignment.Center
            });
            lpCurrentPanel.Children.Add(new TextBlock
            {
                Text = "当前",
                FontSize = 9,
                Foreground = ThemeService.Instance.Text2Brush,
                VerticalAlignment = VerticalAlignment.Center
            });
            lpCurrent.Child = lpCurrentPanel;
            Grid.SetColumn(lpCurrent, 3);
            lpGrid.Children.Add(lpCurrent);
        }

        if (lpSelected)
        {
            var lpEnter = new Border
            {
                Width = 22,
                Height = 20,
                CornerRadius = new CornerRadius(5),
                Background = ThemeService.Instance.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "↵",
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(lpEnter, 4);
            lpGrid.Children.Add(lpEnter);
        }

        lpRow.Child = lpGrid;
        lpRow.Tapped += (_, _) => OpenFolder(index);
        if (!lpSelected)
        {
            lpRow.PointerEntered += (_, _) => lpRow.Background = ThemeService.Instance.RowHoverBrush;
            lpRow.PointerExited += (_, _) => lpRow.Background = ThemeService.Instance.TransparentBrush;
        }

        return lpRow;
    }

    private void PositionAndSize()
    {
        try
        {
            var lpScale = DpiUtil.Scale(this);
            var lpRows = Math.Min(MaxRows, _lpFolders.Count);
            var lpWidth = (int)Math.Round(426 * lpScale);
            var lpHeight = (int)Math.Round((51 + Math.Max(1, lpRows) * 45 + 4 + 34 + 2) * lpScale);
            var lpWork = DisplayArea.Primary.WorkArea;
            var lpX = lpWork.X + (lpWork.Width - lpWidth) / 2;
            var lpY = lpWork.Y + (lpWork.Height - lpHeight) / 2;
            AppWindow.MoveAndResize(new RectInt32(lpX, lpY, lpWidth, lpHeight));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"快速切换定位失败: {ex.Message}");
        }
    }

    public void ShowAtCursor(FileDialogInfo dialog)
    {
        _lpDialog = dialog;
        UpdateDialogBadge();
        PositionAndSize();
        AppWindow.Show();
        Activate();
        Platform.ForegroundWindow.ForceActivate(this);
        // 强制前台之后再走一次 WinUI 激活，让 XAML 岛进入 Active 状态，键盘事件才会路由到 KeySink。
        Activate();
        KeySink.Focus(FocusState.Programmatic);
    }

    private void UpdateDialogBadge()
    {
        if (_lpDialog.IsFileDialog)
        {
            PreviewBadgeText.Text = $"对话框：{_lpDialog.Kind} · 将注入路径";
            PreviewBadgeText.Foreground = ThemeService.Instance.AccentBrush;
        }
        else
        {
            PreviewBadgeText.Text = "未检测到文件对话框";
            PreviewBadgeText.Foreground = ThemeService.Instance.Text4Brush;
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        var lpScreenMode = !string.IsNullOrEmpty(Program.Startup.Screen);
        if (e.WindowActivationState == WindowActivationState.Deactivated && _lpConfig.HideOnDeactivate && !lpScreenMode)
        {
            AppWindow.Hide();
        }
        else if (e.WindowActivationState != WindowActivationState.Deactivated)
        {
            KeySink.Focus(FocusState.Programmatic);
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            AppWindow.Hide();
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OpenFolder(0);
            return;
        }

        var lpNumber = e.Key switch
        {
            VirtualKey.Number1 or VirtualKey.NumberPad1 => 1,
            VirtualKey.Number2 or VirtualKey.NumberPad2 => 2,
            VirtualKey.Number3 or VirtualKey.NumberPad3 => 3,
            VirtualKey.Number4 or VirtualKey.NumberPad4 => 4,
            VirtualKey.Number5 or VirtualKey.NumberPad5 => 5,
            VirtualKey.Number6 or VirtualKey.NumberPad6 => 6,
            _ => 0
        };

        if (lpNumber > 0)
        {
            e.Handled = true;
            OpenFolder(lpNumber - 1);
        }
    }

    /// <summary>截图/自动化用：直接触发第 index 行（0 起），等价于按数字键。</summary>
    internal void PickForTest(int index) => OpenFolder(index);

    private void OpenFolder(int index)
    {
        if (index < 0 || index >= _lpFolders.Count)
        {
            return;
        }

        var lpFolder = _lpFolders[index];
        var lpDialog = _lpDialog;
        AppWindow.Hide();

        if (lpDialog.IsFileDialog)
        {
            // 注入放到后台线程：先等对话框重新拿到前台，再写文件名框并回车。
            var lpPath = lpFolder.Path;
            Task.Run(() =>
            {
                Thread.Sleep(80);
                var lpOk = FileDialogInjector.TryInject(lpDialog.Handle, lpPath);
                if (lpOk)
                {
                    AppLog.Info($"快速切换已注入文件对话框: {lpPath}");
                }
                else
                {
                    AppLog.Warn($"快速切换注入失败，回退为资源管理器打开: {lpPath}");
                    OpenInExplorer(lpPath);
                }
            });
            return;
        }

        OpenInExplorer(lpFolder.Path);
        AppLog.Info($"快速切换打开文件夹: {lpFolder.Path}");
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"资源管理器打开失败: {ex.Message}");
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_lpAllowClose)
        {
            return;
        }

        args.Cancel = true;
        AppWindow.Hide();
    }

    public void PrepareShutdown()
    {
        _lpAllowClose = true;
        RecentStore.Updated -= OnRecentUpdated;
        ThemeService.Instance.Changed -= ApplyTheme;
    }
}
