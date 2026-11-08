using System.Reflection;
using System.Text.Json;
using Windows.UI;

namespace Pathleap.UI.Theme;

/// <summary>
/// 一套完整的配色方案。明暗两种变体各自定义整套 token：
/// 窗口/表面层级、文字层级、边框、强调色与悬停/按下、状态色、选中与高亮、标题栏渐变。
/// 数据来自 ColorThemes.json（与 tools/Test-LpThemeContrast.ps1 共用同一份，避免两处漂移）。
/// </summary>
public sealed class ColorTheme
{
    private const string ResourceName = "Pathleap.UI.ColorThemes.json";

    private static IReadOnlyList<ColorTheme>? _lpThemes;

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    public Palette Dark { get; set; } = new();

    public Palette Light { get; set; } = new();

    public static IReadOnlyList<ColorTheme> All => _lpThemes ??= LoadThemes();

    public static ColorTheme Find(string? id)
    {
        return All.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.Ordinal)) ?? All[0];
    }

    private static IReadOnlyList<ColorTheme> LoadThemes()
    {
        try
        {
            using var lpStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (lpStream is not null)
            {
                using var lpDocument = JsonDocument.Parse(lpStream);
                var lpThemes = lpDocument.RootElement.GetProperty("themes").Deserialize<List<ColorTheme>>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (lpThemes is { Count: > 0 })
                {
                    return lpThemes;
                }
            }
        }
        catch (Exception ex)
        {
            Pathleap.Shared.Diagnostics.AppLog.Warn($"配色数据读取失败，使用内置靛紫: {ex.Message}");
        }

        return new List<ColorTheme> { FallbackIndigo() };
    }

    /// <summary>资源缺失时的兜底：与 ColorThemes.json 的 indigo 一致，保证界面仍可用。</summary>
    private static ColorTheme FallbackIndigo()
    {
        return new ColorTheme
        {
            Id = "indigo",
            Name = "靛紫（默认）",
            Dark = new Palette
            {
                Window = "#171A21",
                SurfaceRaised = "#1D2129",
                Surface2 = "#1B1F27",
                Surface3 = "#232833",
                Surface4 = "#2B313D",
                Text1 = "#E9EDF5",
                Text2 = "#A3ADBE",
                Text3 = "#8590A3",
                Text4 = "#6B7488",
                Border1 = "#12FFFFFF",
                Border2 = "#24FFFFFF",
                Border3 = "#33FFFFFF",
                Accent = "#3563E6",
                AccentHover = "#3F6BE8",
                AccentPressed = "#294FBD",
                AccentInk = "#FFFFFF",
                Ok = "#3FBF8C",
                Warn = "#E0A14A",
                Danger = "#F0705E",
                RowHover = "#0FFFFFFF",
                Caption1 = "#1E222A",
                Caption2 = "#191D24",
                Scrim = "#66000000"
            },
            Light = new Palette
            {
                Window = "#FFFFFF",
                SurfaceRaised = "#FFFFFF",
                Surface2 = "#F7F8FB",
                Surface3 = "#EFF2F6",
                Surface4 = "#E7EBF1",
                Text1 = "#131720",
                Text2 = "#4C5568",
                Text3 = "#616B7C",
                Text4 = "#7C8697",
                Border1 = "#17101828",
                Border2 = "#24101828",
                Border3 = "#3A101828",
                Accent = "#2F5BD6",
                AccentHover = "#3563EA",
                AccentPressed = "#2446A8",
                AccentInk = "#FFFFFF",
                Ok = "#17724E",
                Warn = "#A4650F",
                Danger = "#C23A2B",
                RowHover = "#0A101828",
                Caption1 = "#FBFCFD",
                Caption2 = "#F2F5F9",
                Scrim = "#66FFFFFF"
            }
        };
    }
}

/// <summary>JSON 里的一套亮或暗配色（字符串形式，便于脚本共用同一份数据）。</summary>
public sealed class Palette
{
    public string Window { get; set; } = "#000000";
    public string SurfaceRaised { get; set; } = "#000000";
    public string Surface2 { get; set; } = "#000000";
    public string Surface3 { get; set; } = "#000000";
    public string Surface4 { get; set; } = "#000000";
    public string Text1 { get; set; } = "#FFFFFF";
    public string Text2 { get; set; } = "#CCCCCC";
    public string Text3 { get; set; } = "#999999";
    public string Text4 { get; set; } = "#777777";
    public string Border1 { get; set; } = "#00000000";
    public string Border2 { get; set; } = "#00000000";
    public string Border3 { get; set; } = "#00000000";
    public string Accent { get; set; } = "#3D6EF7";
    public string AccentHover { get; set; } = "#3D6EF7";
    public string AccentPressed { get; set; } = "#3D6EF7";
    public string AccentInk { get; set; } = "#FFFFFF";
    public string Ok { get; set; } = "#2E9E6B";
    public string Warn { get; set; } = "#C77A18";
    public string Danger { get; set; } = "#D9503F";
    public string RowHover { get; set; } = "#0FFFFFFF";
    public string Caption1 { get; set; } = "#000000";
    public string Caption2 { get; set; } = "#000000";
    public string Scrim { get; set; } = "#66000000";

