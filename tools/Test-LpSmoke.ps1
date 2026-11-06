#Requires -Version 7.0
<#
.SYNOPSIS
  M1 空壳冒烟测试：进程、单实例、托盘/热键、UI 与服务管道、配置与日志。
.DESCRIPTION
  只做只读检查；-LpStop 时在结尾调用 --quit 关闭被测实例。
.EXAMPLE
  pwsh -File .\tools\Test-LpSmoke.ps1 -LpStop
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$LpConfiguration = 'Debug',

    [switch]$LpStop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$LpRepoRoot = Split-Path -Parent $PSScriptRoot
$LpUiExe = Join-Path $LpRepoRoot "src\Pathleap.UI\bin\x64\$LpConfiguration\net8.0-windows10.0.22621.0\Pathleap.UI.exe"
$LpStagedServiceExe = Join-Path (Split-Path -Parent $LpUiExe) 'Pathleap.Service.exe'
$LpUserRoot = Join-Path $env:LOCALAPPDATA 'Pathleap'
$LpConfigFile = Join-Path $LpUserRoot 'config.json'
$LpLogRoot = Join-Path $LpUserRoot 'logs'
$LpToday = Get-Date -Format 'yyyyMMdd'
$LpUiLog = Join-Path $LpLogRoot "ui-$LpToday.log"
$LpServiceLog = Join-Path $LpLogRoot "service-$LpToday.log"

$LpResults = [System.Collections.Generic.List[object]]::new()

function Add-LpCheck {
    param(
        [string]$LpName,
        [bool]$LpPassed,
        [string]$LpDetail
    )

    $LpResults.Add([pscustomobject]@{
            Name = $LpName
            Passed = $LpPassed
            Detail = $LpDetail
        })
}

function Invoke-LpPipeCommand {
    param(
        [string]$LpPipeName,
        [string]$LpCommand,
        [int]$LpTimeoutMs = 3000
    )

    $LpClient = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', $LpPipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $LpClient.Connect($LpTimeoutMs)
        $LpWriter = [System.IO.StreamWriter]::new($LpClient, [System.Text.Encoding]::UTF8, 1024, $true)
        $LpPayload = @{ command = $LpCommand } | ConvertTo-Json -Compress
        $LpWriter.WriteLine($LpPayload)
        $LpWriter.Flush()

        $LpReader = [System.IO.StreamReader]::new($LpClient, [System.Text.Encoding]::UTF8, $false, 1024, $true)
        $LpLine = $LpReader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($LpLine)) {
            throw "管道 $LpPipeName 未返回数据"
        }

        return $LpLine | ConvertFrom-Json
    }
    finally {
        $LpClient.Dispose()
    }
}

function Wait-LpPipeCommand {
    param(
        [string]$LpPipeName,
        [string]$LpCommand,
        [int]$LpWaitSeconds = 15
    )

    $LpDeadline = [datetime]::UtcNow.AddSeconds($LpWaitSeconds)
    while ([datetime]::UtcNow -lt $LpDeadline) {
        try {
            return Invoke-LpPipeCommand -LpPipeName $LpPipeName -LpCommand $LpCommand -LpTimeoutMs 500
        }
        catch {
            Start-Sleep -Milliseconds 300
        }
    }

    throw "等待管道超时: $LpPipeName"
}

# 1. 构建产物
Add-LpCheck -LpName 'UI 可执行文件存在' -LpPassed (Test-Path -LiteralPath $LpUiExe) -LpDetail $LpUiExe
Add-LpCheck -LpName '服务已部署到 UI 目录' -LpPassed (Test-Path -LiteralPath $LpStagedServiceExe) -LpDetail $LpStagedServiceExe

if (-not (Test-Path -LiteralPath $LpUiExe)) {
    throw "UI 尚未编译: $LpUiExe。请先运行 Build-Lp.ps1。"
}

# 2. 启动常驻实例
$LpUiProcesses = @(Get-Process -Name 'Pathleap.UI' -ErrorAction SilentlyContinue)
if ($LpUiProcesses.Count -eq 0) {
    Write-Host '启动常驻 UI ...' -ForegroundColor Cyan
    Start-Process -FilePath $LpUiExe
}
else {
    Write-Host "UI 已在运行: PID $($LpUiProcesses[0].Id)" -ForegroundColor DarkGray
}

