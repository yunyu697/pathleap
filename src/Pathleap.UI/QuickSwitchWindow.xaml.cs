using System.Diagnostics;
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
/// 说明：真正的打开/保存对话框注入尚未接入，所以表头常驻「预览 · 未接入打开/保存对话框」徽标；
/// 列表内容与跳转都是真实的——最近打开过的文件夹，回车/数字键直接在资源管理器打开。
/// </summary>
public sealed partial class QuickSwitchWindow : Window
{
    private const int MaxRows = 6;
    private const int RowHeight = 44;

    private readonly AppConfig _lpConfig;
    private readonly List<RecentEntry> _lpFolders = new();
    private bool _lpAllowClose;

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
        var lpRecent = RecentStore.RecentFolders(MaxRows).ToList();
        if (lpRecent.Count > 0)
        {
            _lpFolders.AddRange(lpRecent);
            SubtitleText.Text = "最近打开的文件夹";
            EmptyHintText.Visibility = Visibility.Collapsed;
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
                _lpFolders.Add(new RecentEntry
                {
                    Name = Path.GetFileName(lpCandidate.TrimEnd('\\')) is { Length: > 0 } lpLeaf ? lpLeaf : lpCandidate,
                    Path = lpCandidate,
                    IsDirectory = true
                });
            }
        }

        SubtitleText.Text = "常用位置（尚无打开记录）";
        EmptyHintText.Visibility = _lpFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRecentUpdated()
    {
        LoadFolders();
        BuildRows();
    }

    private void BuildRows()
    {
        RowsPanel.Children.Clear();
        CountText.Text = $"{_lpFolders.Count} 个窗口";
        for (var lpIndex = 0; lpIndex < Math.Min(MaxRows, _lpFolders.Count); lpIndex++)
        {
            RowsPanel.Children.Add(BuildRow(lpIndex, _lpFolders[lpIndex]));
        }
    }

    private Border BuildRow(int index, RecentEntry folder)
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

        // 绿点「当前」：只有真实最近记录的第一项才标记，常用位置不会伪造当前状态。
        if (lpSelected && !SubtitleText.Text.StartsWith("常用位置", StringComparison.Ordinal))
        {
            var lpCurrent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center
            };
            lpCurrent.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = new SolidColorBrush(Color.FromArgb(255, 46, 158, 107)),
                VerticalAlignment = VerticalAlignment.Center
            });
            lpCurrent.Children.Add(new TextBlock
            {
                Text = "当前",
                FontSize = 9,
                Foreground = ThemeService.Instance.Text2Brush,
                VerticalAlignment = VerticalAlignment.Center
            });
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

    public void ShowAtCursor()
    {
        PositionAndSize();
        AppWindow.Show();
        Activate();
        KeySink.Focus(FocusState.Programmatic);
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

    private void OpenFolder(int index)
    {
        if (index < 0 || index >= _lpFolders.Count)
        {
            return;
        }

        var lpFolder = _lpFolders[index];
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{lpFolder.Path}\"") { UseShellExecute = true });
            AppLog.Info($"快速切换打开文件夹: {lpFolder.Path}");
            AppWindow.Hide();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"快速切换打开失败: {ex.Message}");
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
