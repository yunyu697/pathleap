using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Pathleap.Shared.Config;
using Windows.UI;

namespace Pathleap.UI.Theme;

/// <summary>
/// 运行期可变的外观状态：主题模式、强调色、不透明度、磨砂与结果密度。
/// 颜色数值来自 Themes.xaml 与设计稿；强调色相关的画刷在这里派生，改一处即可同步
/// 选中态、高亮片段与主按钮。所有窗口订阅 <see cref="Changed"/> 后刷新自身。
/// </summary>
public sealed class ThemeService
{
    public static ThemeService Instance { get; } = new();

    private AppConfig _lpConfig = new();
    private string _lpConfigPath = string.Empty;

    public event Action? Changed;

    public LpThemeMode Mode { get; private set; } = LpThemeMode.Dark;

    public bool IsDark { get; private set; } = true;

    public ElementTheme ElementTheme => Mode switch
    {
        LpThemeMode.Light => ElementTheme.Light,
        LpThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    public double WindowOpacity { get; private set; } = 0.92;

    public bool AcrylicEnabled { get; private set; } = true;

    public LpDensity Density { get; private set; } = LpDensity.Standard;

    public Color Accent { get; private set; } = Color.FromArgb(255, 61, 110, 247);

    public SolidColorBrush AccentBrush { get; private set; } = new(Color.FromArgb(255, 61, 110, 247));

    public SolidColorBrush AccentSoftBrush { get; private set; } = new(Color.FromArgb(26, 61, 110, 247));

    public SolidColorBrush AccentSoft2Brush { get; private set; } = new(Color.FromArgb(51, 61, 110, 247));

    public SolidColorBrush MarkBackgroundBrush { get; private set; } = new(Color.FromArgb(51, 144, 144, 255));

    public SolidColorBrush MarkForegroundBrush { get; private set; } = new(Color.FromArgb(255, 169, 195, 255));

    public LinearGradientBrush SelectionBrush { get; private set; } = new();

    public SolidColorBrush SelectionBorderBrush { get; private set; } = new(Color.FromArgb(51, 61, 110, 247));

    public SolidColorBrush WindowBackgroundBrush { get; private set; } = new(Color.FromArgb(235, 23, 26, 33));

    /// <summary>不透明的窗口底色，用于根容器，避免 Transparent 透出系统默认白底。</summary>
    public SolidColorBrush SurfaceBrush { get; private set; } = new(Color.FromArgb(255, 23, 26, 33));

    public SolidColorBrush RowHoverBrush { get; private set; } = new(Color.FromArgb(15, 255, 255, 255));

    public SolidColorBrush TrackOffBrush { get; private set; } = new(Color.FromArgb(255, 43, 49, 61));

    public SolidColorBrush TrackOffBorderBrush { get; private set; } = new(Color.FromArgb(31, 255, 255, 255));

    public SolidColorBrush NavSelectedBrush { get; private set; } = new(Color.FromArgb(255, 35, 40, 51));

    public SolidColorBrush Text1Brush { get; private set; } = new(Color.FromArgb(255, 234, 237, 244));

    public SolidColorBrush Text2Brush { get; private set; } = new(Color.FromArgb(255, 160, 168, 184));

    public SolidColorBrush Text3Brush { get; private set; } = new(Color.FromArgb(255, 124, 134, 152));

    public SolidColorBrush Text4Brush { get; private set; } = new(Color.FromArgb(255, 89, 97, 115));

    public SolidColorBrush Border1Brush { get; private set; } = new(Color.FromArgb(19, 255, 255, 255));

    /// <summary>
    /// 窗口外描边的实色。DWM 只接受不透明色，所以把 design token 的 Border2 描边
    /// 与窗口底色做一次合成，得到真正要画在最外一圈的颜色（深浅主题自动切换）。
    /// </summary>
    public Color WindowBorderColor { get; private set; } = Color.FromArgb(255, 52, 54, 60);

    public SolidColorBrush TransparentBrush { get; } = new(Color.FromArgb(0, 0, 0, 0));

    public double RowHeight => Density switch
    {
        LpDensity.Compact => 34,
        LpDensity.Comfortable => 56,
        _ => 46
    };

    public bool ShowRowPath => Density != LpDensity.Compact;

    public double RowNameFontSize => Density switch
    {
        LpDensity.Compact => 12.5,
        LpDensity.Comfortable => 14.5,
        _ => 13.5
    };

    public string DensityLabel => Density switch
    {
        LpDensity.Compact => "紧凑",
        LpDensity.Comfortable => "宽松",
        _ => "标准"
    };

    public string ModeLabel => Mode switch
    {
        LpThemeMode.Light => "浅色",
        LpThemeMode.Dark => "深色",
        _ => "跟随系统"
    };

    public AppConfig Config => _lpConfig;

    public void Load(AppConfig config, string configPath)
    {
        _lpConfig = config;
        _lpConfigPath = configPath;
        Mode = config.ThemeMode switch
        {
            "Light" => LpThemeMode.Light,
            "System" => LpThemeMode.System,
            _ => LpThemeMode.Dark
        };
        WindowOpacity = config.WindowOpacity;
        AcrylicEnabled = config.AcrylicEnabled;
        Density = config.ResultDensity switch
        {
            "Compact" => LpDensity.Compact,
            "Comfortable" => LpDensity.Comfortable,
            _ => LpDensity.Standard
        };
        Accent = ParseColor(config.AccentColor, Color.FromArgb(255, 61, 110, 247));
        IsDark = ResolveIsDark(Mode);
        RebuildBrushes();
        Changed?.Invoke();
    }

    public void SetMode(LpThemeMode mode, bool persist = true)
    {
        Mode = mode;
        _lpConfig.ThemeMode = mode switch
        {
            LpThemeMode.Light => "Light",
            LpThemeMode.System => "System",
            _ => "Dark"
        };
        IsDark = ResolveIsDark(mode);
        RebuildBrushes();
        SaveIfPersist(persist);
        Changed?.Invoke();
    }

    public void SetAccent(Color color, bool persist = true)
    {
        Accent = color;
        _lpConfig.AccentColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        RebuildBrushes();
        SaveIfPersist(persist);
        Changed?.Invoke();
    }

    public void SetOpacity(double opacity, bool persist = true)
    {
        WindowOpacity = Math.Clamp(opacity, 0.6, 1.0);
        _lpConfig.WindowOpacity = WindowOpacity;
        RebuildBrushes();
        SaveIfPersist(persist);
        Changed?.Invoke();
    }

    public void SetAcrylic(bool enabled, bool persist = true)
    {
        AcrylicEnabled = enabled;
        _lpConfig.AcrylicEnabled = enabled;
        SaveIfPersist(persist);
        Changed?.Invoke();
    }

    public void SetDensity(LpDensity density, bool persist = true)
    {
        Density = density;
        _lpConfig.ResultDensity = density switch
        {
            LpDensity.Compact => "Compact",
            LpDensity.Comfortable => "Comfortable",
            _ => "Standard"
        };
        SaveIfPersist(persist);
        Changed?.Invoke();
    }

    public void SaveConfig()
    {
        if (string.IsNullOrEmpty(_lpConfigPath))
        {
            return;
        }

        try
        {
            ConfigStore.Save(_lpConfigPath, _lpConfig);
        }
        catch (Exception ex)
        {
            Shared.Diagnostics.AppLog.Warn($"配置写入失败: {ex.Message}");
        }
    }

    private void SaveIfPersist(bool persist)
    {
        if (persist)
        {
            SaveConfig();
        }
    }

    private void RebuildBrushes()
    {
        var lpSurface = IsDark ? Color.FromArgb(255, 23, 26, 33) : Color.FromArgb(255, 255, 255, 255);
        var lpAlpha = (byte)Math.Round(WindowOpacity * 255);

        AccentBrush = new SolidColorBrush(Accent);
        AccentSoftBrush = new SolidColorBrush(WithAlpha(Accent, 26));
        AccentSoft2Brush = new SolidColorBrush(WithAlpha(Accent, 51));
        SelectionBorderBrush = new SolidColorBrush(WithAlpha(Accent, 51));

        var lpHl1 = IsDark ? Blend(Accent, lpSurface, 0.87) : Blend(Accent, Microsoft.UI.Colors.White, 0.92);
        var lpHl2 = IsDark ? Blend(Accent, lpSurface, 0.80) : Blend(Accent, Microsoft.UI.Colors.White, 0.84);
        SelectionBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5)
        };
        SelectionBrush.GradientStops.Add(new GradientStop { Color = lpHl1, Offset = 0 });
        SelectionBrush.GradientStops.Add(new GradientStop { Color = lpHl2, Offset = 1 });

