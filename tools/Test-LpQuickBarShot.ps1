#Requires -Version 7.0
<#
.SYNOPSIS
  快速切换栏实拍：真实拉起 --screen=quick，可选真实敲入搜索词，抓物理像素并扫描边缘像素。
.EXAMPLE
  pwsh -NoProfile -File tools\Test-LpQuickBarShot.ps1 -LpNum 48 -LpTag recent
  pwsh -NoProfile -File tools\Test-LpQuickBarShot.ps1 -LpNum 49 -LpTag search -LpType path
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$LpNum,
    [Parameter(Mandatory)][string]$LpTag,
    [string]$LpType = '',
    [switch]$LpFocus,
    [switch]$LpSkipGuide
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$LpRepo = Split-Path -Parent $PSScriptRoot
$LpExe = Join-Path $LpRepo 'src\Pathleap.UI\bin\x64\Debug\net8.0-windows10.0.22621.0\Pathleap.UI.exe'
$LpShotsDir = Join-Path $LpRepo 'docs\shots'
$LpWork = Join-Path $env:TEMP 'pathleap-scaling'
$LpCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$LpWindowTitle = 'Pathleap 快速切换'
$LpLogDir = Join-Path $env:LOCALAPPDATA 'Pathleap\logs'
New-Item -ItemType Directory -Force -Path $LpShotsDir, $LpWork | Out-Null

Add-Type -Namespace 'LpQuickNative' -Name 'Win32' -MemberDefinition @'
    public delegate bool EnumProc(System.IntPtr hWnd, System.IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool EnumWindows(LpQuickNative.Win32.EnumProc cb, System.IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsWindowVisible(System.IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int GetWindowTextW(System.IntPtr hWnd, System.Text.StringBuilder sb, int n);
'@

function Find-LpWindowByTitle {
    param([string]$LpTitle)
    $script:LpFoundHwnd = [IntPtr]::Zero
    $LpProc = [LpQuickNative.Win32+EnumProc]{
        param($LpH, $LpL)
        if (-not [LpQuickNative.Win32]::IsWindowVisible($LpH)) { return $true }
        $LpSb = New-Object System.Text.StringBuilder 512
        [void][LpQuickNative.Win32]::GetWindowTextW($LpH, $LpSb, $LpSb.Capacity)
        if ($LpSb.ToString() -eq $LpTitle) { $script:LpFoundHwnd = $LpH; return $false }
        return $true
    }
    [LpQuickNative.Win32]::EnumWindows($LpProc, [IntPtr]::Zero) | Out-Null
    return $script:LpFoundHwnd
}

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

# ---------- 边缘逐像素扫描：水平中线左侧 12px + 垂直中线顶部 12px ----------
function Get-LpEdgeScan {
    param([System.Drawing.Bitmap]$LpBmp)
    $LpMidY = [int]($LpBmp.Height / 2)
    $LpMidX = [int]($LpBmp.Width / 2)
    $LpLeft = for ($LpX = 0; $LpX -lt [Math]::Min(12, $LpBmp.Width); $LpX++) {
        $LpC = $LpBmp.GetPixel($LpX, $LpMidY)
        '{0}:#{1:X2}{2:X2}{3:X2}' -f $LpX, $LpC.R, $LpC.G, $LpC.B
    }
    $LpTop = for ($LpY = 0; $LpY -lt [Math]::Min(12, $LpBmp.Height); $LpY++) {
        $LpC = $LpBmp.GetPixel($LpMidX, $LpY)
        '{0}:#{1:X2}{2:X2}{3:X2}' -f $LpY, $LpC.R, $LpC.G, $LpC.B
    }
    return [pscustomobject]@{ Left = ($LpLeft -join ' '); Top = ($LpTop -join ' ') }
}

function Save-LpCornerZoom {
    param([System.Drawing.Bitmap]$LpBmp, [string]$LpPath)
    $LpCropW = [Math]::Min(48, $LpBmp.Width)
    $LpCropH = [Math]::Min(40, $LpBmp.Height)
    $LpZoom = 8
    $LpOut = New-Object System.Drawing.Bitmap ($LpCropW * $LpZoom), ($LpCropH * $LpZoom)
    $LpG = [System.Drawing.Graphics]::FromImage($LpOut)
    $LpG.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $LpG.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $LpG.DrawImage($LpBmp, (New-Object System.Drawing.Rectangle 0, 0, ($LpCropW * $LpZoom), ($LpCropH * $LpZoom)),
        (New-Object System.Drawing.Rectangle 0, 0, $LpCropW, $LpCropH),
        [System.Drawing.GraphicsUnit]::Pixel)
    $LpG.Dispose()
    $LpOut.Save($LpPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $LpOut.Dispose()
}

# ---------- 主流程 ----------
Get-Process | Where-Object { $_.ProcessName -eq 'Pathleap.UI' } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700

$LpArgs = @('--screen=quick')
if ($LpType.Length -gt 0) { $LpArgs += "--quick-type=$LpType" }
if ($LpFocus) { $LpArgs += '--quick-focus' }
if ($LpSkipGuide) { $LpArgs += '--quick-skip-guide' }
$LpProc = Start-Process -FilePath $LpExe -ArgumentList $LpArgs -PassThru

$LpHwnd = [IntPtr]::Zero
$LpDeadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $LpDeadline) {
    $LpHwnd = Find-LpWindowByTitle -LpTitle $LpWindowTitle
    if ($LpHwnd -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 300
}
if ($LpHwnd -eq [IntPtr]::Zero) {
    Get-Process -Id $LpProc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
    throw '等不到快速切换栏窗口'
}

if ($LpType.Length -gt 0) {
    $LpLog = Get-ChildItem -LiteralPath $LpLogDir -Filter 'ui-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $LpSettled = $false
    $LpSearchDeadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $LpSearchDeadline) {
        if ($LpLog) {
            $LpTail = Get-Content -LiteralPath $LpLog.FullName -Tail 60 -ErrorAction SilentlyContinue
            if ($LpTail -match '快速切换搜索结果') { $LpSettled = $true; break }
        }
        Start-Sleep -Milliseconds 300
    }
    if (-not $LpSettled) { Write-Warning '未在日志里等到搜索结果行，仍按当前画面抓取' }
}

# 窗口句柄出现时 XAML 首帧往往还没合成，必须等日志里的“已打开”并把首帧渲染时间留足。
$LpLog = Get-ChildItem -LiteralPath $LpLogDir -Filter 'ui-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($LpLog) {
    $LpOpenDeadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $LpOpenDeadline) {
        if ((Get-Content -LiteralPath $LpLog.FullName -Tail 80 -ErrorAction SilentlyContinue) -match '快速切换栏已(以预览形态)?打开') {
            break
        }
        Start-Sleep -Milliseconds 250
    }
}

Start-Sleep -Milliseconds 2600
$LpRaw = Join-Path $LpWork ("quick-raw-{0}.png" -f $LpTag)
$LpBounds = & $LpCaptureExe ([int64]$LpHwnd) $LpRaw
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $LpRaw)) {
    Get-Process -Id $LpProc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
    throw "抓取失败（exit=$LASTEXITCODE）"
}

