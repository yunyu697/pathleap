#Requires -Version 7.0
<#
.SYNOPSIS
  快速切换栏 z 序 / 点击闪烁 / 拖动跟随的实机采样。
  用一个真实的 #32770 文件对话框（DialogHost.exe 拉起 OpenFileDialog）作为宿主，
  QsProbeLp.exe 负责 1ms 级采样与 SendInput 点击。

.EXAMPLE
  pwsh -NoProfile -File tools\Test-LpQuickBarProbe.ps1 -LpTag new
  pwsh -NoProfile -File tools\Test-LpQuickBarProbe.ps1 -LpTag baseline -LpExe "$env:TEMP\pathleap-baseline\Pathleap.UI.exe"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LpTag,
    [string]$LpExe = '',
    [int]$LpFlickerMs = 900,
    [int]$LpFollowSteps = 12,
    [int]$LpFollowStepPx = 6
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$LpRepo = Split-Path -Parent $PSScriptRoot
if ($LpExe.Length -eq 0) {
    $LpExe = Join-Path $LpRepo 'src\Pathleap.UI\bin\x64\Debug\net8.0-windows10.0.22621.0\Pathleap.UI.exe'
}
$LpWork = Join-Path $env:TEMP 'pathleap-scaling'
$LpProbe = Join-Path $LpWork 'QsProbeLp.exe'
$LpHost = Join-Path $LpWork 'DialogHost.exe'
$LpReport = Join-Path $LpWork ("qs-probe-{0}.txt" -f $LpTag)
if (-not (Test-Path -LiteralPath $LpProbe)) { throw "缺少采样助手：$LpProbe" }
if (-not (Test-Path -LiteralPath $LpHost)) { throw "缺少对话框宿主：$LpHost" }

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
namespace LpProbeNative
{
    public static class W
    {
        public delegate bool EnumProc(IntPtr h, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
        [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int idx);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int cb);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    }
}
'@ -WarningAction SilentlyContinue

$script:LpLines = New-Object System.Collections.Generic.List[string]
function Write-Lp {
    param([string]$LpText = '')
    Write-Host $LpText
    $script:LpLines.Add($LpText)
}

function Get-LpWindows {
    $script:LpAll = New-Object System.Collections.Generic.List[object]
    $LpCb = [LpProbeNative.W+EnumProc]{
        param($LpH, $LpL)
        if (-not [LpProbeNative.W]::IsWindowVisible($LpH)) { return $true }
        $LpSb = New-Object System.Text.StringBuilder 512
        [void][LpProbeNative.W]::GetWindowTextW($LpH, $LpSb, $LpSb.Capacity)
        $LpCls = New-Object System.Text.StringBuilder 256
        [void][LpProbeNative.W]::GetClassNameW($LpH, $LpCls, $LpCls.Capacity)
        $LpPid = [uint32]0
        [void][LpProbeNative.W]::GetWindowThreadProcessId($LpH, [ref]$LpPid)
        $script:LpAll.Add([pscustomobject]@{
            Handle = $LpH; Title = $LpSb.ToString(); Class = $LpCls.ToString(); Pid = $LpPid
        })
        return $true
    }
    [void][LpProbeNative.W]::EnumWindows($LpCb, [IntPtr]::Zero)
    return $script:LpAll
}

function Wait-LpWindow {
    param([scriptblock]$LpMatch, [int]$LpSeconds = 20, [string]$LpWhat = '窗口')
    $LpDeadline = (Get-Date).AddSeconds($LpSeconds)
    while ((Get-Date) -lt $LpDeadline) {
        $LpHit = Get-LpWindows | Where-Object { & $LpMatch $_ } | Select-Object -First 1
        if ($LpHit) { return $LpHit.Handle }
        Start-Sleep -Milliseconds 200
    }
    throw "等不到$LpWhat"
}

