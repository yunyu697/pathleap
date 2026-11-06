namespace Pathleap.UI.Platform;

/// <summary>通过 SendInput 注入热键 / 探测键（基准测试与真实键盘走同一条系统输入路径）。</summary>
internal static class InputInjector
{
    public static bool SendHotkey(HotkeySpec spec)
    {
        var lpModifiers = spec.GetModifierVirtualKeys();
        var lpInputs = new List<NativeMethods.Input>(lpModifiers.Count * 2 + 2);
        foreach (var lpModifier in lpModifiers)
        {
            lpInputs.Add(CreateKey(lpModifier, keyUp: false));
        }

        lpInputs.Add(CreateKey((ushort)spec.VirtualKey, keyUp: false));
        lpInputs.Add(CreateKey((ushort)spec.VirtualKey, keyUp: true));
        for (var lpIndex = lpModifiers.Count - 1; lpIndex >= 0; lpIndex--)
        {
            lpInputs.Add(CreateKey(lpModifiers[lpIndex], keyUp: true));
        }

        return Send(lpInputs);
    }

    public static bool SendProbeKey(char character)
    {
        var lpVirtualKey = (ushort)char.ToUpperInvariant(character);
        var lpInputs = new[]
        {
            CreateKey(lpVirtualKey, keyUp: false),
            CreateKey(lpVirtualKey, keyUp: true)
        };
        return Send(lpInputs);
    }

    private static bool Send(IReadOnlyList<NativeMethods.Input> inputs)
    {
        var lpArray = inputs as NativeMethods.Input[] ?? inputs.ToArray();
        var lpSent = NativeMethods.SendInput(
            (uint)lpArray.Length,
            lpArray,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>());
        if (lpSent != lpArray.Length)
        {
            Shared.Diagnostics.AppLog.Warn($"SendInput 发送 {lpArray.Length} 个事件，实际返回 {lpSent}");
            return false;
        }

        return true;
    }

    private static NativeMethods.Input CreateKey(ushort virtualKey, bool keyUp) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Data = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? NativeMethods.KeyeventfKeyUp : 0
            }
        }
    };
}
