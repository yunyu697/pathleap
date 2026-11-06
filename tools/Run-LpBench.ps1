#Requires -Version 7.0
<#
.SYNOPSIS
  运行唤醒延迟基准测试（默认 200 次），并打印首帧 / 可输入的分位数。
.DESCRIPTION
  这是「前台实测」工具：基准靠 SendInput 注入全局热键，再由真实的 WM_HOTKEY 触发显示，
  每次采样都会把 Pathleap 窗口切到前台。因此默认拒绝运行，必须显式加 -LpAllowForeground，
  运行前请自行确认可以占用前台。需要解锁的交互式桌面；锁屏、无桌面会话或热键被其它程序
  占用时都会失败。索引/查询类性能请用 Run-LpIndexBench.ps1（后台跑，不碰前台）。
.EXAMPLE
  pwsh -File .\tools\Run-LpBench.ps1 -LpSamples 200 -LpAllowForeground
#>
[CmdletBinding()]
param(
    [ValidateRange(10, 5000)]
    [int]$LpSamples = 200,

    [ValidateSet('Debug', 'Release')]
    [string]$LpConfiguration = 'Debug',

    [switch]$LpEnsureRunning,

    [switch]$LpAllowForeground
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$LpRepoRoot = Split-Path -Parent $PSScriptRoot
$LpUiExe = Join-Path $LpRepoRoot "src\Pathleap.UI\bin\x64\$LpConfiguration\net8.0-windows10.0.22621.0\Pathleap.UI.exe"
$LpSummaryFile = Join-Path $env:LOCALAPPDATA 'Pathleap\perf\last-bench-summary.json'

if (-not (Test-Path -LiteralPath $LpUiExe)) {
    throw "UI 尚未编译: $LpUiExe。请先运行 Build-Lp.ps1。"
}

if (-not $LpAllowForeground) {
    $LpEstimatedSeconds = [math]::Round($LpSamples * 0.45)
    throw ("唤醒基准会把 Pathleap 窗口反复切到前台（共 {0} 次），会打断当前操作，预计占用约 {1} 秒。" -f $LpSamples, $LpEstimatedSeconds) +
        '确认可以占用前台后再加 -LpAllowForeground 运行。查询/索引类基准请用 Run-LpIndexBench.ps1（后台，无前台窗口）。'
}

if ($LpEnsureRunning) {
    $LpRunning = @(Get-Process -Name 'Pathleap.UI' -ErrorAction SilentlyContinue)
    if ($LpRunning.Count -eq 0) {
        Write-Host '启动常驻 UI（--tray，不显示窗口）...' -ForegroundColor Cyan
        Start-Process -FilePath $LpUiExe -ArgumentList '--tray'
        Start-Sleep -Seconds 3
    }
}

$LpPreviousWriteTime = $null
if (Test-Path -LiteralPath $LpSummaryFile) {
    $LpPreviousWriteTime = (Get-Item -LiteralPath $LpSummaryFile).LastWriteTimeUtc
}

Write-Host "运行基准：$LpSamples 次（热键 Ctrl+Alt+Space，探测键由配置 benchProbeKey 决定） ..." -ForegroundColor Cyan
Write-Host '测试期间请勿操作键盘鼠标。' -ForegroundColor DarkGray

$LpBenchProcess = Start-Process -FilePath $LpUiExe -ArgumentList @('--bench', "$LpSamples", '--allow-foreground') -PassThru -Wait
Write-Host "基准进程退出码: $($LpBenchProcess.ExitCode)" -ForegroundColor DarkGray

if (-not (Test-Path -LiteralPath $LpSummaryFile)) {
    throw "未找到基准结果: $LpSummaryFile。请检查 $env:LOCALAPPDATA\Pathleap\logs\ui-*.log。"
}

$LpCurrentWriteTime = (Get-Item -LiteralPath $LpSummaryFile).LastWriteTimeUtc
if ($null -ne $LpPreviousWriteTime -and $LpCurrentWriteTime -le $LpPreviousWriteTime) {
    throw "基准结果文件未被更新（$LpSummaryFile），本次运行可能没有真正执行。请检查日志。"
}

$LpSummary = Get-Content -LiteralPath $LpSummaryFile -Raw | ConvertFrom-Json

Write-Host ''
Write-Host '==== 唤醒基准结果 ====' -ForegroundColor Green
Write-Host ("产品/版本   : {0} {1}" -f $LpSummary.product, $LpSummary.appVersion)
Write-Host ("机器        : {0} / {1} 逻辑核 / {2}Hz / DPI {3}" -f $LpSummary.machine.cpu, $LpSummary.machine.logicalProcessors, $LpSummary.machine.displayRefreshHz, $LpSummary.machine.displayDpi)
Write-Host ("系统 / 框架 : {0} / {1}" -f $LpSummary.machine.osVersion, $LpSummary.machine.framework)
Write-Host ("样本        : 请求 {0}，有效送达 {1}，warm 失败 {2}" -f $LpSummary.samplesRequested, $LpSummary.samplesCompleted, $LpSummary.failures)
Write-Host ''
Write-Host ("首帧        : P50 {0:0.###}ms / P95 {1:0.###}ms / max {2:0.###}ms" -f $LpSummary.firstFrame.p50, $LpSummary.firstFrame.p95, $LpSummary.firstFrame.max)
Write-Host ("获得焦点    : P50 {0:0.###}ms / P95 {1:0.###}ms / max {2:0.###}ms" -f $LpSummary.focus.p50, $LpSummary.focus.p95, $LpSummary.focus.max)
Write-Host ("可输入      : P50 {0:0.###}ms / P95 {1:0.###}ms / max {2:0.###}ms" -f $LpSummary.inputReady.p50, $LpSummary.inputReady.p95, $LpSummary.inputReady.max)
Write-Host ("文本提交    : P50 {0:0.###}ms / P95 {1:0.###}ms" -f $LpSummary.textCommit.p50, $LpSummary.textCommit.p95)
Write-Host ''
Write-Host ("冷启动样本  : 首帧 {0:0.###}ms / 焦点 {1:0.###}ms / 可输入 {2:0.###}ms" -f $LpSummary.coldSample.hotkeyToFirstFrameMs, $LpSummary.coldSample.hotkeyToFocusMs, $LpSummary.coldSample.hotkeyToKeyMs)
Write-Host ("阈值        : P50 <= {0}ms，P95 <= {1}ms" -f $LpSummary.p50TargetMs, $LpSummary.p95TargetMs)
Write-Host ("结论        : {0}" -f $LpSummary.verdict) -ForegroundColor $(if ($LpSummary.pass) { 'Green' } else { 'Yellow' })
Write-Host ''
Write-Host ("CSV  : {0}" -f $LpSummary.csvPath)
Write-Host ("JSON : {0}" -f $LpSummary.jsonPath)
