using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pathleap.Shared.Config;
using Windows.UI;

namespace Pathleap.UI.Theme;

/// <summary>
/// 运行期可变的外观状态：明暗模式、配色方案、不透明度、磨砂与结果密度。
/// 明暗只管 light/dark/system；配色方案（ColorTheme）提供整套颜色 token，两者互不覆盖。
/// 配色同时写入 Themes.xaml 的主题字典（替换画刷颜色），因此已打开的窗口、快速切换栏、
/// 设置窗与更新窗都会即时跟随，不需要重建索引或重启。
/// </summary>
public sealed class ThemeService
{
    public static ThemeService Instance { get; } = new();

    private AppConfig _lpConfig = new();
    private string _lpConfigPath = string.Empty;
    private DispatcherQueueTimer? _lpSystemWatch;
    private ColorTheme _lpColorTheme = ColorTheme.All[0];
    private PaletteColors _lpPalette = ColorTheme.All[0].Dark.ToColors();

    public event Action? Changed;

    public LpThemeMode Mode { get; private set; } = LpThemeMode.Dark;

    public bool IsDark { get; private set; } = true;

    /// <summary>总是给出明确 Light/Dark：跟随系统时由监听器在系统切换后重新求值并触发刷新。</summary>
    public ElementTheme ElementTheme => IsDark ? ElementTheme.Dark : ElementTheme.Light;

    public double WindowOpacity { get; private set; } = 0.92;

    public bool AcrylicEnabled { get; private set; } = true;

    public LpDensity Density { get; private set; } = LpDensity.Standard;

    public IReadOnlyList<ColorTheme> ColorThemes => ColorTheme.All;

    public ColorTheme CurrentColorTheme => _lpColorTheme;

    public string ColorThemeName => _lpColorTheme.Name;

    public PaletteColors Palette => _lpPalette;

    public Color Accent => _lpPalette.Accent;

    public SolidColorBrush AccentBrush { get; private set; } = new(Color.FromArgb(255, 53, 99, 230));

    public SolidColorBrush AccentHoverBrush { get; private set; } = new(Color.FromArgb(255, 63, 107, 232));

    public SolidColorBrush AccentPressedBrush { get; private set; } = new(Color.FromArgb(255, 41, 79, 189));

    public SolidColorBrush AccentInkBrush { get; private set; } = new(Color.FromArgb(255, 255, 255, 255));

    public SolidColorBrush AccentSoftBrush { get; private set; } = new(Color.FromArgb(26, 53, 99, 230));

    public SolidColorBrush AccentSoft2Brush { get; private set; } = new(Color.FromArgb(51, 53, 99, 230));

    public SolidColorBrush MarkBackgroundBrush { get; private set; } = new(Color.FromArgb(51, 53, 99, 230));

    public SolidColorBrush MarkForegroundBrush { get; private set; } = new(Color.FromArgb(255, 169, 195, 255));

    public LinearGradientBrush SelectionBrush { get; private set; } = new();

    public SolidColorBrush SelectionBorderBrush { get; private set; } = new(Color.FromArgb(51, 53, 99, 230));

    public SolidColorBrush WindowBackgroundBrush { get; private set; } = new(Color.FromArgb(235, 23, 26, 33));

    /// <summary>不透明的窗口底色，用于根容器，避免 Transparent 透出系统默认白底。</summary>
    public SolidColorBrush SurfaceBrush { get; private set; } = new(Color.FromArgb(255, 23, 26, 33));

    public SolidColorBrush SurfaceRaisedBrush { get; private set; } = new(Color.FromArgb(255, 29, 33, 41));

    public SolidColorBrush Surface2Brush { get; private set; } = new(Color.FromArgb(255, 27, 31, 39));

    public SolidColorBrush Surface3Brush { get; private set; } = new(Color.FromArgb(255, 35, 40, 51));

    public SolidColorBrush Surface4Brush { get; private set; } = new(Color.FromArgb(255, 43, 49, 61));

