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
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
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
    for ($LpTry = 1; $LpTry -le 5; $LpTry++) {
        $LpCur = [LpProbeNative.W]::GetCurrentThreadId()
        $LpFg = [LpProbeNative.W]::GetForegroundWindow()
        $LpPidOut = [uint32]0
        $LpFgThread = [LpProbeNative.W]::GetWindowThreadProcessId($LpFg, [ref]$LpPidOut)
        $LpAtt = ($LpCur -ne $LpFgThread) -and [LpProbeNative.W]::AttachThreadInput($LpCur, $LpFgThread, $true)
        try { [void][LpProbeNative.W]::SetForegroundWindow($LpH) }
        finally { if ($LpAtt) { [void][LpProbeNative.W]::AttachThreadInput($LpCur, $LpFgThread, $false) } }
        Start-Sleep -Milliseconds 250
        if ([LpProbeNative.W]::GetForegroundWindow() -eq $LpH) { return $true }
    }
    Write-Lp ("前台切换失败: 目标 0x{0:X}，当前 0x{1:X}" -f $LpH.ToInt64(), [LpProbeNative.W]::GetForegroundWindow().ToInt64())
    return $false
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
    $LpSeen = 0
    while ((Get-Date) -lt $LpDeadline) {
        $LpBars = @(Get-LpWindows | Where-Object { $_.Title -eq 'Pathleap 快速切换' })
        $LpSeen = [Math]::Max($LpSeen, $LpBars.Count)
        foreach ($LpBar in $LpBars) {
            if ((Get-LpOwner $LpBar.Handle) -eq $LpDialog) { return $LpBar.Handle }
        }
        Start-Sleep -Milliseconds 250
    }
    Write-Lp ("找不到属于对话框 0x{0:X} 的栏（期间见过 {1} 个栏）" -f $LpDialog.ToInt64(), $LpSeen)
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
$LpDlg1 = Wait-LpWindow -LpWhat '文件对话框 1' -LpMatch { param($LpWin) $LpWin.Class -eq '#32770' -and $LpWin.Pid -eq $LpHost1.Id }
Set-LpForeground $LpDlg1
Start-Sleep -Milliseconds 1500
$LpBar1 = Find-LpBarFor -LpDialog $LpDlg1
if ($LpBar1 -eq [IntPtr]::Zero) { throw '对话框 1 等不到快速切换栏' }
Write-Lp ("dialog1=0x{0:X} bar1=0x{1:X}" -f $LpDlg1.ToInt64(), $LpBar1.ToInt64())

# ---------- 1. z 序基线 ----------
Write-Lp ''
Write-Lp '--- [1] z 序：栏空闲吸附态 ---'
Invoke-LpProbe @('zorder', "$([int64]$LpBar1)", "$([int64]$LpDlg1)", '0') | Out-Null

# ---------- 2. 遮挡：另一个窗口盖到对话框和栏上 ----------
Write-Lp ''
Write-Lp '--- [2] 另一个窗口盖住对话框与栏（遮挡窗插到对话框正上方）---'
$LpCover = Start-Process -FilePath $LpHost -ArgumentList @('cover') -PassThru
$LpCoverH = Wait-LpWindow -LpWhat '遮挡窗口' -LpMatch { param($LpWin) $LpWin.Title -eq '遮挡测试' }
# 遮挡窗显示时会自己激活到最前，等价于用户切到别的应用窗口。
Start-Sleep -Milliseconds 1500
$LpBarRect = Get-LpBounds $LpBar1
$LpDlgRect = Get-LpBounds $LpDlg1
Invoke-LpProbe @('zorder', "$([int64]$LpBar1)", "$([int64]$LpDlg1)", "$([int64]$LpCoverH)") | Out-Null
$LpPx = & $LpProbe 'pixel' ([int]($LpBarRect.L + ($LpBarRect.R - $LpBarRect.L) / 2)) ([int]($LpBarRect.T + ($LpBarRect.B - $LpBarRect.T) / 2))
$LpPx2 = & $LpProbe 'pixel' ([int]($LpDlgRect.L + 300)) ([int]($LpDlgRect.T + 300))
Write-Lp (('对话框被遮挡: {0}（像素 {1}）' -f ((($LpPx2 -join ' ') -match '#FF00FF')), ($LpPx2 -join ' ')))
Write-Lp (('栏被遮挡: {0}（像素 {1}）' -f ((($LpPx -join ' ') -match '#FF00FF'), ($LpPx -join ' '))))
Write-Lp (('栏-对话框矩形相交: {0}' -f (Test-LpOverlap $LpBarRect $LpDlgRect)))
Write-Lp ('窗口可见性: 栏={0} 对话框={1} 遮挡窗={2}' -f ([LpProbeNative.W]::IsWindowVisible($LpBar1)), ([LpProbeNative.W]::IsWindowVisible($LpDlg1)), ([LpProbeNative.W]::IsWindowVisible($LpCoverH)))

