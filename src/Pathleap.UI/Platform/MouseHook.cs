using System.Runtime.InteropServices;

namespace Pathleap.UI.Platform;

/// <summary>
/// 低层鼠标钩子（WH_MOUSE_LL）。只关心中键与 XBUTTON1/2 的按下，回调必须极短。
/// 与键盘钩子一样，系统在回调超时后会静默摘钩，由消息线程定期 Rearm 重装。
/// </summary>
internal sealed class MouseHook
{
    private const int WhMouseLl = 14;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;
    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProcedure(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int hookId, HookProcedure callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    private readonly HookProcedure _lpProcedure;
    private readonly Action<int> _lpButtonPressed;
    private IntPtr _lpHandle;

    public MouseHook(Action<int> buttonPressed)
    {
        _lpButtonPressed = buttonPressed;
        _lpProcedure = Callback;
    }

    public bool Installed => _lpHandle != IntPtr.Zero;

    public int LastError { get; private set; }

    public bool Install()
    {
        if (_lpHandle != IntPtr.Zero)
        {
            return true;
        }

        _lpHandle = SetWindowsHookExW(WhMouseLl, _lpProcedure, IntPtr.Zero, 0);
        if (_lpHandle == IntPtr.Zero)
        {
            LastError = Marshal.GetLastWin32Error();
            return false;
        }

        return true;
    }

    /// <summary>重新装一次，抵消系统因超时静默摘钩的影响；返回重装是否成功。</summary>
    public bool Rearm()
    {
        if (_lpHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_lpHandle);
            _lpHandle = IntPtr.Zero;
        }

        return Install();
    }

    public void Remove()
    {
        if (_lpHandle == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_lpHandle);
        _lpHandle = IntPtr.Zero;
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var lpMessage = (int)wParam;
            if (lpMessage is WmMButtonDown or WmXButtonDown)
            {
                var lpData = Marshal.PtrToStructure<MouseHookData>(lParam);
                var lpButton = lpMessage == WmMButtonDown
                    ? 0
                    : ((lpData.MouseData >> 16) & 0xFFFF) == XButton1 ? 1
                    : ((lpData.MouseData >> 16) & 0xFFFF) == XButton2 ? 2
                    : -1;
                if (lpButton >= 0)
                {
                    try
                    {
                        _lpButtonPressed(lpButton);
                    }
                    catch
                    {
                        // 回调里的异常绝不能冒泡到系统，否则钩子会被直接摘掉。
                    }
                }
            }
        }

        // 鼠标按键不拦截：中键/侧键在原应用里的功能（新开标签、前进后退）照常生效。
        return CallNextHookEx(_lpHandle, code, wParam, lParam);
    }
}