    public SolidColorBrush RowHoverBrush { get; private set; } = new(Color.FromArgb(15, 255, 255, 255));

    public SolidColorBrush TrackOffBrush { get; private set; } = new(Color.FromArgb(255, 43, 49, 61));

    public SolidColorBrush TrackOffBorderBrush { get; private set; } = new(Color.FromArgb(31, 255, 255, 255));

    public SolidColorBrush NavSelectedBrush { get; private set; } = new(Color.FromArgb(255, 35, 40, 51));

    public SolidColorBrush Text1Brush { get; private set; } = new(Color.FromArgb(255, 234, 237, 244));

    public SolidColorBrush Text2Brush { get; private set; } = new(Color.FromArgb(255, 160, 168, 184));

    public SolidColorBrush Text3Brush { get; private set; } = new(Color.FromArgb(255, 124, 134, 152));

    public SolidColorBrush Text4Brush { get; private set; } = new(Color.FromArgb(255, 89, 97, 115));

    public SolidColorBrush Border1Brush { get; private set; } = new(Color.FromArgb(19, 255, 255, 255));

    public SolidColorBrush Border2Brush { get; private set; } = new(Color.FromArgb(31, 255, 255, 255));

    public SolidColorBrush Border3Brush { get; private set; } = new(Color.FromArgb(48, 255, 255, 255));

    public SolidColorBrush OkBrush { get; private set; } = new(Color.FromArgb(255, 46, 158, 107));

    public SolidColorBrush WarnBrush { get; private set; } = new(Color.FromArgb(255, 199, 122, 24));

    public SolidColorBrush DangerBrush { get; private set; } = new(Color.FromArgb(255, 217, 80, 63));

    public SolidColorBrush ScrimBrush { get; private set; } = new(Color.FromArgb(102, 0, 0, 0));

    public LinearGradientBrush CaptionBrush { get; private set; } = new();

    /// <summary>界面内品牌标记（亮色版 / 暗色版），随实际生效的明暗切换。</summary>
    public ImageBrush BrandMarkBrush { get; private set; } = new();

    /// <summary>
    /// 窗口外描边的实色。DWM 只接受不透明色，所以把 Border2 描边与窗口底色做一次合成。
    /// </summary>
    public Color WindowBorderColor { get; private set; } = Color.FromArgb(255, 52, 54, 60);

    public SolidColorBrush TransparentBrush { get; } = new(Color.FromArgb(0, 0, 0, 0));

    /// <summary>标题栏按钮字形（所有按钮常态同一份 token，保证颜色同源）。</summary>
    public SolidColorBrush CaptionGlyphBrush { get; private set; } = new(Color.FromArgb(255, 160, 168, 184));

    /// <summary>标题栏按钮悬停/按下时的字形，取正文级文字色。</summary>
    public SolidColorBrush CaptionGlyphHoverBrush { get; private set; } = new(Color.FromArgb(255, 234, 237, 244));

    /// <summary>标题栏按钮悬停底色，按当前文字色叠 10% 透明度，深浅主题都能看出层次。</summary>
    public SolidColorBrush CaptionHoverBrush { get; private set; } = new(Color.FromArgb(26, 234, 237, 244));

    public SolidColorBrush CaptionPressBrush { get; private set; } = new(Color.FromArgb(51, 234, 237, 244));

    /// <summary>关闭按钮悬停红：独立定义，只出现在关闭位上，不随配色方案变化。</summary>
    public SolidColorBrush CaptionCloseHoverBrush { get; private set; } = new(Color.FromArgb(255, 196, 43, 28));

    public SolidColorBrush CaptionClosePressBrush { get; private set; } = new(Color.FromArgb(255, 154, 32, 20));