$LpShot = Join-Path $LpShotsDir ("{0}-quickbar-{1}.png" -f $LpNum, $LpTag)
$LpZoom = Join-Path $LpShotsDir ("{0}-quickbar-{1}-corner.png" -f $LpNum, $LpTag)
Copy-Item -LiteralPath $LpRaw -Destination $LpShot -Force

$LpBmp = [System.Drawing.Bitmap]::FromFile($LpRaw)
try {
    $LpScan = Get-LpEdgeScan -LpBmp $LpBmp
    Save-LpCornerZoom -LpBmp $LpBmp -LpPath $LpZoom
}
finally { $LpBmp.Dispose() }

$LpLogLine = ''
$LpLog = Get-ChildItem -LiteralPath $LpLogDir -Filter 'ui-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($LpLog) {
    $LpLogLine = (Get-Content -LiteralPath $LpLog.FullName -Tail 200 -ErrorAction SilentlyContinue |
        Where-Object { $_ -match '快速切换搜索结果|快速切换栏测试键入' } |
        Select-Object -Last 2) -join ' | '
}

Get-Process -Id $LpProc.Id -ErrorAction SilentlyContinue | Stop-Process -Force

[pscustomobject]@{
    Shot       = (Split-Path $LpShot -Leaf)
    Zoom       = (Split-Path $LpZoom -Leaf)
    WindowPx   = ('{0}x{1}' -f (($LpBounds.Trim() -split ' ')[2]), (($LpBounds.Trim() -split ' ')[3]))
    LeftEdge   = $LpScan.Left
    TopEdge    = $LpScan.Top
    Log        = $LpLogLine
} | Format-List | Out-String -Width 240 | Write-Host
Write-Host '完成'
