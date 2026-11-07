using System.Runtime.InteropServices;

namespace Pathleap.UI.Platform;

/// <summary>
/// 低层键盘钩子（WH_KEYBOARD_LL）。回调必须极短：只做状态判断与投递，绝不做 IO/日志。
/// 系统在回调超时后会静默摘掉钩子，因此由消息线程定期 Rearm 重装。
/// </summary>
internal sealed class KeyboardHook
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint LlkhfInjected = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
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
    private readonly Func<int, bool, bool, bool> _lpInterceptor;
    private IntPtr _lpHandle;

    public KeyboardHook(Func<int, bool, bool, bool> interceptor)
    {
        _lpInterceptor = interceptor;
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

        _lpHandle = SetWindowsHookExW(WhKeyboardLl, _lpProcedure, IntPtr.Zero, 0);
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
            if (lpMessage is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
            {
                var lpData = Marshal.PtrToStructure<KeyboardHookData>(lParam);
                var lpInjected = (lpData.Flags & LlkhfInjected) != 0;
                var lpDown = lpMessage is WmKeyDown or WmSysKeyDown;
                try
                {
                    if (_lpInterceptor((int)lpData.VirtualKey, lpDown, lpInjected))
                    {
                        return new IntPtr(1);
                    }
                }
                catch
                {
                    // 回调里的异常绝不能冒泡到系统，否则钩子会被直接摘掉。
                }
            }
        }

        return CallNextHookEx(_lpHandle, code, wParam, lParam);
    }
}