# 3. UI 控制管道 + 热键
$LpUiStatus = Wait-LpPipeCommand -LpPipeName 'Pathleap.UI' -LpCommand 'STATUS'
Add-LpCheck -LpName 'UI 控制管道可用' -LpPassed ([bool]$LpUiStatus.ok) -LpDetail $LpUiStatus.message
Add-LpCheck -LpName '全局热键已注册' -LpPassed ($LpUiStatus.message -match 'registered=True') -LpDetail $LpUiStatus.message

# 4. 服务管道（UI 会自动拉起服务）
$LpServiceStatus = Wait-LpPipeCommand -LpPipeName 'Pathleap.Service' -LpCommand 'STATUS' -LpWaitSeconds 20
Add-LpCheck -LpName '服务管道可用' -LpPassed ([bool]$LpServiceStatus.ok) -LpDetail ("mode=" + $LpServiceStatus.status.mode + ", pid=" + $LpServiceStatus.status.pid)
Add-LpCheck -LpName '索引未实现（M1 预期）' -LpPassed ($LpServiceStatus.status.index -eq 'not-implemented') -LpDetail ("index=" + $LpServiceStatus.status.index)

# 5. 配置与日志
Add-LpCheck -LpName '配置文件存在' -LpPassed (Test-Path -LiteralPath $LpConfigFile) -LpDetail $LpConfigFile
if (Test-Path -LiteralPath $LpConfigFile) {
    $LpConfig = Get-Content -LiteralPath $LpConfigFile -Raw | ConvertFrom-Json
    Add-LpCheck -LpName '配置包含热键与窗口尺寸' -LpPassed ($null -ne $LpConfig.uiHotkey -and $null -ne $LpConfig.windowWidth) -LpDetail ("uiHotkey=" + $LpConfig.uiHotkey + ", window=" + $LpConfig.windowWidth + 'x' + $LpConfig.windowHeight)
}

Add-LpCheck -LpName 'UI 日志已写入' -LpPassed (Test-Path -LiteralPath $LpUiLog) -LpDetail $LpUiLog
if (Test-Path -LiteralPath $LpUiLog) {
    $LpUiLogText = Get-Content -LiteralPath $LpUiLog -Raw
    Add-LpCheck -LpName 'UI 日志包含热键注册成功' -LpPassed ($LpUiLogText -match '全局热键注册成功') -LpDetail 'ui log'
    Add-LpCheck -LpName 'UI 日志包含托盘图标已添加' -LpPassed ($LpUiLogText -match '托盘图标已添加') -LpDetail 'ui log'
}

Add-LpCheck -LpName '服务日志已写入' -LpPassed (Test-Path -LiteralPath $LpServiceLog) -LpDetail $LpServiceLog

# 6. 单实例转发
$LpStatusChild = Start-Process -FilePath $LpUiExe -ArgumentList '--status' -PassThru -Wait
Add-LpCheck -LpName '单实例 STATUS 转发' -LpPassed ($LpStatusChild.ExitCode -eq 0) -LpDetail ("exitCode=" + $LpStatusChild.ExitCode)

# 7. 汇总
$LpFailed = @($LpResults | Where-Object { -not $_.Passed })
Write-Host ''
Write-Host '==== 冒烟结果 ====' -ForegroundColor Cyan
foreach ($LpResult in $LpResults) {
    $LpTag = if ($LpResult.Passed) { 'PASS' } else { 'FAIL' }
    $LpColor = if ($LpResult.Passed) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}  {2}" -f $LpTag, $LpResult.Name, $LpResult.Detail) -ForegroundColor $LpColor
}

if ($LpStop) {
    Write-Host ''
    Write-Host '关闭被测实例 ...' -ForegroundColor Cyan
    Start-Process -FilePath $LpUiExe -ArgumentList '--quit' -Wait
}

if ($LpFailed.Count -gt 0) {
    throw "冒烟测试未通过：$($LpFailed.Count)/$($LpResults.Count) 项失败。"
}

Write-Host ''
Write-Host "冒烟测试通过：$($LpResults.Count)/$($LpResults.Count) 项。" -ForegroundColor Green