        MarkBackgroundBrush = new SolidColorBrush(WithAlpha(Accent, IsDark ? (byte)51 : (byte)41));
        MarkForegroundBrush = new SolidColorBrush(IsDark ? Blend(Accent, Microsoft.UI.Colors.White, 0.55) : Blend(Accent, Microsoft.UI.Colors.Black, 0.22));

        WindowBackgroundBrush = new SolidColorBrush(WithAlpha(lpSurface, lpAlpha));
        SurfaceBrush = new SolidColorBrush(lpSurface);
        RowHoverBrush = new SolidColorBrush(IsDark ? Color.FromArgb(15, 255, 255, 255) : Color.FromArgb(10, 16, 24, 40));
        TrackOffBrush = new SolidColorBrush(IsDark ? Color.FromArgb(255, 43, 49, 61) : Color.FromArgb(255, 231, 235, 241));
        TrackOffBorderBrush = new SolidColorBrush(IsDark ? Color.FromArgb(31, 255, 255, 255) : Color.FromArgb(36, 16, 24, 40));
        NavSelectedBrush = new SolidColorBrush(IsDark ? Color.FromArgb(255, 35, 40, 51) : Color.FromArgb(255, 239, 242, 246));
        Text1Brush = new SolidColorBrush(IsDark ? Color.FromArgb(255, 234, 237, 244) : Color.FromArgb(255, 20, 24, 33));
        Text2Brush = new SolidColorBrush(IsDark ? Color.FromArgb(255, 160, 168, 184) : Color.FromArgb(255, 92, 101, 119));
        Text3Brush = new SolidColorBrush(IsDark ? Color.FromArgb(255, 124, 134, 152) : Color.FromArgb(255, 122, 131, 149));
        Text4Brush = new SolidColorBrush(IsDark ? Color.FromArgb(255, 89, 97, 115) : Color.FromArgb(255, 155, 163, 178));
        Border1Brush = new SolidColorBrush(IsDark ? Color.FromArgb(19, 255, 255, 255) : Color.FromArgb(23, 16, 24, 40));

