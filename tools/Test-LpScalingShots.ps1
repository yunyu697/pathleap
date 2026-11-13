#Requires -Version 7.0
<#
.SYNOPSIS
  100%/125%/150% 缩放下标题栏按钮笔画实拍（附 200% 对照）。
.DESCRIPTION
  1. 缩放切换走 Set-LpScaleViaSettings.ps1（UIA 驱动系统设置的缩放下拉框，与人工切换同路径）。
     实测本机（Win11 26300）注册表 DpiValue + WM_SETTINGCHANGE 广播不即时生效，已弃用该机制。
     每档都用 dev 构建的 --layout-probe 读 GetDpiForMonitor 实测值作为「即时生效」闸门，
     探针与目标不符则中止（finally 会切回 200% 并验证），不做任何实拍结论。
  2. 生效时：--screen=settings 拉起设置窗（自绘最小化/关闭按钮），用带 PerMonitorV2 声明的
     抓取助手取窗口物理像素，量最小化横线与关闭叉形的包围盒与笔画，存全窗与标题栏 4× 放大图。
  3. 结束（finally）必还原 200% 并用探针验证。
  注意：最大化字形在现行 UI 中不渲染（固定尺寸窗），实拍覆盖的是最小化/关闭两个字形。
.EXAMPLE
  pwsh -NoProfile -File tools\Test-LpScalingShots.ps1
#>
[CmdletBinding()]
param(
    [double[]]$LpTargets = @(1.0, 1.25, 1.5, 2.0),
    [string]$LpShotPrefix = '39',
    [switch]$LpManual
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$LpRepo = Split-Path -Parent $PSScriptRoot
$LpExe = Join-Path $LpRepo 'src\Pathleap.UI\bin\x64\Debug\net8.0-windows10.0.22621.0\Pathleap.UI.exe'
$LpShotsDir = Join-Path $LpRepo 'docs\shots'
$LpWork = Join-Path $env:TEMP 'pathleap-scaling'
$LpCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$LpWindowTitle = '设置 · Pathleap'
$LpShotNumbers = @{ 1.0 = 39; 1.25 = 40; 1.5 = 41; 2.0 = 42 }
$LpScaleTag = @{ 1.0 = '100'; 1.25 = '125'; 1.5 = '150'; 2.0 = '200' }

if (-not (Test-Path -LiteralPath $LpExe)) { throw "dev 构建不存在: $LpExe（先跑 tools\Build-Lp.ps1）" }
if (-not (Test-Path -LiteralPath $LpCsc)) { throw "缺少 C# 编译器: $LpCsc" }
New-Item -ItemType Directory -Force -Path $LpWork | Out-Null
New-Item -ItemType Directory -Force -Path $LpShotsDir | Out-Null

# ---------- 原生调用（PS 侧：EnumWindows 找窗、取 pid） ----------
# 注：当前会话里 FindWindowW（经由 Add-Type DllImport）全部返回 0x0，
# 已确认被某个注入层 hook 掉，所以改用 EnumWindows（实测仍可靠）。
Add-Type -Namespace 'LpScaleNative' -Name 'Win32' -MemberDefinition @'
    public delegate bool EnumProc(System.IntPtr hWnd, System.IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool EnumWindows(LpScaleNative.Win32.EnumProc cb, System.IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint pid);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsWindowVisible(System.IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int GetWindowTextW(System.IntPtr hWnd, System.Text.StringBuilder sb, int n);
'@

function Find-LpWindowByTitle {
    param([string]$LpTitle, [switch]$LpRequireVisible)
    $LpHwnd = [IntPtr]::Zero
    $LpProc = [LpScaleNative.Win32+EnumProc]{
        param($LpH, $LpL)
        if ($LpRequireVisible -and -not [LpScaleNative.Win32]::IsWindowVisible($LpH)) { return $true }
        $LpSb = New-Object System.Text.StringBuilder 512
        [void][LpScaleNative.Win32]::GetWindowTextW($LpH, $LpSb, $LpSb.Capacity)
        $LpTitleGot = $LpSb.ToString()
        if ($LpTitleGot -eq $LpTitle) { $script:LpHwnd = $LpH; return $false }
        return $true
    }
    [LpScaleNative.Win32]::EnumWindows($LpProc, [IntPtr]::Zero) | Out-Null
    return $LpHwnd
}



# ---------- DPI 感知抓取助手（独立进程，PerMonitorV2，坐标即物理像素） ----------
$LpCaptureCs = Join-Path $LpWork 'CaptureLp.cs'
$LpCaptureExe = Join-Path $LpWork 'CaptureLp.exe'
@'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

class CaptureLp
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dc, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);

    struct RECT { public int Left, Top, Right, Bottom; }

    static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // PerMonitorV2
        IntPtr hwnd = new IntPtr(long.Parse(args[0]));
        string outPath = args[1];
        RECT r;
        if (DwmGetWindowAttribute(hwnd, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0)
        {
            if (!GetWindowRect(hwnd, out r)) { Console.Error.WriteLine("no rect"); return 1; }
        }
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) { Console.Error.WriteLine("bad rect"); return 2; }
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        SelectObject(mem, bmp);
        const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
        BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, SRCCOPY | CAPTUREBLT);
        using (var img = Image.FromHbitmap(bmp))
        {
            img.Save(outPath, ImageFormat.Png);
        }
        DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        Console.WriteLine(r.Left + " " + r.Top + " " + w + " " + h);
        return 0;
    }
}
'@ | Set-Content -LiteralPath $LpCaptureCs -Encoding UTF8

