using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;
using Pathleap.UI.Platform;
using Pathleap.UI.Update;
using Windows.Graphics;
using Windows.UI;

namespace Pathleap.UI;

/// <summary>「关于」：产品名、版本、运行时与索引来源，均为真实值。</summary>
public sealed partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // 打开时不要在可见按钮上留下键盘焦点框：把焦点放到 1x1 的隐藏接管点。
        var lpKeySink = new Button { Width = 1, Height = 1, Opacity = 0 };
        RootGrid.Children.Add(lpKeySink);
        RootGrid.Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => lpKeySink.Focus(FocusState.Programmatic));

        Title = $"关于 {Branding.DisplayName}";
        BrandMarkText.Text = Branding.Mark;
        ProductNameText.Text = Branding.DisplayName;
        ProductSubText.Text = Branding.ChineseName;
        VersionText.Text = Branding.Version;
        IndexStateText.Text = "由后台服务提供";

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
                AppLog.Warn($"关于窗无边框设置失败: {ex.Message}");
            }
        }

        AppWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = true;
        NcFrame.Apply(this);

        RootGrid.RequestedTheme = Theme.ThemeService.Instance.ElementTheme;
        RootGrid.Background = Theme.ThemeService.Instance.SurfaceBrush;
        WindowChrome.Apply(this, Theme.ThemeService.Instance.WindowBorderColor, Theme.ThemeService.Instance.IsDark);
        var lpGradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1)
        };
        lpGradient.GradientStops.Add(new GradientStop { Color = Theme.ThemeService.Instance.Accent, Offset = 0 });
        lpGradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 122, 92, 255), Offset = 1 });
        BrandDot.Background = lpGradient;

        ResizeAndCenter();
    }

    private void ResizeAndCenter()
    {
        try
        {
            var lpScale = DpiUtil.Scale(this);
            var lpWidth = (int)Math.Round(380 * lpScale);
            var lpHeight = (int)Math.Round(236 * lpScale);
            var lpWork = DisplayArea.Primary.WorkArea;
            var lpX = lpWork.X + (lpWork.Width - lpWidth) / 2;
            var lpY = lpWork.Y + (lpWork.Height - lpHeight) / 2;
            AppWindow.MoveAndResize(new RectInt32(lpX, lpY, lpWidth, lpHeight));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"关于窗定位失败: {ex.Message}");
        }
    }

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

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
