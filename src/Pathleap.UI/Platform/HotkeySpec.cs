using System.Globalization;

namespace Pathleap.UI.Platform;

/// <summary>可解析的热键描述（修饰键 + 单个虚拟键）。M1 只需支持 RegisterHotKey 的常见组合。</summary>
internal sealed class HotkeySpec
{
    private HotkeySpec(uint modifiers, uint virtualKey, string display)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
        Display = display;
    }

    public uint Modifiers { get; }

    public uint VirtualKey { get; }

    public string Display { get; }

    public static HotkeySpec Default { get; } = new(
        NativeMethods.ModControl | NativeMethods.ModAlt,
        0x20,
        "Ctrl+Alt+Space");

    public static bool TryParse(string? text, out HotkeySpec spec, out string error)
    {
        spec = Default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "热键为空";
            return false;
        }

        var lpModifiers = 0u;
        var lpVirtualKey = 0u;
        var lpKeyName = string.Empty;
        var lpParts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var lpPart in lpParts)
        {
            switch (lpPart.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    lpModifiers |= NativeMethods.ModControl;
                    break;
                case "ALT":
                    lpModifiers |= NativeMethods.ModAlt;
                    break;
                case "SHIFT":
                    lpModifiers |= NativeMethods.ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    lpModifiers |= NativeMethods.ModWin;
                    break;
                default:
                    if (lpVirtualKey != 0)
                    {
                        error = $"热键包含多个主键: {text}";
                        return false;
                    }

                    lpVirtualKey = ParseKey(lpPart, out lpKeyName);
                    if (lpVirtualKey == 0)
                    {
                        error = $"无法识别的按键: {lpPart}";
                        return false;
                    }

                    break;
            }
        }

        if (lpVirtualKey == 0)
        {
            error = $"热键缺少主键: {text}";
            return false;
        }

        if (lpModifiers == 0)
        {
            error = "全局热键至少需要一个修饰键（Ctrl/Alt/Shift/Win）";
            return false;
        }

        var lpDisplay = Format(lpModifiers, lpKeyName);
        spec = new HotkeySpec(lpModifiers, lpVirtualKey, lpDisplay);
        return true;
    }

    private static uint ParseKey(string token, out string canonical)
    {
        canonical = token;
        if (token.Length == 1)
        {
            var lpChar = char.ToUpperInvariant(token[0]);
            if (lpChar is >= 'A' and <= 'Z')
            {
                canonical = lpChar.ToString();
                return (uint)lpChar;
            }

            if (lpChar is >= '0' and <= '9')
            {
                canonical = lpChar.ToString();
                return 0x30 + (uint)(lpChar - '0');
            }
        }

        switch (token.ToUpperInvariant())
        {
            case "SPACE":
                canonical = "Space";
                return 0x20;
            case "ENTER":
            case "RETURN":
                canonical = "Enter";
                return 0x0D;
            case "TAB":
                canonical = "Tab";
                return 0x09;
            case "ESC":
            case "ESCAPE":
                canonical = "Esc";
                return 0x1B;
            case "BACKSPACE":
                canonical = "Backspace";
                return 0x08;
            case "DELETE":
            case "DEL":
                canonical = "Delete";
                return 0x2E;
            case "UP":
                canonical = "Up";
                return 0x26;
            case "DOWN":
                canonical = "Down";
                return 0x28;
            case "LEFT":
                canonical = "Left";
                return 0x25;
            case "RIGHT":
                canonical = "Right";
                return 0x27;
        }

        if (token.Length is >= 2 and <= 3 && (token[0] is 'F' or 'f'))
        {
            var lpNumber = int.TryParse(token[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lpParsed)
                ? lpParsed
                : 0;
            if (lpNumber is >= 1 and <= 24)
            {
                canonical = "F" + lpNumber;
                return 0x70 + (uint)(lpNumber - 1);
            }
        }

        return 0;
    }

    private static string Format(uint modifiers, string keyName)
    {
        var lpParts = new List<string>(4);
        if ((modifiers & NativeMethods.ModControl) != 0)
        {
            lpParts.Add("Ctrl");
        }

        if ((modifiers & NativeMethods.ModAlt) != 0)
        {
            lpParts.Add("Alt");
        }

        if ((modifiers & NativeMethods.ModShift) != 0)
        {
            lpParts.Add("Shift");
        }

        if ((modifiers & NativeMethods.ModWin) != 0)
        {
            lpParts.Add("Win");
        }

        lpParts.Add(keyName);
        return string.Join('+', lpParts);
    }

    /// <summary>用于 SendInput 的修饰键虚拟键序列。</summary>
    public IReadOnlyList<ushort> GetModifierVirtualKeys()
    {
        var lpKeys = new List<ushort>(4);
        if ((Modifiers & NativeMethods.ModControl) != 0)
        {
            lpKeys.Add(0x11);
        }

        if ((Modifiers & NativeMethods.ModAlt) != 0)
        {
            lpKeys.Add(0x12);
        }

        if ((Modifiers & NativeMethods.ModShift) != 0)
        {
            lpKeys.Add(0x10);
        }

        if ((Modifiers & NativeMethods.ModWin) != 0)
        {
            lpKeys.Add(0x5B);
        }

        return lpKeys;
    }
}
