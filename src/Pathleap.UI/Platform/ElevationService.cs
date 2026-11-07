using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.UI.Platform;

/// <summary>安装范围：由安装包写入注册表；读取不到时视为便携版。</summary>
public sealed record InstallInfo(string Scope, string Directory, string Version)
{
    public bool IsInstalled => Scope is "allusers" or "currentuser";

    public string ScopeLabel => Scope switch
    {
        "allusers" => "所有用户",
        "currentuser" => "仅当前用户",
        _ => "便携版"
    };
}

/// <summary>
/// 「始终以管理员身份运行」的落地方案：注册一个无触发器的计划任务（最高权限），
/// 启动时用 schtasks /Run 拉起，不使用 Run 注册表项。任务注册/删除需要一次 UAC。
/// </summary>
public static class ElevationService
{
    private const string TaskPrefix = "Pathleap-Elevated";
    private const string PendingFileName = "pending-launch.txt";
    private const int PendingFreshSeconds = 30;

    private const int SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    private const uint INFINITE = 0xFFFFFFFF;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo lpExecInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string lpParameters;
        public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    /// <summary>当前进程是否处于提升后的管理员令牌（High/System 完整性级别）。</summary>
    public static bool IsElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /*TOKEN_QUERY*/, out var lpToken))
        {
            return false;
        }

        try
        {
            return GetTokenInformation(lpToken, 20 /*TokenElevation*/, out var lpElevated, sizeof(int), out _)
                   && lpElevated != 0;
        }
        finally
        {
            CloseHandle(lpToken);
        }
    }

    /// <summary>任务名按用户隔离，避免同一台机器上的多个用户互相覆盖。</summary>
    public static string TaskName
    {
        get
        {
            var lpSid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
            return $"{TaskPrefix}-{lpSid}";
        }
    }

    /// <summary>读取安装范围；两个注册表视图都没有值时按便携版处理。</summary>
    public static InstallInfo ReadInstallInfo()
    {
        var lpMachine = ReadScope(RegistryHive.LocalMachine);
        if (lpMachine is not null)
        {
            return lpMachine;
        }

        return ReadScope(RegistryHive.CurrentUser) ?? new InstallInfo("portable", string.Empty, string.Empty);
    }

    private static InstallInfo? ReadScope(RegistryHive hive)
    {
        try
        {
            using var lpKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64)
                .OpenSubKey(@"Software\" + Branding.ProductId);
            if (lpKey is null)
            {
                return null;
            }

            var lpScope = lpKey.GetValue("InstallScope") as string;
            if (string.IsNullOrWhiteSpace(lpScope))
            {
                return null;
            }

            return new InstallInfo(
                lpScope,
                lpKey.GetValue("InstallDir") as string ?? string.Empty,
                lpKey.GetValue("InstallVersion") as string ?? string.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取安装范围失败（{hive}）: {ex.Message}");
            return null;
        }
    }

    /// <summary>查询计划任务是否存在（不弹 UAC，schtasks /Query 允许普通用户查询自己的任务）。</summary>
    public static bool TaskExists()
    {
        var lpCode = RunHidden("schtasks.exe", $"/Query /TN \"{TaskName}\"");
        return lpCode == 0;
    }

    /// <summary>注册计划任务：无触发器、仅登录时运行、最高权限。失败时给出可读原因。</summary>
    public static bool TryCreateTask(out string error)
    {
        error = string.Empty;
        var lpExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(lpExe))
        {
            error = "无法确定当前程序路径";
            return false;
        }

        var lpXml = BuildTaskXml(lpExe, Path.GetDirectoryName(lpExe) ?? string.Empty);
        var lpTemp = Path.Combine(Path.GetTempPath(), $"pathleap-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(lpTemp, lpXml, Encoding.Unicode);
        }
        catch (Exception ex)
        {
            error = $"写入任务定义失败: {ex.Message}";
            return false;
        }

        try
        {
            var lpCode = RunElevated("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{lpTemp}\" /F", out var lpCanceled);
            if (lpCanceled)
            {
                error = "已取消 UAC 授权";
                return false;
            }

            if (lpCode != 0)
            {
                error = $"schtasks /Create 返回 {lpCode}";
                return false;
            }

            AppLog.Info($"计划任务已注册: {TaskName}");
            return true;
        }
        finally
        {
            try
            {
                File.Delete(lpTemp);
            }
            catch (Exception)
            {
                // 临时文件删不掉不影响功能。
            }
        }
    }

    public static bool TryDeleteTask(out string error)
    {
        error = string.Empty;
        var lpCode = RunElevated("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", out var lpCanceled);
        if (lpCanceled)
        {
            error = "已取消 UAC 授权";
            return false;
        }

        if (lpCode != 0 && TaskExists())
        {
            error = $"schtasks /Delete 返回 {lpCode}";
            return false;
        }

        AppLog.Info($"计划任务已删除: {TaskName}");
        return true;
    }

    /// <summary>以任务方式启动（任务已注册时不需要再确认 UAC）。</summary>
    public static bool RunTask()
    {
        if (!TaskExists())
        {
            AppLog.Warn("计划任务不存在，无法提权启动。");
            return false;
        }

        var lpCode = RunHidden("schtasks.exe", $"/Run /TN \"{TaskName}\"");
        if (lpCode != 0)
        {
            AppLog.Warn($"schtasks /Run 返回 {lpCode}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 非提权实例把原始命令行留给提权实例：任务的 Action 固定带 --elevated-launch，
    /// 其余参数（含 --tray / --quit / --show）靠这个文件交接。
    /// </summary>
    public static void WritePendingArgs(string[] args)
    {
        try
        {
            AppPaths.EnsureDirectory(AppPaths.UserRoot);
            File.WriteAllText(
                Path.Combine(AppPaths.UserRoot, PendingFileName),
                JsonSerializer.Serialize(new PendingLaunch(
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    args)));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"写入待转发命令行失败: {ex.Message}");
        }
    }

    /// <summary>提权实例启动时消费原始命令行；超过 30 秒视为陈旧并丢弃。</summary>
    public static string[]? ConsumePendingArgs()
    {
        var lpPath = Path.Combine(AppPaths.UserRoot, PendingFileName);
        try
        {
            if (!File.Exists(lpPath))
            {
                return null;
            }

            var lpHad = File.ReadAllText(lpPath);
            File.Delete(lpPath);
            var lpPending = JsonSerializer.Deserialize<PendingLaunch>(lpHad, JsonDefaults.Compact);
            if (lpPending is null
                || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lpPending.Timestamp > PendingFreshSeconds)
            {
                return null;
            }

            return lpPending.Args;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取待转发命令行失败: {ex.Message}");
            return null;
        }
    }

    private sealed record PendingLaunch(long Timestamp, string[] Args);

    private static string BuildTaskXml(string exe, string workingDirectory)
    {
        var lpUser = WindowsIdentity.GetCurrent().Name;
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Pathleap 以最高权限启动（由设置开关维护）</Description>
              </RegistrationInfo>
              <Triggers />
              <Principals>
                <Principal id="Author">
                  <UserId>{XmlEscape(lpUser)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{XmlEscape(exe)}</Command>
                  <Arguments>--elevated-launch</Arguments>
                  <WorkingDirectory>{XmlEscape(workingDirectory)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string XmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static int RunHidden(string file, string arguments)
    {
        try
        {
            using var lpProcess = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (lpProcess is null)
            {
                return -1;
            }

            lpProcess.WaitForExit(15000);
            return lpProcess.HasExited ? lpProcess.ExitCode : -1;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"{file} {arguments} 执行失败: {ex.Message}");
            return -1;
        }
    }

    private static int RunElevated(string file, string arguments, out bool canceled)
    {
        canceled = false;
        var lpInfo = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SEE_MASK_NOCLOSEPROCESS,
            lpVerb = "runas",
            lpFile = file,
            lpParameters = arguments,
            nShow = 0
        };

        if (!ShellExecuteExW(ref lpInfo))
        {
            var lpError = Marshal.GetLastWin32Error();
            canceled = lpError == 1223; /*ERROR_CANCELLED*/
            AppLog.Warn($"提权执行 {file} 失败: Win32={lpError}");
            return -1;
        }

        try
        {
            WaitForSingleObject(lpInfo.hProcess, INFINITE);
            return GetExitCodeProcess(lpInfo.hProcess, out var lpCode) ? lpCode : -1;
        }
        finally
        {
            if (lpInfo.hProcess != IntPtr.Zero)
            {
                CloseHandle(lpInfo.hProcess);
            }
        }
    }
}
