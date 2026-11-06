using System.Runtime.InteropServices;

namespace Pathleap.Service;

/// <summary>
/// 无第三方依赖的 Windows 服务宿主（advapi32 P/Invoke）。
/// M1 不安装服务，默认用 --console；--service 用于 M2 的 SCM 安装验证。
/// </summary>
internal static class WindowsServiceHost
{
    private const int ServiceWin32OwnProcess = 0x00000010;
    private const int ServiceStateStopped = 0x00000001;
    private const int ServiceStateStartPending = 0x00000002;
    private const int ServiceStateStopPending = 0x00000003;
    private const int ServiceStateRunning = 0x00000004;
    private const int ServiceAcceptStop = 0x00000001;
    private const int ServiceAcceptShutdown = 0x00000004;
    private const int ServiceControlStop = 0x00000001;
    private const int ServiceControlShutdown = 0x00000005;
    private const int ServiceControlInterrogate = 0x00000004;

    private static string _lpServiceName = string.Empty;
    private static ServiceMainFunction? _lpServiceMain;
    private static HandlerExFunction? _lpHandler;
    private static IntPtr _lpStatusHandle;
    private static readonly ManualResetEventSlim StopEvent = new(false);
    private static ServiceRuntime? _lpRuntime;
    private static int _lpState = ServiceStateStopped;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainFunction(int argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HandlerExFunction(int control, int eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ServiceName;

        public IntPtr ServiceProcedure;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceCtrlDispatcherW(IntPtr serviceStartTable);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(
        string serviceName,
        HandlerExFunction handler,
        IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus status);

    public static int Run(string serviceName)
    {
        _lpServiceName = serviceName;
        _lpServiceMain = ServiceMain;
        _lpHandler = HandleControl;

        var lpTable = new ServiceTableEntry[2];
        lpTable[0].ServiceName = serviceName;
        lpTable[0].ServiceProcedure = Marshal.GetFunctionPointerForDelegate(_lpServiceMain);
        lpTable[1].ServiceName = null;
        lpTable[1].ServiceProcedure = IntPtr.Zero;

        var lpPinned = GCHandle.Alloc(lpTable, GCHandleType.Pinned);
        try
        {
            if (!StartServiceCtrlDispatcherW(lpPinned.AddrOfPinnedObject()))
            {
                var lpError = Marshal.GetLastWin32Error();
                Shared.Diagnostics.AppLog.Error(
                    $"SCM 启动失败（Win32={lpError}）。本进程不是由服务控制管理器启动；M1 请使用 --console。");
                return 2;
            }

            return 0;
        }
        finally
        {
            lpPinned.Free();
        }
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _lpStatusHandle = RegisterServiceCtrlHandlerExW(_lpServiceName, _lpHandler!, IntPtr.Zero);
        if (_lpStatusHandle == IntPtr.Zero)
        {
            Shared.Diagnostics.AppLog.Error($"RegisterServiceCtrlHandlerEx 失败（Win32={Marshal.GetLastWin32Error()}）");
            return;
        }

        ReportStatus(ServiceStateStartPending, controlsAccepted: 0, checkPoint: 1, waitHintMs: 3000);
        try
        {
            Shared.Diagnostics.AppLog.Initialize("service");
            _lpRuntime = new ServiceRuntime("service");
            _lpRuntime.Start();
            ReportStatus(ServiceStateRunning, ServiceAcceptStop | ServiceAcceptShutdown, 0, 0);
            StopEvent.Wait();
            ReportStatus(ServiceStateStopPending, 0, 1, 3000);
        }
        catch (Exception ex)
        {
            Shared.Diagnostics.AppLog.Error("服务主循环异常", ex);
        }
        finally
        {
            try
            {
                _lpRuntime?.Stop();
                _lpRuntime?.Dispose();
            }
            finally
            {
                ReportStatus(ServiceStateStopped, 0, 0, 0);
                Shared.Diagnostics.AppLog.Shutdown();
            }
        }
    }

    private static int HandleControl(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ServiceControlStop:
            case ServiceControlShutdown:
                ReportStatus(ServiceStateStopPending, 0, 1, 3000);
                StopEvent.Set();
                return 0;

            case ServiceControlInterrogate:
                ReportStatus(_lpState, _lpState == ServiceStateRunning ? ServiceAcceptStop | ServiceAcceptShutdown : 0, 0, 0);
                return 0;

            default:
                return 0;
        }
    }

    private static void ReportStatus(int state, int controlsAccepted, int checkPoint, int waitHintMs)
    {
        if (_lpStatusHandle == IntPtr.Zero)
        {
            return;
        }

        _lpState = state;
        var lpStatus = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = controlsAccepted,
            Win32ExitCode = 0,
            ServiceSpecificExitCode = 0,
            CheckPoint = checkPoint,
            WaitHint = waitHintMs
        };
        SetServiceStatus(_lpStatusHandle, ref lpStatus);
    }
}
