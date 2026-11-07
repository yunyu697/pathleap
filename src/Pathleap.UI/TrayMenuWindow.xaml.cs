using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pathleap.Shared.Diagnostics;
using Pathleap.UI.Platform;
using Pathleap.UI.Theme;
using Windows.Graphics;
using Windows.System;

namespace Pathleap.UI;

/// <summary>
/// 托盘菜单（设计稿屏 5）：5 项——打开搜索框（右侧热键提示）、设置、检查更新、关于、退出。
/// 用自绘弹层代替原生 Win32 菜单，保证图标、间距、圆角与深浅主题和设计稿一致。
/// </summary>
public sealed partial class TrayMenuWindow : Window
{
    private const int ItemHeight = 31;
    private const int MenuWidth = 226;
    private const int MenuHeight = 5 * ItemHeight + 19;

    private readonly string _lpHotkey;

    public event Action? ShowSearchRequested;

    public event Action? SettingsRequested;

    public event Action? UpdateRequested;

    public event Action? AboutRequested;

    public event Action? ExitRequested;

    public TrayMenuWindow(string hotkeyDisplay)
    {
        _lpHotkey = hotkeyDisplay;
        InitializeComponent();

        Title = "托盘菜单";
        ConfigureWindow();
        ApplyTheme();
        BuildItems();

        KeySink.KeyDown += OnKeyDown;
        Activated += OnActivated;
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
                AppLog.Warn($"托盘菜单无边框设置失败: {ex.Message}");
            }
        }

        AppWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = false;
        NcFrame.Apply(this);
        AppWindow.Resize(new SizeInt32(MenuWidth, MenuHeight));
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = ThemeService.Instance.ElementTheme;
        RootGrid.Background = ThemeService.Instance.SurfaceBrush;
        WindowChrome.Apply(this, ThemeService.Instance.WindowBorderColor, ThemeService.Instance.IsDark);
    }

    private static FontIcon MakeIcon(string glyph)
    {
        return new FontIcon
        {
            Glyph = glyph,
            FontSize = 13.5,
            Foreground = ThemeService.Instance.Text2Brush,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 18
        };
    }

    private void BuildItems()
    {
        ItemsPanel.Children.Clear();
        ItemsPanel.Children.Add(BuildItem("\uE721", "打开搜索框", _lpHotkey, () => ShowSearchRequested?.Invoke()));
        ItemsPanel.Children.Add(BuildItem("\uE713", "设置", string.Empty, () => SettingsRequested?.Invoke()));
        ItemsPanel.Children.Add(BuildItem("\uE895", "检查更新", string.Empty, () => UpdateRequested?.Invoke()));
        ItemsPanel.Children.Add(BuildItem("\uE946", "关于", string.Empty, () => AboutRequested?.Invoke()));

        var lpSeparator = new Border
        {
            Height = 1,
            Background = ThemeService.Instance.Border1Brush,
            Margin = new Thickness(6, 4, 6, 4)
        };
        ItemsPanel.Children.Add(lpSeparator);

        ItemsPanel.Children.Add(BuildItem("\uE7E8", "退出", string.Empty, () => ExitRequested?.Invoke(), danger: true));
    }

    private Border BuildItem(string glyph, string text, string shortcut, Action action, bool danger = false)
    {
        var lpForeground = danger
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 217, 80, 63))
            : ThemeService.Instance.Text1Brush;
        var lpPanel = new Grid { ColumnSpacing = 8 };
        lpPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lpPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lpPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        lpPanel.Children.Add(MakeIcon(glyph));

        var lpText = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            Foreground = lpForeground,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lpText, 1);
        lpPanel.Children.Add(lpText);

        if (!string.IsNullOrEmpty(shortcut))
        {
            var lpShortcut = new TextBlock
            {
                Text = shortcut,
                FontSize = 9.5,
                Foreground = ThemeService.Instance.Text2Brush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lpShortcut, 2);
            lpPanel.Children.Add(lpShortcut);
        }

        var lpItem = new Border
        {
            Height = ItemHeight,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(5),
            Background = ThemeService.Instance.TransparentBrush,
            Child = lpPanel
        };

        lpItem.PointerEntered += (_, _) => lpItem.Background = ThemeService.Instance.RowHoverBrush;
        lpItem.PointerExited += (_, _) => lpItem.Background = ThemeService.Instance.TransparentBrush;
        lpItem.Tapped += (_, _) =>
        {
            AppWindow.Hide();
            action();
        };
        return lpItem;
    }

    public void ShowAt(int screenX, int screenY)
    {
        try
        {
            var lpScale = DpiUtil.Scale(this);
            var lpWidth = (int)Math.Round(MenuWidth * lpScale);
            var lpHeight = (int)Math.Round(MenuHeight * lpScale);
            var lpWork = DisplayArea.Primary.WorkArea;
            var lpX = Math.Clamp(screenX - lpWidth + 12, lpWork.X, lpWork.X + lpWork.Width - lpWidth);
            var lpY = Math.Clamp(screenY - lpHeight, lpWork.Y, lpWork.Y + lpWork.Height - lpHeight);
            AppWindow.MoveAndResize(new RectInt32(lpX, lpY, lpWidth, lpHeight));
            AppWindow.Show();
            Activate();
            KeySink.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"托盘菜单显示失败: {ex.Message}");
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated
            && string.IsNullOrEmpty(Program.Startup.Screen))
        {
            AppWindow.Hide();
        }
        else
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
        }
    }

    public void PrepareShutdown()
    {
        ThemeService.Instance.Changed -= ApplyTheme;
    }
}
