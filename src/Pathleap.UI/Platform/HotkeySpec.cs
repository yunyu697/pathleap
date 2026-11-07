using System.Globalization;

namespace Pathleap.UI.Platform;

/// <summary>热键的实现方式。</summary>
internal enum HotkeyKind
{
    /// <summary>修饰键 + 单个主键，由 RegisterHotKey 注册。</summary>
    Chord,

    /// <summary>连续按两次同一个修饰键，由低层键盘钩子判定（不叠加其它键）。</summary>
    DoubleTap
}

/// <summary>
/// 一个可解析的唤醒方式。两种实现：
///  - <see cref="HotkeyKind.Chord"/>：修饰键 + 单主键，交给 RegisterHotKey，系统级生效；
///  - <see cref="HotkeyKind.DoubleTap"/>：双击 Ctrl / Alt / Shift，由低层键盘钩子判定，
///    判定窗口内两次「按下并松开」才算触发，期间出现其它按键立即作废，避免吞掉 Ctrl+C 这类组合键。
/// </summary>
internal sealed class HotkeySpec
{
    private HotkeySpec(HotkeyKind kind, uint modifiers, uint virtualKey, string display, string text)
    {
        Kind = kind;
        Modifiers = modifiers;
        VirtualKey = virtualKey;
        Display = display;
        Text = text;
    }

    public HotkeyKind Kind { get; }

    /// <summary>Chord 的修饰键掩码；DoubleTap 为 0。</summary>
    public uint Modifiers { get; }

    /// <summary>Chord 的主键虚拟键；DoubleTap 为 0。</summary>
    public uint VirtualKey { get; }

    /// <summary>DoubleTap 被双击的修饰键虚拟键（Ctrl=0x11 / Alt=0x12 / Shift=0x10）；Chord 为 0。</summary>
    public uint TapVirtualKey { get; private init; }

    /// <summary>界面展示形式，例如「双击 Ctrl」「Ctrl + Alt + Space」。</summary>
    public string Display { get; }

    /// <summary>写入 config.json 的规范文本，例如 <c>Ctrl+Ctrl</c>、<c>Ctrl+Alt+Space</c>。</summary>
    public string Text { get; }

    public bool IsDoubleTap => Kind == HotkeyKind.DoubleTap;

    /// <summary>默认唤醒方式：双击 Ctrl。</summary>
    public static HotkeySpec Default { get; } = DoubleTap("Ctrl+Ctrl", "双击 Ctrl", 0x11);

    /// <summary>快速切换浮层默认热键 Ctrl+G。</summary>
    public static HotkeySpec DefaultQuick { get; } = new(
        HotkeyKind.Chord,
        NativeMethods.ModControl,
        0x47,
        "Ctrl+G",
        "Ctrl+G");

    /// <summary>
    /// 设置页预置的唤醒方式。只列真实可用的实现；鼠标中键/侧键需要额外的鼠标钩子，本版不提供，
    /// 在说明文案里如实标注，不做假开关。
    /// </summary>
    public static IReadOnlyList<HotkeyPreset> Presets { get; } = new[]
    {
        new HotkeyPreset(
            Default,
            "双击 Ctrl",
            "默认。低层键盘钩子判定，两次间隔 ≤380 ms；长按不触发，期间按其它键（如 Ctrl+C）立即作废。",
            "默认"),
        new HotkeyPreset(
            DoubleTap("Alt+Alt", "双击 Alt", 0x12),
            "双击 Alt",
            "同样走钩子，单指操作顺手；部分应用里单按 Alt 会激活菜单栏，需要习惯后使用。",
            "低风险"),
        new HotkeyPreset(
            DoubleTap("Shift+Shift", "双击 Shift", 0x10),
            "双击 Shift",
            "走钩子；中文输入法常用连续 Shift 切换中英文，容易误触，仅建议不用该习惯时选择。",
            "注意误触"),
        new HotkeyPreset(
            new HotkeySpec(HotkeyKind.Chord, NativeMethods.ModControl | NativeMethods.ModAlt, 0x20, "Ctrl + Alt + Space", "Ctrl+Alt+Space"),
            "Ctrl + Alt + Space",
            "RegisterHotKey 注册，最稳定；可能与输入法或其它软件的全局热键冲突。",
            "组合键"),
        new HotkeyPreset(
            new HotkeySpec(HotkeyKind.Chord, NativeMethods.ModControl | NativeMethods.ModShift, 0x20, "Ctrl + Shift + Space", "Ctrl+Shift+Space"),
            "Ctrl + Shift + Space",
            "RegisterHotKey 注册，冲突面比 Ctrl+Alt+Space 小；部分输入法占用该组合。",
            "组合键"),
        new HotkeyPreset(
            new HotkeySpec(HotkeyKind.Chord, NativeMethods.ModControl | NativeMethods.ModAlt, 0x46, "Ctrl + Alt + F", "Ctrl+Alt+F"),
            "Ctrl + Alt + F",
            "RegisterHotKey 注册；适合不习惯空格键的人，注意别与编辑器/游戏快捷键撞车。",
            "组合键")
    };