    public PaletteColors ToColors() => new(this);
}

/// <summary>解析后的颜色集合，供画刷构建直接读取。</summary>
public sealed class PaletteColors
{
    public PaletteColors(Palette palette)
    {
        Window = LpColor.Parse(palette.Window);
        SurfaceRaised = LpColor.Parse(palette.SurfaceRaised);
        Surface2 = LpColor.Parse(palette.Surface2);
        Surface3 = LpColor.Parse(palette.Surface3);
        Surface4 = LpColor.Parse(palette.Surface4);
        Text1 = LpColor.Parse(palette.Text1);
        Text2 = LpColor.Parse(palette.Text2);
        Text3 = LpColor.Parse(palette.Text3);
        Text4 = LpColor.Parse(palette.Text4);
        Border1 = LpColor.Parse(palette.Border1);
        Border2 = LpColor.Parse(palette.Border2);
        Border3 = LpColor.Parse(palette.Border3);
        Accent = LpColor.Parse(palette.Accent);
        AccentHover = LpColor.Parse(palette.AccentHover);
        AccentPressed = LpColor.Parse(palette.AccentPressed);
        AccentInk = LpColor.Parse(palette.AccentInk);
        Ok = LpColor.Parse(palette.Ok);
        Warn = LpColor.Parse(palette.Warn);
        Danger = LpColor.Parse(palette.Danger);
        RowHover = LpColor.Parse(palette.RowHover);
        Caption1 = LpColor.Parse(palette.Caption1);
        Caption2 = LpColor.Parse(palette.Caption2);
        Scrim = LpColor.Parse(palette.Scrim);
    }

    public Color Window { get; }
    public Color SurfaceRaised { get; }
    public Color Surface2 { get; }
    public Color Surface3 { get; }
    public Color Surface4 { get; }
    public Color Text1 { get; }
    public Color Text2 { get; }
    public Color Text3 { get; }
    public Color Text4 { get; }
    public Color Border1 { get; }
    public Color Border2 { get; }
    public Color Border3 { get; }
    public Color Accent { get; }
    public Color AccentHover { get; }
    public Color AccentPressed { get; }
    public Color AccentInk { get; }
    public Color Ok { get; }
    public Color Warn { get; }
    public Color Danger { get; }
    public Color RowHover { get; }
    public Color Caption1 { get; }
    public Color Caption2 { get; }
    public Color Scrim { get; }
}

public static class LpColor
{
    /// <summary>支持 #RRGGBB 与 #AARRGGBB；非法值回退为不透明黑。</summary>
    public static Color Parse(string? value)
    {
        var lpText = (value ?? string.Empty).Trim();
        if (lpText.StartsWith('#'))
        {
            lpText = lpText[1..];
        }

        try
        {
            return lpText.Length switch
            {
                6 => Color.FromArgb(
                    255,
                    Convert.ToByte(lpText[..2], 16),
                    Convert.ToByte(lpText.Substring(2, 2), 16),
                    Convert.ToByte(lpText.Substring(4, 2), 16)),
                8 => Color.FromArgb(
                    Convert.ToByte(lpText[..2], 16),
                    Convert.ToByte(lpText.Substring(2, 2), 16),
                    Convert.ToByte(lpText.Substring(4, 2), 16),
                    Convert.ToByte(lpText.Substring(6, 2), 16)),
                _ => Color.FromArgb(255, 0, 0, 0)
            };
        }
        catch
        {
            return Color.FromArgb(255, 0, 0, 0);
        }
    }

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>把带 alpha 的前景色合成到底色上，得到不透明结果。</summary>
    public static Color Over(Color top, Color bottom)
    {
        var lpAlpha = top.A / 255.0;
        return Color.FromArgb(
            255,
            (byte)Math.Round(top.R * lpAlpha + bottom.R * (1 - lpAlpha)),
            (byte)Math.Round(top.G * lpAlpha + bottom.G * (1 - lpAlpha)),
            (byte)Math.Round(top.B * lpAlpha + bottom.B * (1 - lpAlpha)));
    }

    public static Color Blend(Color top, Color bottom, double bottomWeight)
    {
        var lpTopWeight = 1 - bottomWeight;
        return Color.FromArgb(
            255,
            (byte)Math.Round(top.R * lpTopWeight + bottom.R * bottomWeight),
            (byte)Math.Round(top.G * lpTopWeight + bottom.G * bottomWeight),
            (byte)Math.Round(top.B * lpTopWeight + bottom.B * bottomWeight));
    }
}