& $LpCsc /nologo /target:exe /out:$LpCaptureExe /r:System.Drawing.dll $LpCaptureCs | Out-Null
if (-not (Test-Path -LiteralPath $LpCaptureExe)) { throw '抓取助手编译失败' }

# ---------- 探针：读真实每屏有效缩放 ----------
function Invoke-LpProbe {
    $LpFile = Join-Path $LpWork ("probe-{0}.json" -f (Get-Date -Format 'HHmmssfff'))
    & $LpExe "--layout-probe=$LpFile" | Out-Null
    if (-not (Test-Path -LiteralPath $LpFile)) { throw '探针未写出结果' }
    $LpJson = Get-Content -LiteralPath $LpFile -Raw | ConvertFrom-Json
    Remove-Item -LiteralPath $LpFile -Force
    return [pscustomobject]@{ Scale = [double]$LpJson.realScale; Work = $LpJson.realWork }
}



$LpResults = [System.Collections.Generic.List[object]]::new()
$LpStartedPids = [System.Collections.Generic.List[uint32]]::new()

# 缩放切换机制：注册表 DpiValue + WM_SETTINGCHANGE 广播在本机实测不即时生效（探针恒为 2.0）。
# UIA 驱动系统设置的缩放下拉框曾在 125%/200% 间切换成功过，但 ExpandCollapsePattern.Expand() 在 Win11 26300
# 上经常异常（返回 0x80070057 类错误），设置应用偶尔变空壳后需重启。
# 当 -LpManual 传入或 UIA 失败时，脚本会提示用户手动切换，等回车后再探针验证——最可靠的路径。
$LpHelper = Join-Path $PSScriptRoot 'Set-LpScaleViaSettings.ps1'

function Set-LpScaleViaUi {
    param([int]$LpPercent, [switch]$LpSkipManualFallback)
    if ($LpManual) {
        Write-Host ''
        Write-Host ('>>> 请手动把系统缩放切到 {0}%' -f $LpPercent) -ForegroundColor Yellow
        Start-Process 'ms-settings:display' -ErrorAction SilentlyContinue
        Write-Host '    （设置 → 显示 → 缩放与布局，切换后脚本自动检测到继续；无超时）' -ForegroundColor DarkGray
        $LpTargetScale = $LpPercent / 100.0
        $LpLastRead = $null
        while ($true) {
            $LpNow = (Invoke-LpProbe).Scale
            if ([Math]::Abs($LpNow - $LpTargetScale) -lt 0.02) {
                Write-Host ("    ✓ 已检测到缩放 {0} → 继续" -f $LpNow) -ForegroundColor Green
                return
            }
            if ($LpNow -ne $LpLastRead) {
                Write-Host ("    … 当前 realScale={0}，目标 {1}" -f $LpNow, $LpTargetScale)
                $LpLastRead = $LpNow
            }
            Start-Sleep -Seconds 3
        }
    }
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $LpHelper -LpPercent $LpPercent
    if ($LASTEXITCODE -ne 0) {
        if ($LpSkipManualFallback) {
            throw ("UIA 切换缩放 {0}% 失败（exit={1}）" -f $LpPercent, $LASTEXITCODE)
        }
        Write-Host ("UIA 切换缩放 {0}% 失败（exit={1}），fallback 到手动" -f $LpPercent, $LASTEXITCODE) -ForegroundColor Yellow
        Write-Host ('>>> 请手动把系统缩放切到 {0}%，然后回到这里按回车继续' -f $LpPercent) -ForegroundColor Yellow
        Read-Host "已切好 $LpPercent% → 回车" | Out-Null
    }
}