# 遮挡物移走 + 对话框回前台，栏应原样回来
[void][LpProbeNative.W]::PostMessageW($LpCoverH, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500
Set-LpForeground $LpDlg1
Start-Sleep -Milliseconds 900
Write-Lp ('遮挡移除 + 对话框回前台 → 栏可见: {0}' -f ([LpProbeNative.W]::IsWindowVisible($LpBar1)))

# ---------- 3. 点击闪烁采样 ----------
Write-Lp ''
Write-Lp ("--- [3] 点击闪烁采样 {0}ms（在栏中心 SendInput 左键）---" -f $LpFlickerMs)
Invoke-LpProbe @('flicker', "$([int64]$LpBar1)", "$([int64]$LpDlg1)", "$LpFlickerMs", '1') | Out-Null

# ---------- 4. 点击后 z 序 ----------
Write-Lp ''
Write-Lp '--- [4] z 序：点击进入输入态后 ---'
Invoke-LpProbe @('zorder', "$([int64]$LpBar1)", "$([int64]$LpDlg1)", '0') | Out-Null

# ---------- 5. 拖动改变尺寸 → 栏跟随 ----------
Write-Lp ''
Write-Lp ("--- [5] 对话框逐级放大：{0} 步 × {1}px，采样栏位置延迟 ---" -f $LpFollowSteps, $LpFollowStepPx)
Invoke-LpProbe @('follow', "$([int64]$LpDlg1)", "$([int64]$LpBar1)", "$LpFollowSteps", "$LpFollowStepPx") | Out-Null

# ---------- 6. 最小化 / 还原 ----------
Write-Lp ''
Write-Lp '--- [6] 对话框最小化 / 还原 ---'
$LpVisBefore = [LpProbeNative.W]::IsWindowVisible($LpBar1)
[void][LpProbeNative.W]::ShowWindow($LpDlg1, 6)
Start-Sleep -Milliseconds 900
Write-Lp ('最小化前 栏可见: {0}；最小化后 栏可见: {1}' -f $LpVisBefore, ([LpProbeNative.W]::IsWindowVisible($LpBar1)))
[void][LpProbeNative.W]::ShowWindow($LpDlg1, 9)
Start-Sleep -Milliseconds 1200
Write-Lp ('还原后 栏可见: {0}' -f ([LpProbeNative.W]::IsWindowVisible($LpBar1)))
Invoke-LpProbe @('zorder', "$([int64]$LpBar1)", "$([int64]$LpDlg1)", '0') | Out-Null

# ---------- 8. 多对话框 ----------
Write-Lp ''
Write-Lp '--- [8] 第二个对话框：各自跟自己的宿主 ---'
$LpHost2 = Start-Process -FilePath $LpHost -ArgumentList @('打开（Pathleap 验收 2）') -PassThru
$LpDlg2 = Wait-LpWindow -LpWhat '文件对话框 2' -LpMatch { param($LpWin) $LpWin.Class -eq '#32770' -and $LpWin.Pid -eq $LpHost2.Id }
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