    private static HotkeySpec DoubleTap(string text, string display, uint tapKey) => new(
        HotkeyKind.DoubleTap,
        0,
        0,
        display,
        text) { TapVirtualKey = tapKey };

    public static bool TryParse(string? text, out HotkeySpec spec, out string error)
    {
        spec = Default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "热键为空";
            return false;
        }

        var lpTrimmed = text.Trim();
        var lpParts = lpTrimmed.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lpParts.Length == 0)
        {
            error = $"热键为空: {text}";
            return false;
        }

        // 双击写法：Ctrl+Ctrl / Alt+Alt / Shift+Shift / DoubleCtrl 等。
        if (TryParseDoubleTap(lpParts, lpTrimmed, out var lpDoubleTap, out error))
        {
            spec = lpDoubleTap;
            return true;
        }

        if (!string.IsNullOrEmpty(error))
        {
            return false;
        }

        var lpModifiers = 0u;
        var lpVirtualKey = 0u;
        var lpKeyName = string.Empty;
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
        spec = new HotkeySpec(HotkeyKind.Chord, lpModifiers, lpVirtualKey, lpDisplay, lpTrimmed);
        return true;
    }

    private static bool TryParseDoubleTap(
        IReadOnlyList<string> parts,
        string raw,
        out HotkeySpec spec,
        out string error)
    {
        spec = Default;
        error = string.Empty;

        // 单 token 写法：DoubleCtrl / DoubleAlt / DoubleShift。
        if (parts.Count == 1)
        {
            switch (parts[0].ToUpperInvariant())
            {
                case "DOUBLECTRL":
                case "CTRLCTRL":
                case "CTRL2":
                    spec = DoubleTap("Ctrl+Ctrl", "双击 Ctrl", 0x11);
                    return true;
                case "DOUBLEALT":
                case "ALTALT":
                case "ALT2":
                    spec = DoubleTap("Alt+Alt", "双击 Alt", 0x12);
                    return true;
                case "DOUBLESHIFT":
                case "SHIFTSHIFT":
                case "SHIFT2":
                    spec = DoubleTap("Shift+Shift", "双击 Shift", 0x10);
                    return true;
                default:
                    return false;
            }
        }

        if (parts.Count != 2)
        {
            return false;
        }

        var lpFirst = parts[0].ToUpperInvariant();
        var lpSecond = parts[1].ToUpperInvariant();
        if (lpFirst != lpSecond)
        {
            return false;
        }

        switch (lpFirst)
        {
            case "CTRL":
            case "CONTROL":
                spec = DoubleTap("Ctrl+Ctrl", "双击 Ctrl", 0x11);
                return true;
            case "ALT":
                spec = DoubleTap("Alt+Alt", "双击 Alt", 0x12);
                return true;
            case "SHIFT":
                spec = DoubleTap("Shift+Shift", "双击 Shift", 0x10);
                return true;
            case "WIN":
            case "WINDOWS":
                // Win 键的单击/长按由系统开始菜单接管，双击无法稳定判定，直接拒绝而不是留下隐患。
                error = "双击 Win 键会被系统开始菜单接管，无法可靠判定；请改用双击 Ctrl/Alt/Shift 或组合键";
                return false;
        }

        return false;
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
        return string.Join(" + ", lpParts);
    }

    /// <summary>两个 Chord 是否完全相同（用于检测与快速切换热键的冲突）。</summary>
    public bool SameAs(HotkeySpec other) => Kind == HotkeyKind.Chord
        && other.Kind == HotkeyKind.Chord
        && Modifiers == other.Modifiers
        && VirtualKey == other.VirtualKey;

    /// <summary>用于 SendInput 的修饰键虚拟键序列。</summary>
    public IReadOnlyList<ushort> GetModifierVirtualKeys()
    {
        if (IsDoubleTap)
        {
            return new[] { (ushort)TapVirtualKey };
        }

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

/// <summary>设置页的一项预置唤醒方式（含适用与冲突说明，不做假开关）。</summary>
internal sealed record HotkeyPreset(HotkeySpec Spec, string Title, string Note, string Tag);
