#Requires -Version 7.0
<#
.SYNOPSIS
  即时切换主显示器缩放（SystemParametersInfo SPI_SETLOGICALDPIOVERRIDE）。
.DESCRIPTION
  注册表 DpiValue + WM_SETTINGCHANGE 广播与 UIA 驱动设置应用两条路在本机都不可用
  （前者探针观察不到变化；后者设置应用内容进程已损坏），改用系统文档确认的即时通道：
  SPI_GETLOGICALDPIOVERRIDE(0x93) 读当前缩放档位索引，按 25% 一档换算目标索引后
  SPI_SETLOGICALDPIOVERRIDE(0x92) 应用。调用方用 --layout-probe 验证生效。
.EXAMPLE
  pwsh -NoProfile -File tools\Set-LpScale.ps1 -LpTargetPercent 125 -LpCurrentPercent 200
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(100, 125, 150, 175, 200, 225, 250)]
    [int]$LpTargetPercent,
    [Parameter(Mandatory)]
    [ValidateSet(100, 125, 150, 175, 200, 225, 250)]
    [int]$LpCurrentPercent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -Namespace 'LpSpi' -Name 'W' -MemberDefinition @'
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

    public static uint GetOverride()
    {
        uint value = 0;
        if (!SystemParametersInfoW(0x0093, 0, ref value, 0))
        {
            throw new Exception("SPI_GETLOGICALDPIOVERRIDE 失败, err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
        return value;
    }

    public static void SetOverride(uint index)
    {
        uint value = index;
        // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE
        if (!SystemParametersInfoW(0x0092, 0, ref value, 0x01 | 0x02))
        {
            throw new Exception("SPI_SETLOGICALDPIOVERRIDE 失败, err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
    }
'@

$LpCurIdx = [LpSpi.W]::GetOverride()
$LpDelta = ($LpTargetPercent - $LpCurrentPercent) / 25
$LpTargetIdx = $LpCurIdx + $LpDelta
if ($LpTargetIdx -lt 0) { throw ("目标 {0}% 低于当前档位表下限（当前索引 {1}，步长 25%）" -f $LpTargetPercent, $LpCurIdx) }

Write-Host ("当前索引={0}（{1}%），目标索引={2}（{3}%）" -f $LpCurIdx, $LpCurrentPercent, $LpTargetIdx, $LpTargetPercent)
[LpSpi.W]::SetOverride([uint32]$LpTargetIdx)
Write-Host ("已应用 {0}%" -f $LpTargetPercent)
exit 0