function Set-LpForeground {
    param([IntPtr]$LpH)
    $LpCur = [LpProbeNative.W]::GetCurrentThreadId()
    $LpFg = [LpProbeNative.W]::GetForegroundWindow()
    $LpPidOut = [uint32]0
    $LpFgThread = [LpProbeNative.W]::GetWindowThreadProcessId($LpFg, [ref]$LpPidOut)
    $LpAtt = ($LpCur -ne $LpFgThread) -and [LpProbeNative.W]::AttachThreadInput($LpCur, $LpFgThread, $true)
    try { [void][LpProbeNative.W]::SetForegroundWindow($LpH) }
    finally { if ($LpAtt) { [void][LpProbeNative.W]::AttachThreadInput($LpCur, $LpFgThread, $false) } }
}

function Get-LpOwner {
    param([IntPtr]$LpH)
    return [LpProbeNative.W]::GetWindowLongPtrW($LpH, -8)
}

function Get-LpBounds {
    param([IntPtr]$LpH)
    $LpR = New-Object LpProbeNative.W+RECT
    if ([LpProbeNative.W]::DwmGetWindowAttribute($LpH, 9, [ref]$LpR, 16) -ne 0) {
        [void][LpProbeNative.W]::GetWindowRect($LpH, [ref]$LpR)
    }
    return $LpR
}

function Invoke-LpProbe {
    param([string[]]$LpArgs)
    $LpOut = & $LpProbe @LpArgs 2>&1
    $LpOut | ForEach-Object { Write-Lp ([string]$_) }
    return $LpOut
}

function Test-LpOverlap {
    param($LpRectA, $LpRectB)
    $LpW = [Math]::Min($LpRectA.R, $LpRectB.R) - [Math]::Max($LpRectA.L, $LpRectB.L)
    $LpH = [Math]::Min($LpRectA.B, $LpRectB.B) - [Math]::Max($LpRectA.T, $LpRectB.T)
    if ($LpW -le 0 -or $LpH -le 0) { return '无重叠' }
    return ("重叠 {0}x{1}px" -f $LpW, $LpH)
}

function Find-LpBarFor {
    param([IntPtr]$LpDialog)
    $LpDeadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $LpDeadline) {
        $LpBars = Get-LpWindows | Where-Object { $_.Title -eq 'Pathleap 快速切换' }
        foreach ($LpBar in $LpBars) {
            if ((Get-LpOwner $LpBar.Handle) -eq $LpDialog) { return $LpBar.Handle }
        }
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}