        var lpBorder2 = IsDark ? Color.FromArgb(31, 255, 255, 255) : Color.FromArgb(36, 16, 24, 40);
        WindowBorderColor = Over(lpBorder2, lpSurface);
    }

    private static bool ResolveIsDark(LpThemeMode mode)
    {
        return mode switch
        {
            LpThemeMode.Light => false,
            LpThemeMode.Dark => true,
            _ => !IsSystemLight()
        };
    }

    /// <summary>读取系统浅色/深色偏好（跟随系统时使用）。</summary>
    private static bool IsSystemLight()
    {
        try
        {
            var lpValue = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0);
            return lpValue is int lpInt && lpInt > 0;
        }
        catch
        {
            return false;
        }
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color ParseColor(string value, Color fallback)
    {
        try
        {
            if (value.Length == 7 && value[0] == '#')
            {
                return Color.FromArgb(
                    255,
                    Convert.ToByte(value.Substring(1, 2), 16),
                    Convert.ToByte(value.Substring(3, 2), 16),
                    Convert.ToByte(value.Substring(5, 2), 16));
            }
        }
        catch
        {
            // 无效颜色回退到默认强调色。
        }

        return fallback;
    }

    /// <summary>把带 alpha 的描边色合成到底色上，得到不透明结果。</summary>
    private static Color Over(Color top, Color bottom)
    {
        var lpAlpha = top.A / 255.0;
        return Color.FromArgb(
            255,
            (byte)Math.Round(top.R * lpAlpha + bottom.R * (1 - lpAlpha)),
            (byte)Math.Round(top.G * lpAlpha + bottom.G * (1 - lpAlpha)),
            (byte)Math.Round(top.B * lpAlpha + bottom.B * (1 - lpAlpha)));
    }

    private static Color Blend(Color top, Color bottom, double bottomWeight)
    {
        var lpTopWeight = 1 - bottomWeight;
        return Color.FromArgb(
            255,
            (byte)Math.Round(top.R * lpTopWeight + bottom.R * bottomWeight),
            (byte)Math.Round(top.G * lpTopWeight + bottom.G * bottomWeight),
            (byte)Math.Round(top.B * lpTopWeight + bottom.B * bottomWeight));
    }
}

public enum LpThemeMode
{
    System,
    Light,
    Dark
}

public enum LpDensity
{
    Compact,
    Standard,
    Comfortable
}
