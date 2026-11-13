#Requires -Version 7.0
<#
.SYNOPSIS
  单发实拍：在「当前已切好的系统缩放」下，拉起设置窗、抓物理像素、量标题栏按钮笔画。
  用户手动切好缩放后调用一次；探针读数与期望不符则直接中止，不做任何拍摄。
.EXAMPLE
  pwsh -NoProfile -File tools\Test-LpShotOnce.ps1 -LpScale 1.0 -LpNum 39 -LpTag 100
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][double]$LpScale,   # 期望缩放（1.0/1.25/1.5/2.0），探针闸门
    [Parameter(Mandatory)][int]$LpNum,        # 截图编号（39/40/41/42）
    [Parameter(Mandatory)][string]$LpTag      # 缩放标签（100/125/150/200）
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
New-Item -ItemType Directory -Force -Path $LpShotsDir, $LpWork | Out-Null

# ---------- 原生：EnumWindows 找窗（FindWindowW 在本会话被 hook，恒返 0，弃用） ----------
Add-Type -Namespace 'LpShotNative' -Name 'Win32' -MemberDefinition @'
    public delegate bool EnumProc(System.IntPtr hWnd, System.IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool EnumWindows(LpShotNative.Win32.EnumProc cb, System.IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsWindowVisible(System.IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int GetWindowTextW(System.IntPtr hWnd, System.Text.StringBuilder sb, int n);
'@

function Find-LpWindowByTitle {
    param([string]$LpTitle)
    $script:LpFoundHwnd = [IntPtr]::Zero
    $LpProc = [LpShotNative.Win32+EnumProc]{
        param($LpH, $LpL)
        if (-not [LpShotNative.Win32]::IsWindowVisible($LpH)) { return $true }
        $LpSb = New-Object System.Text.StringBuilder 512
        [void][LpShotNative.Win32]::GetWindowTextW($LpH, $LpSb, $LpSb.Capacity)
        if ($LpSb.ToString() -eq $LpTitle) { $script:LpFoundHwnd = $LpH; return $false }
        return $true
    }
    [LpShotNative.Win32]::EnumWindows($LpProc, [IntPtr]::Zero) | Out-Null
    return $script:LpFoundHwnd
}

# ---------- 抓取助手（独立 exe，PerMonitorV2，物理像素；已编译则复用） ----------
$LpCaptureExe = Join-Path $LpWork 'CaptureLp.exe'
if (-not (Test-Path -LiteralPath $LpCaptureExe)) {
    $LpCaptureCs = Join-Path $LpWork 'CaptureLp.cs'
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
        SetProcessDpiAwarenessContext(new IntPtr(-4));
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
        using (var img = Image.FromHbitmap(bmp)) { img.Save(outPath, ImageFormat.Png); }
        DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        Console.WriteLine(r.Left + " " + r.Top + " " + w + " " + h);
        return 0;
    }
}
'@ | Set-Content -LiteralPath $LpCaptureCs -Encoding UTF8
    & $LpCsc /nologo /target:exe /out:$LpCaptureExe /r:System.Drawing.dll $LpCaptureCs | Out-Null
    if (-not (Test-Path -LiteralPath $LpCaptureExe)) { throw '抓取助手编译失败' }
}

# ---------- 量笔画 / 4× 放大 ----------
function Get-LpGlyphMetrics {
    param([System.Drawing.Bitmap]$LpBmp, [double]$LpScale)
    $LpW = $LpBmp.Width
    $LpStripH = [int][Math]::Ceiling(37 * $LpScale)
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

    # Y 扫描带：字形中心在 26 DIP（按钮 40 DIP 居中 20 DIP + 内容顶部 ~6 DIP 隐形边框内缩）。
    # 带取 [10, 31] DIP × scale：避开右上圆角弧线（<10 DIP）与底部分隔线（~33 DIP），各档均适用。
    $LpBandY0 = [int][Math]::Ceiling(10 * $LpScale)
    $LpBandY1 = [Math]::Min($LpStripH - 1, [int][Math]::Ceiling(31 * $LpScale))

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
        return [pscustomobject]@{ X0 = $LpMinX; Y0 = $LpMinY; X1 = $LpMaxX; Y1 = $LpMaxY; Count = $LpCount }
    }

    $LpZones = [ordered]@{
        # 注意：逗号优先级高于减号，元素必须各自加括号，否则解析成 int-数组 直接报错
        min   = @(($LpW - [int][Math]::Ceiling(92 * $LpScale)), ($LpW - [int][Math]::Ceiling(48 * $LpScale)))
        close = @(($LpW - [int][Math]::Ceiling(46 * $LpScale)), ($LpW - 2))
    }
    $LpOut = @{}
    foreach ($LpName in $LpZones.Keys) {
        $LpTight = Get-LpMaskBox $LpZones[$LpName][0] $LpZones[$LpName][1] 60
        $LpLoose = Get-LpMaskBox $LpZones[$LpName][0] $LpZones[$LpName][1] 25
        $LpEntry = [ordered]@{ tight = $null; loose = $null; stroke = $null }
        if ($LpTight) {
            $LpEntry.tight = '{0}x{1}' -f ($LpTight.X1 - $LpTight.X0 + 1), ($LpTight.Y1 - $LpTight.Y0 + 1)
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

# ---------- 主流程：探针闸门 → 拉窗 → 抓 → 量 → 杀 ----------
$LpFile = Join-Path $LpWork ("probe-{0}.json" -f (Get-Date -Format 'HHmmssfff'))
& $LpExe "--layout-probe=$LpFile" | Out-Null
$LpJson = Get-Content -LiteralPath $LpFile -Raw | ConvertFrom-Json
Remove-Item -LiteralPath $LpFile -Force
$LpProbed = [double]$LpJson.realScale
Write-Host ("探针 realScale={0}，期望 {1}" -f $LpProbed, $LpScale)
if ([Math]::Abs($LpProbed - $LpScale) -gt 0.02) {
    throw ("当前缩放 {0} 与期望 {1} 不符，未拍摄" -f $LpProbed, $LpScale)
}

Get-Process | Where-Object { $_.ProcessName -eq 'Pathleap.UI' } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
$LpProc = Start-Process -FilePath $LpExe -ArgumentList '--screen=settings' -PassThru
$LpHwnd = [IntPtr]::Zero
$LpDeadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $LpDeadline) {
    $LpHwnd = Find-LpWindowByTitle -LpTitle $LpWindowTitle
    if ($LpHwnd -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 400
}
if ($LpHwnd -eq [IntPtr]::Zero) {
    Get-Process -Id $LpProc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
    throw "等不到设置窗"
}
Start-Sleep -Milliseconds 2500

$LpFullShot = Join-Path $LpShotsDir ("{0}-scale{1}-settings.png" -f $LpNum, $LpTag)
$LpZoomShot = Join-Path $LpShotsDir ("{0}-scale{1}-caption.png" -f $LpNum, $LpTag)
$LpRaw = Join-Path $LpWork ("raw-{0}.png" -f $LpTag)
$LpBounds = & $LpCaptureExe ([int64]$LpHwnd) $LpRaw
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $LpRaw)) { throw "抓取失败（exit=$LASTEXITCODE）" }
$LpBoundsParts = $LpBounds.Trim() -split ' '
$LpBmp = [System.Drawing.Bitmap]::FromFile($LpRaw)
try {
    $LpMetrics = Get-LpGlyphMetrics -LpBmp $LpBmp -LpScale $LpScale
    Save-LpCaptionZoom -LpBmp $LpBmp -LpScale $LpScale -LpPath $LpZoomShot
}
catch {
    Write-Host $_.ScriptStackTrace
    Write-Host $_.InvocationInfo.PositionMessage
    throw
}
finally { $LpBmp.Dispose() }
Copy-Item -LiteralPath $LpRaw -Destination $LpFullShot -Force

Get-Process -Id $LpProc.Id -ErrorAction SilentlyContinue | Stop-Process -Force

[pscustomobject]@{
    Scale       = $LpScale
    WindowPx    = ('{0}x{1}' -f $LpBoundsParts[2], $LpBoundsParts[3])
    ExpectPx    = ('{0}x{1}' -f [Math]::Round(840 * $LpScale), [Math]::Round(640 * $LpScale))
    MinTight    = $LpMetrics.min.tight
    MinLoose    = $LpMetrics.min.loose
    MinStroke   = $LpMetrics.min.stroke
    CloseTight  = $LpMetrics.close.tight
    CloseLoose  = $LpMetrics.close.loose
    CloseStroke = $LpMetrics.close.stroke
    Shots       = ('{0} / {1}' -f (Split-Path $LpFullShot -Leaf), (Split-Path $LpZoomShot -Leaf))
} | Format-List | Out-String -Width 200 | Write-Host
Write-Host '完成'