function Close-LpWindow {
    param([IntPtr]$LpH)
    if ($LpH -ne [IntPtr]::Zero) { [void][LpProbeNative.W]::PostMessageW($LpH, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
}

# ---------- 清场 ----------
Get-Process Pathleap.UI -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process DialogHost -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

Write-Lp ("=== 快速切换栏实机采样 tag={0} ===" -f $LpTag)
Write-Lp ("exe={0}" -f $LpExe)
Write-Lp ("time={0:yyyy-MM-dd HH:mm:ss}" -f (Get-Date))

$LpApp = Start-Process -FilePath $LpExe -PassThru
Start-Sleep -Seconds 5

# ---------- 宿主对话框 1 ----------
$LpHost1 = Start-Process -FilePath $LpHost -ArgumentList @('打开（Pathleap 验收）') -PassThru
$LpDlg1 = Wait-LpWindow -LpWhat '文件对话框 1' -LpMatch { param($w) $w.Class -eq '#32770' -and $w.Pid -eq $LpHost1.Id }
Set-LpForeground $LpDlg1
Start-Sleep -Milliseconds 1500
$LpBar1 = Find-LpBarFor -LpDialog $LpDlg1
if ($LpBar1 -eq [IntPtr]::Zero) { throw '对话框 1 等不到快速切换栏' }
Write-Lp ("dialog1=0x{0:X} bar1=0x{1:X}" -f $LpDlg1.ToInt64(), $LpBar1.ToInt64())

# ---------- 1. z 序基线 ----------
Write-Lp ''
Write-Lp '--- [1] z 序：点击前 ---'
Invoke-LpProbe @('zorder', "$([int64]$LpBar1)", "$([int64]$LpDlg1)", '0') | Out-Null

# ---------- 2. 点击闪烁采样 ----------
Write-Lp ''
Write-Lp ("--- [2] 点击闪烁采样 {0}ms（在栏中心 SendInput 左键）---" -f $LpFlickerMs)
Invoke-LpProbe @('flicker', "$([int64]$LpBar1)", "$([int64]$LpDlg1)", "$LpFlickerMs", '1') | Out-Null

# ---------- 3. 点击后 z 序 ----------
Write-Lp ''
Write-Lp '--- [3] z 序：点击进入输入态后 ---'
Invoke-LpProbe @('zorder', "$([int64]$LpBar1)", "$([int64]$LpDlg1)", '0') | Out-Null

# ---------- 4. 拖动改变尺寸 → 栏跟随 ----------
Write-Lp ''
Write-Lp ("--- [4] 对话框逐级放大：{0} 步 × {1}px，采样栏位置延迟 ---" -f $LpFollowSteps, $LpFollowStepPx)
Invoke-LpProbe @('follow', "$([int64]$LpDlg1)", "$([int64]$LpBar1)", "$LpFollowSteps", "$LpFollowStepPx") | Out-Null

# ---------- 5. 遮挡：另一个窗口盖到对话框和栏上 ----------
Write-Lp ''
Write-Lp '--- [5] 遮挡窗口同时盖住对话框与栏（不激活，纯 z 序）---'
$LpCover = Start-Process -FilePath $LpHost -ArgumentList @('cover') -PassThru
$LpCoverH = Wait-LpWindow -LpWhat '遮挡窗口' -LpMatch { param($w) $w.Title -eq '遮挡测试' }
Start-Sleep -Milliseconds 1200
$LpBarRect = Get-LpBounds $LpBar1
$LpDlgRect = Get-LpBounds $LpDlg1
$LpCoverX = [int]$LpDlgRect.L
$LpCoverY = [int]$LpDlgRect.T
$LpCoverW = [int]($LpBarRect.R - $LpDlgRect.L)
$LpCoverH2 = [int]($LpBarRect.B - $LpDlgRect.T)
$LpCovered = $false
for ($LpTry = 1; $LpTry -le 3; $LpTry++) {
    [void][LpProbeNative.W]::SetWindowPos($LpCoverH, [IntPtr]::Zero, $LpCoverX, $LpCoverY, $LpCoverW, $LpCoverH2, 0x0010)
    Start-Sleep -Milliseconds 700
    $LpProbeBarRect = Get-LpBounds $LpBar1
    $LpPxTry = & $LpProbe 'pixel' ([int]($LpProbeBarRect.L + ($LpProbeBarRect.R - $LpProbeBarRect.L) / 2)) ([int]($LpProbeBarRect.T + ($LpProbeBarRect.B - $LpProbeBarRect.T) / 2))
    Write-Lp (('遮挡尝试 {0}: ' -f $LpTry) + ($LpPxTry -join ' '))
    if ($LpPxTry -match '#FF00FF') { $LpCovered = $true; break }
}
Invoke-LpProbe @('zorder', "$([int64]$LpBar1)", "$([int64]$LpDlg1)", "$([int64]$LpCoverH)") | Out-Null
Write-Lp (('栏被遮挡: {0}' -f $LpCovered))
Write-Lp (('栏-对话框矩形相交: {0}' -f (Test-LpOverlap $LpBarRect $LpDlgRect)))
$LpBarRect = Get-LpBounds $LpBar1
$LpPx = & $LpProbe 'pixel' ([int]($LpBarRect.L + ($LpBarRect.R - $LpBarRect.L) / 2)) ([int]($LpBarRect.T + ($LpBarRect.B - $LpBarRect.T) / 2))
Write-Lp (('栏中心屏幕像素（被遮挡时，遮挡窗为品红): ' + ($LpPx -join ' ')))
$LpPx2 = & $LpProbe 'pixel' ([int]($LpDlgRect.L + 300)) ([int]($LpDlgRect.T + 300))
Write-Lp (('对话框内部像素（被遮挡时）: ' + ($LpPx2 -join ' ')))

# ---------- 6. 遮挡窗口取得前台 ----------
Write-Lp ''
Write-Lp '--- [6] 遮挡窗口取得前台（对话框失去前台）---'
Set-LpForeground $LpCoverH
Start-Sleep -Milliseconds 900
Invoke-LpProbe @('zorder', "$([int64]$LpBar1)", "$([int64]$LpDlg1)", "$([int64]$LpCoverH)") | Out-Null

# ---------- 7. 对话框回到前台 ----------
Write-Lp ''
Write-Lp '--- [7] 对话框重新回到前台 ---'
Set-LpForeground $LpDlg1
Start-Sleep -Milliseconds 900
Invoke-LpProbe @('zorder', "$([int64]$LpBar1)", "$([int64]$LpDlg1)", "$([int64]$LpCoverH)") | Out-Null
$LpBarRect = Get-LpBounds $LpBar1
$LpPx = & $LpProbe 'pixel' ([int]($LpBarRect.L + ($LpBarRect.R - $LpBarRect.L) / 2)) ([int]($LpBarRect.T + ($LpBarRect.B - $LpBarRect.T) / 2))
Write-Lp (('栏中心屏幕像素（回到前台后）: ' + ($LpPx -join ' ')))

# ---------- 8. 多对话框 ----------
Write-Lp ''
Write-Lp '--- [8] 第二个对话框：各自跟自己的宿主 ---'
$LpHost2 = Start-Process -FilePath $LpHost -ArgumentList @('打开（Pathleap 验收 2）') -PassThru
$LpDlg2 = Wait-LpWindow -LpWhat '文件对话框 2' -LpMatch { param($w) $w.Class -eq '#32770' -and $w.Pid -eq $LpHost2.Id }
Set-LpForeground $LpDlg2
Start-Sleep -Milliseconds 1500
$LpBar2 = Find-LpBarFor -LpDialog $LpDlg2
if ($LpBar2 -eq [IntPtr]::Zero) { Write-Lp '对话框 2 没有生成自己的栏' }
else {
    Write-Lp ("dialog2=0x{0:X} bar2=0x{1:X}" -f $LpDlg2.ToInt64(), $LpBar2.ToInt64())
    Invoke-LpProbe @('zorder', "$([int64]$LpBar2)", "$([int64]$LpDlg2)", "$([int64]$LpBar1)") | Out-Null
    Write-Lp '把前台切回对话框 1：'
    Set-LpForeground $LpDlg1
    Start-Sleep -Milliseconds 900
    Invoke-LpProbe @('zorder', "$([int64]$LpBar1)", "$([int64]$LpDlg1)", "$([int64]$LpBar2)") | Out-Null
    Write-Lp '关闭对话框 2：'
    Close-LpWindow $LpDlg2
    Start-Sleep -Milliseconds 1500
    $LpBar2Alive = Get-LpWindows | Where-Object { $_.Handle -eq $LpBar2 }
    Write-Lp ("栏2 是否仍存在: {0}" -f ([bool]$LpBar2Alive))
    Invoke-LpProbe @('zorder', "$([int64]$LpBar1)", "$([int64]$LpDlg1)", '0') | Out-Null
}

# ---------- 收尾 ----------
Close-LpWindow $LpBar1 | Out-Null
Close-LpWindow $LpDlg1
Close-LpWindow $LpCoverH
Close-LpWindow $LpDlg2
Get-Process DialogHost -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process Pathleap.UI -ErrorAction SilentlyContinue | Stop-Process -Force

$script:LpLines | Set-Content -LiteralPath $LpReport -Encoding UTF8
Write-Host ''
Write-Host ("报告已写入 {0}" -f $LpReport)
