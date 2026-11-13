#Requires -Version 7.0
<#
.SYNOPSIS
  D 项单发实拍：光标移到副屏 → 拉起设置窗 → 验证「按光标所在屏工作区居中」与副屏字形几何。
  pwsh 进程清单为 PerMonitorV2，SetCursorPos/MonitorFromPoint/GetMonitorInfoW/
  Screen.AllScreens 全部返回物理坐标，可直接比较。
.EXAMPLE
  pwsh -NoProfile -File tools\Test-LpShotDual.ps1 -LpNum 43 -LpTag dual
#>
[CmdletBinding()]
param(
    [int]$LpNum = 43,
    [string]$LpTag = 'dual'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$LpRepo = Split-Path -Parent $PSScriptRoot
$LpExe = Join-Path $LpRepo 'src\Pathleap.UI\bin\x64\Debug\net8.0-windows10.0.22621.0\Pathleap.UI.exe'
$LpShotsDir = Join-Path $LpRepo 'docs\shots'
$LpWork = Join-Path $env:TEMP 'pathleap-scaling'
$LpCaptureExe = Join-Path $LpWork 'CaptureLp.exe'
$LpWindowTitle = '设置 · Pathleap'
New-Item -ItemType Directory -Force -Path $LpShotsDir | Out-Null
if (-not (Test-Path -LiteralPath $LpCaptureExe)) { throw '缺少抓取助手，请先运行 Test-LpShotOnce.ps1 编译 CaptureLp' }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace LpDualNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MONINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }
    public static class W
    {
        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr hMonitor, IntPtr info);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dx, out uint dy);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder sb, int n);
    }
}
'@ -WarningAction SilentlyContinue

function Get-LpMonitorInfo {
    # 指针法取 MONITORINFO，绕开 PS 装箱 struct [ref] 的 marshalling 陷阱（此前恒 err=87）。
    # 返回 rect 为本进程（system-DPI-aware）虚拟坐标。
    param([IntPtr]$LpMon)
    $LpSize = 40
    $LpBuf = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($LpSize)
    try {
        $LpZero = New-Object byte[] $LpSize
        [System.Runtime.InteropServices.Marshal]::Copy($LpZero, 0, $LpBuf, $LpSize)
        [System.Runtime.InteropServices.Marshal]::WriteInt32($LpBuf, $LpSize)
        $LpOk = [LpDualNative.W]::GetMonitorInfoW($LpMon, $LpBuf)
        if (-not $LpOk) { return $null }
        return [System.Runtime.InteropServices.Marshal]::PtrToStructure($LpBuf, [type][LpDualNative.MONINFO])
    }
    finally { [System.Runtime.InteropServices.Marshal]::FreeHGlobal($LpBuf) }
}

function Get-LpMonitorDpi {
    param([IntPtr]$LpMon)
    $LpDx = [uint32]0; $LpDy = [uint32]0
    [void][LpDualNative.W]::GetDpiForMonitor($LpMon, 0, [ref]$LpDx, [ref]$LpDy)
    return $LpDx
}

# ---------- 1. 找副屏前的探针（仅确认会话主屏缩放生效） ----------
$LpPt0 = New-Object LpDualNative.POINT
$LpPt0.X = 50; $LpPt0.Y = 50
$LpPrimaryMon = [LpDualNative.W]::MonitorFromPoint($LpPt0, 1)
$LpPrimaryDpi = Get-LpMonitorDpi $LpPrimaryMon
Write-Host ("主屏 dpi={0}" -f $LpPrimaryDpi)

