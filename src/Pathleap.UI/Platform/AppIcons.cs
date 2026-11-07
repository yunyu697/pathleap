using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.UI.Platform;

/// <summary>
/// 产品图标的唯一加载点：exe/安装包里的图标资源（build 时嵌入）与随程序分发的 .ico 文件是同一份图形。
/// 窗口/任务栏用 <see cref="Apply"/> 设置；托盘用 <see cref="LoadTrayIcon"/> 从 exe 资源提取，
/// 避免退回系统默认图标。
/// </summary>
internal static class AppIcons
{
    private static readonly string IconFilePath =
        Path.Combine(AppContext.BaseDirectory, "Assets", $"{Branding.ProductId}.ico");

    /// <summary>给窗口（含任务栏与 Alt+Tab）设置图标。</summary>
    public static void Apply(Window window)
    {
        try
        {
            if (!File.Exists(IconFilePath))
            {
                AppLog.Warn($"窗口图标文件缺失: {IconFilePath}");
                return;
            }

            window.AppWindow.SetIcon(IconFilePath);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置窗口图标失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 托盘图标：优先从当前进程 exe 的图标资源提取（16 px 小图标），
    /// 取不到时退回随程序分发的 .ico 文件；两者都失败才退回系统默认图标并记日志。
    /// 返回值由调用方通过 <see cref="ReleaseIcon"/> 释放。
    /// </summary>
    public static IntPtr LoadTrayIcon()
    {
        try
        {
            var lpExe = Environment.ProcessPath ?? Environment.GetCommandLineArgs().FirstOrDefault();
            if (!string.IsNullOrEmpty(lpExe) && File.Exists(lpExe))
            {
                var lpLarge = new IntPtr[1];
                var lpSmall = new IntPtr[1];
                if (NativeMethods.ExtractIconExW(lpExe, 0, lpLarge, lpSmall, 1) > 0)
                {
                    var lpIcon = lpSmall[0] != IntPtr.Zero ? lpSmall[0] : lpLarge[0];
                    if (lpSmall[0] != IntPtr.Zero && lpLarge[0] != IntPtr.Zero)
                    {
                        NativeMethods.DestroyIcon(lpLarge[0]);
                    }

                    if (lpIcon != IntPtr.Zero)
                    {
                        return lpIcon;
                    }
                }

                AppLog.Warn($"从 exe 提取托盘图标失败（Win32={Marshal.GetLastWin32Error()}）: {lpExe}");
            }

            if (File.Exists(IconFilePath))
            {
                var lpFromFile = NativeMethods.LoadImageW(
                    IntPtr.Zero,
                    IconFilePath,
                    NativeMethods.ImageIcon,
                    0,
                    0,
                    NativeMethods.LrLoadFromFile | NativeMethods.LrDefaultSize);
                if (lpFromFile != IntPtr.Zero)
                {
                    return lpFromFile;
                }

                AppLog.Warn($"从文件加载托盘图标失败（Win32={Marshal.GetLastWin32Error()}）: {IconFilePath}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"加载托盘图标异常: {ex.Message}");
        }

        AppLog.Error("托盘图标回退为系统默认图标。");
        return NativeMethods.LoadIconW(IntPtr.Zero, new IntPtr(NativeMethods.IdiApplication));
    }

    /// <summary>释放 <see cref="LoadTrayIcon"/> 返回的图标句柄。</summary>
    public static void ReleaseIcon(IntPtr icon)
    {
        if (icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(icon);
        }
    }
}