try {
# ---------- 阶段一：缩放即时生效闸门（切 100% + 探针验证） ----------
Write-Host '== 阶段一：缩放即时生效闸门 ==' -ForegroundColor Cyan
if ($LpManual) {
    Write-Host 'Manual 模式：默认相信用户手动切换即时生效，闸门简化为直接验证第一个目标缩放'
}
$LpBase = Invoke-LpProbe
Write-Host ("基线 realScale={0}" -f $LpBase.Scale)

if (-not $LpManual) {
    Set-LpScaleViaUi -LpPercent 100 -LpSkipManualFallback
} elseif ([Math]::Abs($LpBase.Scale - $LpTargets[0]) -gt 0.02) {
    # Manual 模式：如果当前缩放不是第一个目标，先切过去
    Set-LpScaleViaUi -LpPercent ([int]($LpTargets[0] * 100))
}
Start-Sleep -Milliseconds 1500
$LpGate = (Invoke-LpProbe).Scale
Write-Host ("闸门：当前 realScale={0}，首个目标是 {1}" -f $LpGate, $LpTargets[0])
if ([Math]::Abs($LpGate - $LpTargets[0]) -gt 0.02) {
    if ($LpManual) {
        throw ('闸门未过：手动切换后探针读到 {0}，首个目标是 {1}' -f $LpGate, $LpTargets[0])
    }
    throw ('闸门未过：切换后探针读到 {0}，首个目标是 {1}（finally 会还原 200%）' -f $LpGate, $LpTargets[0])
}

# ---------- 阶段二：逐缩放实拍 ----------
function Get-LpGlyphMetrics {
    param([System.Drawing.Bitmap]$LpBmp, [double]$LpScale)
    $LpW = $LpBmp.Width
    $LpStripH = [int][Math]::Ceiling(37 * $LpScale)
    # 背景色取标题栏条带中位数（按 8 量化）
    $LpHist = @{}
    for ($LpX = $LpW - [int][Math]::Ceiling(150 * $LpScale); $LpX -lt $LpW; $LpX += 3) {
        for ($LpY = 2; $LpY -lt $LpStripH; $LpY += 3) {
            $LpC = $LpBmp.GetPixel($LpX, $LpY)
            $LpK = ('{0}_{1}_{2}' -f [int]($LpC.R / 8), [int]($LpC.G / 8), [int]($LpC.B / 8))
            $LpHist[$LpK] = 1 + $LpHist[$LpK]
        }
    }
    $LpTopKey = $LpHist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1
    $LpParts = $LpTopKey.Key.Split('_')
    $LpBgR = [int]$LpParts[0] * 8 + 4; $LpBgG = [int]$LpParts[1] * 8 + 4; $LpBgB = [int]$LpParts[2] * 8 + 4

    function Get-LpMaskBox {
        param([int]$LpX0, [int]$LpX1, [int]$LpThreshold)
        $LpMinX = [int]::MaxValue; $LpMinY = [int]::MaxValue; $LpMaxX = -1; $LpMaxY = -1; $LpCount = 0
        for ($LpX = $LpX0; $LpX -le $LpX1; $LpX++) {
            for ($LpY = 0; $LpY -lt $LpStripH; $LpY++) {
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
        return [pscustomobject]@{ X0 = $LpMinX; Y0 = $LpMinY; X1 = $LpMaxX; Y1 = $LpMaxY; Count = $LpCount }
    }

    $LpZones = [ordered]@{
        min   = @($LpW - [int][Math]::Ceiling(92 * $LpScale), $LpW - [int][Math]::Ceiling(48 * $LpScale))
        close = @($LpW - [int][Math]::Ceiling(46 * $LpScale), $LpW - 2)
    }
    $LpOut = @{}
    foreach ($LpName in $LpZones.Keys) {
        $LpTight = Get-LpMaskBox $LpZones[$LpName][0] $LpZones[$LpName][1] 60
        $LpLoose = Get-LpMaskBox $LpZones[$LpName][0] $LpZones[$LpName][1] 25
        $LpEntry = [ordered]@{ tight = $null; loose = $null; stroke = $null }
        if ($LpTight) {
            $LpEntry.tight = '{0}x{1}' -f ($LpTight.X1 - $LpTight.X0 + 1), ($LpTight.Y1 - $LpTight.Y0 + 1)
            # 笔画：最小化取包围盒中心列的纵向连续段；关闭取包围盒顶行的横向段（对角线，斜宽≈段宽×cos45）
            if ($LpName -eq 'min') {
                $LpCx = [int](($LpTight.X0 + $LpTight.X1) / 2); $LpRun = 0; $LpBestRun = 0
                for ($LpY = $LpTight.Y0; $LpY -le $LpTight.Y1; $LpY++) {
                    $LpC = $LpBmp.GetPixel($LpCx, $LpY)
                    $LpD = [Math]::Max([Math]::Abs($LpC.R - $LpBgR), [Math]::Max([Math]::Abs($LpC.G - $LpBgG), [Math]::Abs($LpC.B - $LpBgB)))
                    if ($LpD -gt 60) { $LpRun++; if ($LpRun -gt $LpBestRun) { $LpBestRun = $LpRun } } else { $LpRun = 0 }
                }
                $LpEntry.stroke = "横线厚 ${LpBestRun}px"
            }
            else {
                $LpRowRuns = [System.Collections.Generic.List[int]]::new()
                for ($LpY = $LpTight.Y0; $LpY -le $LpTight.Y0 + 2 -and $LpY -le $LpTight.Y1; $LpY++) {
                    $LpRun = 0; $LpBestRun = 0
                    for ($LpX = $LpTight.X0; $LpX -le $LpTight.X1; $LpX++) {
                        $LpC = $LpBmp.GetPixel($LpX, $LpY)
                        $LpD = [Math]::Max([Math]::Abs($LpC.R - $LpBgR), [Math]::Max([Math]::Abs($LpC.G - $LpBgG), [Math]::Abs($LpC.B - $LpBgB)))
                        if ($LpD -gt 60) { $LpRun++; if ($LpRun -gt $LpBestRun) { $LpBestRun = $LpRun } } else { $LpRun = 0 }
                    }
                    $LpRowRuns.Add($LpBestRun)
                }
                $LpMaxRowRun = ($LpRowRuns | Measure-Object -Maximum).Maximum
                $LpEntry.stroke = ('叉臂顶行段宽 {0}px ≈斜厚 {1}px' -f $LpMaxRowRun, [Math]::Round($LpMaxRowRun * 0.707, 1))
            }
        }
        if ($LpLoose) {
            $LpEntry.loose = '{0}x{1}' -f ($LpLoose.X1 - $LpLoose.X0 + 1), ($LpLoose.Y1 - $LpLoose.Y0 + 1)
        }
        $LpOut[$LpName] = $LpEntry
    }
    return [pscustomobject]$LpOut
}

function Save-LpCaptionZoom {
    param([System.Drawing.Bitmap]$LpBmp, [double]$LpScale, [string]$LpPath)
    $LpCropW = [int][Math]::Min($LpBmp.Width, [Math]::Ceiling(150 * $LpScale))
    $LpCropH = [int][Math]::Ceiling(42 * $LpScale)
    $LpZoom = 4
    $LpOut = New-Object System.Drawing.Bitmap ($LpCropW * $LpZoom), ($LpCropH * $LpZoom)
    $LpG = [System.Drawing.Graphics]::FromImage($LpOut)
    $LpG.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $LpG.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $LpG.DrawImage($LpBmp, (New-Object System.Drawing.Rectangle 0, 0, ($LpCropW * $LpZoom), ($LpCropH * $LpZoom)),
        (New-Object System.Drawing.Rectangle ($LpBmp.Width - $LpCropW), 0, $LpCropW, $LpCropH),
        [System.Drawing.GraphicsUnit]::Pixel)
    $LpG.Dispose()
    $LpOut.Save($LpPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $LpOut.Dispose()
}

    Write-Host ''
    Write-Host '== 阶段二：逐缩放实拍 ==' -ForegroundColor Cyan
    foreach ($LpTarget in $LpTargets) {
        if ($LpTarget -ne $LpTargets[0]) {
            Set-LpScaleViaUi -LpPercent ([int]($LpTarget * 100))
            Start-Sleep -Milliseconds 1500
        }
        $LpProbed = (Invoke-LpProbe).Scale
        if ([Math]::Abs($LpProbed - $LpTarget) -gt 0.02) {
            throw ("缩放 {0}% 应用后探针读到 {1}，与目标不符，中止（finally 会还原）" -f ($LpTarget * 100), $LpProbed)
        }

        $LpProc = Start-Process -FilePath $LpExe -ArgumentList '--screen=settings' -PassThru
        $LpStartedPids.Add([uint32]$LpProc.Id)
        $LpHwnd = [IntPtr]::Zero
        $LpDeadline = (Get-Date).AddSeconds(25)
        while ((Get-Date) -lt $LpDeadline) {
            $LpHwnd = Find-LpWindowByTitle -LpTitle $LpWindowTitle -LpRequireVisible
            if ($LpHwnd -ne [IntPtr]::Zero) { break }
            Start-Sleep -Milliseconds 400
        }
        if ($LpHwnd -eq [IntPtr]::Zero) { throw ("{0}%：等不到设置窗" -f ($LpTarget * 100)) }
        Start-Sleep -Milliseconds 2500

        $LpTag = $LpScaleTag[$LpTarget]
        $LpNum = $LpShotNumbers[$LpTarget]
        $LpFullShot = Join-Path $LpShotsDir ("{0}-scale{1}-settings.png" -f $LpNum, $LpTag)
        $LpZoomShot = Join-Path $LpShotsDir ("{0}-scale{1}-caption.png" -f $LpNum, $LpTag)
        $LpRaw = Join-Path $LpWork ("raw-{0}.png" -f $LpTag)

        $LpBoundsLine = & $LpCaptureExe ([int64]$LpHwnd) $LpRaw
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $LpRaw)) {
            throw ("{0}%：抓取助手失败（exit={1}）" -f ($LpTarget * 100), $LASTEXITCODE)
        }
        $LpParts = $LpBoundsLine.Trim() -split ' '
        $LpBmp = [System.Drawing.Bitmap]::FromFile($LpRaw)
        try {
            $LpMetrics = Get-LpGlyphMetrics -LpBmp $LpBmp -LpScale $LpTarget
            Save-LpCaptionZoom -LpBmp $LpBmp -LpScale $LpTarget -LpPath $LpZoomShot
        }
        finally { $LpBmp.Dispose() }
        Copy-Item -LiteralPath $LpRaw -Destination $LpFullShot -Force

        $LpRow = [pscustomobject]@{
            Scale      = $LpTarget
            Status     = 'OK'
            WindowPx   = ('{0}x{1}' -f $LpParts[2], $LpParts[3])
            ExpectPx   = ('{0}x{1}' -f [Math]::Round(840 * $LpTarget), [Math]::Round(640 * $LpTarget))
            MinTight   = $LpMetrics.min.tight
            MinLoose   = $LpMetrics.min.loose
            MinStroke  = $LpMetrics.min.stroke
            CloseTight = $LpMetrics.close.tight
            CloseLoose = $LpMetrics.close.loose
            CloseStroke = $LpMetrics.close.stroke
            Shots      = ('{0} / {1}' -f (Split-Path $LpFullShot -Leaf), (Split-Path $LpZoomShot -Leaf))
        }
        $LpResults.Add($LpRow)
        Write-Host ("{0}% 完成：窗口 {1}（期望 {2}） 最小化 {3} 笔画[{4}] 关闭 {5} 笔画[{6}]" -f `
            ($LpTarget * 100), $LpRow.WindowPx, $LpRow.ExpectPx, $LpRow.MinTight, $LpRow.MinStroke, $LpRow.CloseTight, $LpRow.CloseStroke) -ForegroundColor Green

        Get-Process -Id $LpProc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 800
    }
}
finally {
    Write-Host ''
    Write-Host '== 还原 200% 并验证 ==' -ForegroundColor Cyan
    try { Set-LpScaleViaUi -LpPercent 200 }
    catch { Write-Host ("UIA 还原失败：{0}，请手动把缩放切回 200%！" -f $_.Exception.Message) -ForegroundColor Red }
    Start-Sleep -Milliseconds 1500
    $LpFinalScale = (Invoke-LpProbe).Scale
    Write-Host ("还原后探针 realScale={0}" -f $LpFinalScale)
    if ([Math]::Abs($LpFinalScale - 2.0) -gt 0.02) {
        Write-Host '警告：还原后探针未读到 200%，请手动核对系统缩放设置！' -ForegroundColor Red
    }
    foreach ($LpPid in $LpStartedPids) {
        Get-Process -Id $LpPid -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}

Write-Host ''
$LpResults | Format-List | Out-String -Width 200 | Write-Host
$LpResults | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $LpWork 'scaling-results.json') -Encoding UTF8
Write-Host '明细已存 %TEMP%\pathleap-scaling\scaling-results.json'