# ---------- 2. 找副屏（pwsh 进程为 PerMonitorV2，所有 API 均为物理坐标，无需换算） ----------
$LpSecScreens = @([System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary })
if ($LpSecScreens.Count -eq 0) { throw '未检测到副屏（多显示器 D 项需要第二块屏）' }
$LpSec = $LpSecScreens[0]
$LpSecPhysL = $LpSec.Bounds.X; $LpSecPhysT = $LpSec.Bounds.Y
$LpSecPhysW = $LpSec.Bounds.Width; $LpSecPhysH = $LpSec.Bounds.Height
$LpPt = New-Object LpDualNative.POINT
$LpPt.X = [int]($LpSecPhysL + $LpSecPhysW / 2); $LpPt.Y = [int]($LpSecPhysT + $LpSecPhysH / 2)
$LpHmon = [LpDualNative.W]::MonitorFromPoint($LpPt, 1)
$LpMonDpi = Get-LpMonitorDpi $LpHmon
$LpMonInfo = Get-LpMonitorInfo $LpHmon
if ($null -eq $LpMonInfo) { throw ("GetMonitorInfoW 失败 hmon=0x{0:X}" -f $LpHmon.ToInt64()) }
Write-Host ("副屏 {0}: 物理=({1},{2}) {3}x{4} dpi={5}" -f `
    $LpSec.DeviceName, $LpSecPhysL, $LpSecPhysT, $LpSecPhysW, $LpSecPhysH, $LpMonDpi)
Write-Host ("  work(物理)=({0},{1})-({2},{3})" -f $LpMonInfo.Work.L, $LpMonInfo.Work.T, $LpMonInfo.Work.R, $LpMonInfo.Work.B)

# ---------- 3. 光标移到副屏 work 中心（物理坐标），启动设置窗 ----------
$LpCurX = [int](($LpMonInfo.Work.L + $LpMonInfo.Work.R) / 2)
$LpCurY = [int](($LpMonInfo.Work.T + $LpMonInfo.Work.B) / 2)
if (-not [LpDualNative.W]::SetCursorPos($LpCurX, $LpCurY)) { throw 'SetCursorPos 失败' }
Write-Host ("光标已移至 ({0},{1})（副屏 work 物理中心）" -f $LpCurX, $LpCurY)
Start-Sleep -Milliseconds 300

$script:LpFound = [IntPtr]::Zero
Get-Process | Where-Object { $_.ProcessName -eq 'Pathleap.UI' } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
$LpProc = Start-Process -FilePath $LpExe -ArgumentList '--screen=settings' -PassThru
$LpDeadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $LpDeadline -and $script:LpFound -eq [IntPtr]::Zero) {
    $LpEnumCb = [LpDualNative.W+EnumProc]{
        param($LpH, $LpL)
        if (-not [LpDualNative.W]::IsWindowVisible($LpH)) { return $true }
        $LpSb = New-Object System.Text.StringBuilder 512
        [void][LpDualNative.W]::GetWindowTextW($LpH, $LpSb, $LpSb.Capacity)
        if ($LpSb.ToString() -eq $LpWindowTitle) { $script:LpFound = $LpH; return $false }
        return $true
    }
    [LpDualNative.W]::EnumWindows($LpEnumCb, [IntPtr]::Zero) | Out-Null
    if ($script:LpFound -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 400 }
}
if ($script:LpFound -eq [IntPtr]::Zero) {
    Get-Process -Id $LpProc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
    throw '等不到设置窗'
}
Start-Sleep -Milliseconds 2500

# ---------- 4. 抓物理像素 + 量字形 ----------
$LpFullShot = Join-Path $LpShotsDir ("{0}-{1}-settings.png" -f $LpNum, $LpTag)
$LpZoomShot = Join-Path $LpShotsDir ("{0}-{1}-caption.png" -f $LpNum, $LpTag)
$LpRaw = Join-Path $LpWork ("raw-{0}.png" -f $LpTag)
$LpBounds = & $LpCaptureExe ([int64]$script:LpFound) $LpRaw
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $LpRaw)) { throw ("抓取失败 exit={0}" -f $LASTEXITCODE) }
$LpParts = $LpBounds.Trim() -split ' '
$LpWinL = [int]$LpParts[0]; $LpWinT = [int]$LpParts[1]; $LpWinW = [int]$LpParts[2]; $LpWinH = [int]$LpParts[3]

function Get-LpGlyphMetrics {
    param([System.Drawing.Bitmap]$LpBmp, [double]$LpScale)
    $LpW = $LpBmp.Width
    $LpStripH = [int][Math]::Ceiling(37 * $LpScale)
    $LpBandY0 = [int][Math]::Ceiling(10 * $LpScale)
    $LpBandY1 = [Math]::Min($LpStripH - 1, [int][Math]::Ceiling(31 * $LpScale))
    $LpHist = @{}
    for ($LpX = $LpW - [int][Math]::Ceiling(150 * $LpScale); $LpX -lt $LpW; $LpX += 3) {
        for ($LpY = 2; $LpY -lt $LpStripH; $LpY += 3) {
            $LpC = $LpBmp.GetPixel($LpX, $LpY)
            $LpK = ('{0}_{1}_{2}' -f [int]($LpC.R / 8), [int]($LpC.G / 8), [int]($LpC.B / 8))
            $LpHist[$LpK] = 1 + $LpHist[$LpK]
        }
    }
    $LpTopKey = $LpHist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1
    $LpBgParts = $LpTopKey.Key.Split('_')
    $LpBgR = [int]$LpBgParts[0] * 8 + 4; $LpBgG = [int]$LpBgParts[1] * 8 + 4; $LpBgB = [int]$LpBgParts[2] * 8 + 4

    function Get-LpMaskBox {
        param([int]$LpX0, [int]$LpX1, [int]$LpThreshold)
        $LpMinX = [int]::MaxValue; $LpMinY = [int]::MaxValue; $LpMaxX = -1; $LpMaxY = -1; $LpCount = 0
        for ($LpX = $LpX0; $LpX -le $LpX1; $LpX++) {
            for ($LpY = $LpBandY0; $LpY -le $LpBandY1; $LpY++) {
                $LpC = $LpBmp.GetPixel($LpX, $LpY)
                $LpD = [Math]::Max([Math]::Abs($LpC.R - $LpBgR), [Math]::Max([Math]::Abs($LpC.G - $LpBgG), [Math]::Abs($LpC.B - $LpBgB)))
                if ($LpD -gt $LpThreshold) {
                    $LpCount++
                    if ($LpX -lt $LpMinX) { $LpMinX = $LpX }
                    if ($LpX -gt $LpMaxX) { $LpMaxX = $LpX }
                    if ($LpY -lt $LpMinY) { $LpMinY = $LpY }
                    if ($LpY -gt $LpMaxY) { $LpMaxY = $LpY }
                }
            }
        }
        if ($LpCount -eq 0) { return $null }
        return [pscustomobject]@{ X0 = $LpMinX; Y0 = $LpMinY; X1 = $LpMaxX; Y1 = $LpMaxY }
    }

    $LpZones = [ordered]@{
        min   = @(($LpW - [int][Math]::Ceiling(92 * $LpScale)), ($LpW - [int][Math]::Ceiling(48 * $LpScale)))
        close = @(($LpW - [int][Math]::Ceiling(46 * $LpScale)), ($LpW - 2))
    }
    $LpOut = @{}
    foreach ($LpName in $LpZones.Keys) {
        $LpTight = Get-LpMaskBox $LpZones[$LpName][0] $LpZones[$LpName][1] 60
        if ($LpTight) { $LpOut[$LpName] = '{0}x{1}' -f ($LpTight.X1 - $LpTight.X0 + 1), ($LpTight.Y1 - $LpTight.Y0 + 1) }
        else { $LpOut[$LpName] = '-' }
    }
    return [pscustomobject]$LpOut
}

$LpBmp = [System.Drawing.Bitmap]::FromFile($LpRaw)
try {
    $LpScale = $LpMonDpi / 96
    $LpMetrics = Get-LpGlyphMetrics -LpBmp $LpBmp -LpScale $LpScale
    $LpCropW = [int][Math]::Min($LpBmp.Width, [Math]::Ceiling(150 * $LpScale))
    $LpCropH = [int][Math]::Ceiling(42 * $LpScale)
    $LpOutBmp = New-Object System.Drawing.Bitmap ($LpCropW * 4), ($LpCropH * 4)
    $LpG = [System.Drawing.Graphics]::FromImage($LpOutBmp)
    $LpG.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $LpG.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $LpG.DrawImage($LpBmp, (New-Object System.Drawing.Rectangle 0, 0, ($LpCropW * 4), ($LpCropH * 4)),
        (New-Object System.Drawing.Rectangle ($LpBmp.Width - $LpCropW), 0, $LpCropW, $LpCropH),
        [System.Drawing.GraphicsUnit]::Pixel)
    $LpG.Dispose()
    $LpOutBmp.Save($LpZoomShot, [System.Drawing.Imaging.ImageFormat]::Png)
    $LpOutBmp.Dispose()
}
finally { $LpBmp.Dispose() }
Copy-Item -LiteralPath $LpRaw -Destination $LpFullShot -Force
Get-Process -Id $LpProc.Id -ErrorAction SilentlyContinue | Stop-Process -Force

# ---------- 5. 校验：窗中心 vs 光标屏 work 中心（物理坐标对物理坐标） ----------
$LpScaleD = $LpMonDpi / 96
$LpExpectW = [Math]::Round(840 * $LpScaleD); $LpExpectH = [Math]::Round(640 * $LpScaleD)
$LpWorkPhysL = $LpMonInfo.Work.L; $LpWorkPhysT = $LpMonInfo.Work.T
$LpWorkPhysR = $LpMonInfo.Work.R; $LpWorkPhysB = $LpMonInfo.Work.B
$LpWinCx = $LpWinL + $LpWinW / 2; $LpWinCy = $LpWinT + $LpWinH / 2
$LpWorkCx = ($LpWorkPhysL + $LpWorkPhysR) / 2; $LpWorkCy = ($LpWorkPhysT + $LpWorkPhysB) / 2
$LpInScreen = ($LpWinL -ge $LpSecPhysL) -and ($LpWinT -ge $LpSecPhysT) -and `
    ($LpWinL + $LpWinW -le $LpSecPhysL + $LpSecPhysW) -and ($LpWinT + $LpWinH -le $LpSecPhysT + $LpSecPhysH)

[pscustomobject]@{
    Screen           = $LpSec.DeviceName
    Scale            = $LpScaleD
    WindowPhysPx     = ('L={0} T={1} {2}x{3}' -f $LpWinL, $LpWinT, $LpWinW, $LpWinH)
    ExpectWindowPx   = ('{0}x{1}' -f $LpExpectW, $LpExpectH)
    SizeMatch        = ($LpWinW -eq $LpExpectW -and $LpWinH -eq $LpExpectH)
    WorkPhys         = ('L={0} T={1} R={2} B={3}' -f $LpWorkPhysL, $LpWorkPhysT, $LpWorkPhysR, $LpWorkPhysB)
    WindowCenter     = ('{0},{1}' -f $LpWinCx, $LpWinCy)
    WorkCenter       = ('{0},{1}' -f $LpWorkCx, $LpWorkCy)
    CenterOffsetPx   = ('dX={0} dY={1}' -f ($LpWinCx - $LpWorkCx), ($LpWinCy - $LpWorkCy))
    FullyOnSecondary = $LpInScreen
    MinGlyph         = $LpMetrics.min
    CloseGlyph       = $LpMetrics.close
    Shots            = (Split-Path $LpFullShot -Leaf) + ' / ' + (Split-Path $LpZoomShot -Leaf)
} | Format-List | Out-String -Width 200 | Write-Host
Write-Host '完成'