    public SolidColorBrush CaptionCloseInkBrush { get; private set; } = new(Color.FromArgb(255, 255, 255, 255));

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
        _lpColorTheme = ColorTheme.Find(config.ColorTheme);
        IsDark = ResolveIsDark(Mode);
        RebuildBrushes();
        ApplyResources();
        EnsureSystemWatch();
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
        ApplyResources();
        EnsureSystemWatch();
        SaveIfPersist(persist);
        Changed?.Invoke();
    }

    /// <summary>切换整套配色方案；与明暗模式互不影响。</summary>
    public void SetColorTheme(string id, bool persist = true)
    {
        var lpNext = ColorTheme.Find(id);
        if (string.Equals(lpNext.Id, _lpColorTheme.Id, StringComparison.Ordinal))
        {
            return;
        }

        _lpColorTheme = lpNext;
        _lpConfig.ColorTheme = lpNext.Id;
        RebuildBrushes();
        ApplyResources();
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
        _lpPalette = (IsDark ? _lpColorTheme.Dark : _lpColorTheme.Light).ToColors();
        var lp = _lpPalette;
        var lpAlpha = (byte)Math.Round(WindowOpacity * 255);

        AccentBrush = new SolidColorBrush(lp.Accent);
        AccentHoverBrush = new SolidColorBrush(lp.AccentHover);
        AccentPressedBrush = new SolidColorBrush(lp.AccentPressed);
        AccentInkBrush = new SolidColorBrush(lp.AccentInk);
        AccentSoftBrush = new SolidColorBrush(LpColor.WithAlpha(lp.Accent, 26));
        AccentSoft2Brush = new SolidColorBrush(LpColor.WithAlpha(lp.Accent, 51));
        SelectionBorderBrush = new SolidColorBrush(LpColor.WithAlpha(lp.Accent, 51));

        var lpHl1 = IsDark ? LpColor.Blend(lp.Accent, lp.Window, 0.87) : LpColor.Blend(lp.Accent, Microsoft.UI.Colors.White, 0.92);
        var lpHl2 = IsDark ? LpColor.Blend(lp.Accent, lp.Window, 0.80) : LpColor.Blend(lp.Accent, Microsoft.UI.Colors.White, 0.84);
        SelectionBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5)
        };
        SelectionBrush.GradientStops.Add(new GradientStop { Color = lpHl1, Offset = 0 });
        SelectionBrush.GradientStops.Add(new GradientStop { Color = lpHl2, Offset = 1 });

        MarkBackgroundBrush = new SolidColorBrush(LpColor.WithAlpha(lp.Accent, IsDark ? (byte)51 : (byte)41));
        MarkForegroundBrush = new SolidColorBrush(IsDark
            ? LpColor.Blend(lp.Accent, Microsoft.UI.Colors.White, 0.55)
            : LpColor.Blend(lp.Accent, Microsoft.UI.Colors.Black, 0.22));

        WindowBackgroundBrush = new SolidColorBrush(LpColor.WithAlpha(lp.Window, lpAlpha));
        SurfaceBrush = new SolidColorBrush(lp.Window);
        SurfaceRaisedBrush = new SolidColorBrush(lp.SurfaceRaised);
        Surface2Brush = new SolidColorBrush(lp.Surface2);
        Surface3Brush = new SolidColorBrush(lp.Surface3);
        Surface4Brush = new SolidColorBrush(lp.Surface4);
        RowHoverBrush = new SolidColorBrush(lp.RowHover);
        TrackOffBrush = new SolidColorBrush(lp.Surface4);
        TrackOffBorderBrush = new SolidColorBrush(lp.Border2);
        NavSelectedBrush = new SolidColorBrush(lp.Surface3);
        Text1Brush = new SolidColorBrush(lp.Text1);
        Text2Brush = new SolidColorBrush(lp.Text2);
        Text3Brush = new SolidColorBrush(lp.Text3);
        Text4Brush = new SolidColorBrush(lp.Text4);
        Border1Brush = new SolidColorBrush(lp.Border1);
        Border2Brush = new SolidColorBrush(lp.Border2);
        Border3Brush = new SolidColorBrush(lp.Border3);
        OkBrush = new SolidColorBrush(lp.Ok);
        WarnBrush = new SolidColorBrush(lp.Warn);
        DangerBrush = new SolidColorBrush(lp.Danger);
        ScrimBrush = new SolidColorBrush(lp.Scrim);

        CaptionGlyphBrush = new SolidColorBrush(lp.Text2);
        CaptionGlyphHoverBrush = new SolidColorBrush(lp.Text1);
        CaptionHoverBrush = new SolidColorBrush(LpColor.WithAlpha(lp.Text1, 26));
        CaptionPressBrush = new SolidColorBrush(LpColor.WithAlpha(lp.Text1, 51));

        CaptionBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };
        CaptionBrush.GradientStops.Add(new GradientStop { Color = lp.Caption1, Offset = 0 });
        CaptionBrush.GradientStops.Add(new GradientStop { Color = lp.Caption2, Offset = 1 });

        WindowBorderColor = LpColor.Over(lp.Border2, lp.Window);

        BrandMarkBrush = new ImageBrush
        {
            ImageSource = LoadBrandMark(IsDark),
            Stretch = Stretch.UniformToFill
        };
    }

    private static ImageSource? LoadBrandMark(bool dark)
    {
        var lpPath = Path.Combine(AppContext.BaseDirectory, "Assets", dark ? "pathleap-mark-dark.png" : "pathleap-mark-light.png");
        try
        {
            return new BitmapImage(new Uri(lpPath));
        }
        catch (Exception ex)
        {
            Shared.Diagnostics.AppLog.Warn($"界面图标加载失败（{lpPath}）: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把当前配色写进 Themes.xaml 的主题字典。画刷是共享实例，改 Color 会立即反映到
    /// 所有用 {ThemeResource} 引用它的控件；Dark/Light 两侧都写，明暗切换时无需再刷资源。
    /// </summary>
    private void ApplyResources()
    {
        var lpRoot = Application.Current?.Resources;
        if (lpRoot is null)
        {
            return;
        }

        ApplyToDictionary(lpRoot, "Dark", _lpColorTheme.Dark.ToColors());
        ApplyToDictionary(lpRoot, "Light", _lpColorTheme.Light.ToColors());
    }

    private static void ApplyToDictionary(ResourceDictionary root, string themeKey, PaletteColors palette)
    {
        var lpDict = FindThemeDictionary(root, themeKey);
        if (lpDict is null)
        {
            return;
        }

        SetColor(lpDict, "SurfaceBrush", palette.Window);
        SetColor(lpDict, "SurfaceRaisedBrush", palette.SurfaceRaised);
        SetColor(lpDict, "Surface2Brush", palette.Surface2);
        SetColor(lpDict, "Surface3Brush", palette.Surface3);
        SetColor(lpDict, "Surface4Brush", palette.Surface4);
        SetColor(lpDict, "Border1Brush", palette.Border1);
        SetColor(lpDict, "Border2Brush", palette.Border2);
        SetColor(lpDict, "Border3Brush", palette.Border3);
        SetColor(lpDict, "Text1Brush", palette.Text1);
        SetColor(lpDict, "Text2Brush", palette.Text2);
        SetColor(lpDict, "Text3Brush", palette.Text3);
        SetColor(lpDict, "Text4Brush", palette.Text4);
        SetColor(lpDict, "AccentBrush", palette.Accent);
        SetColor(lpDict, "AccentHoverBrush", palette.AccentHover);
        SetColor(lpDict, "AccentPressedBrush", palette.AccentPressed);
        SetColor(lpDict, "AccentInkBrush", palette.AccentInk);
        SetColor(lpDict, "AccentSoftBrush", LpColor.WithAlpha(palette.Accent, 26));
        SetColor(lpDict, "AccentSoft2Brush", LpColor.WithAlpha(palette.Accent, 51));
        SetColor(lpDict, "OkBrush", palette.Ok);
        SetColor(lpDict, "WarnBrush", palette.Warn);
        SetColor(lpDict, "DangerBrush", palette.Danger);
        SetColor(lpDict, "ScrimBrush", palette.Scrim);
        SetColor(lpDict, "RowHoverBrush", palette.RowHover);

        var lpHl1 = themeKey == "Dark"
            ? LpColor.Blend(palette.Accent, palette.Window, 0.87)
            : LpColor.Blend(palette.Accent, Microsoft.UI.Colors.White, 0.92);
        var lpHl2 = themeKey == "Dark"
            ? LpColor.Blend(palette.Accent, palette.Window, 0.80)
            : LpColor.Blend(palette.Accent, Microsoft.UI.Colors.White, 0.84);
        SetColor(lpDict, "HlRowBrush", lpHl1);
        SetColor(lpDict, "HlRow2Brush", lpHl2);
        SetColor(lpDict, "MarkBgBrush", LpColor.WithAlpha(palette.Accent, themeKey == "Dark" ? (byte)51 : (byte)41));
        SetColor(lpDict, "MarkInkBrush", themeKey == "Dark"
            ? LpColor.Blend(palette.Accent, Microsoft.UI.Colors.White, 0.55)
            : LpColor.Blend(palette.Accent, Microsoft.UI.Colors.Black, 0.22));

        if (lpDict.ContainsKey("CaptionBrush") && lpDict["CaptionBrush"] is LinearGradientBrush caption
            && caption.GradientStops.Count >= 2)
        {
            caption.GradientStops[0].Color = palette.Caption1;
            caption.GradientStops[1].Color = palette.Caption2;
        }
    }

    private static ResourceDictionary? FindThemeDictionary(ResourceDictionary root, string themeKey, int depth = 0)
    {
        if (depth > 4)
        {
            return null;
        }

        if (root.ThemeDictionaries.ContainsKey(themeKey)
            && root.ThemeDictionaries[themeKey] is ResourceDictionary lpTheme
            && lpTheme.ContainsKey("SurfaceBrush"))
        {
            return lpTheme;
        }

        foreach (var lpMerged in root.MergedDictionaries)
        {
            var lpFound = FindThemeDictionary(lpMerged, themeKey, depth + 1);
            if (lpFound is not null)
            {
                return lpFound;
            }
        }

        return null;
    }

    private static void SetColor(ResourceDictionary dict, string key, Color color)
    {
        if (dict.ContainsKey(key) && dict[key] is SolidColorBrush brush)
        {
            brush.Color = color;
        }
    }

    /// <summary>跟随系统时轮询实际生效的 AppsUseLightTheme；切换后刷新画刷与所有窗口。</summary>
    private void EnsureSystemWatch()
    {
        if (Mode != LpThemeMode.System)
        {
            _lpSystemWatch?.Stop();
            return;
        }

        if (_lpSystemWatch is not null)
        {
            _lpSystemWatch.Start();
            return;
        }

        var lpQueue = DispatcherQueue.GetForCurrentThread();
        if (lpQueue is null)
        {
            return;
        }

        _lpSystemWatch = lpQueue.CreateTimer();
        _lpSystemWatch.Interval = TimeSpan.FromSeconds(1.5);
        _lpSystemWatch.IsRepeating = true;
        _lpSystemWatch.Tick += (_, _) =>
        {
            if (Mode != LpThemeMode.System)
            {
                _lpSystemWatch?.Stop();
                return;
            }

            var lpDark = !IsSystemLight();
            if (lpDark != IsDark)
            {
                IsDark = lpDark;
                RebuildBrushes();
                ApplyResources();
                Shared.Diagnostics.AppLog.Info($"系统明暗已切换为 {IsDark}，界面已跟随");
                Changed?.Invoke();
            }
        };
        _lpSystemWatch.Start();
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
