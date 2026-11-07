#Requires -Version 7.0
<#
.SYNOPSIS
  M3 结果列表的后台验证：用 UI 控制管道的 QUERY / NAV 命令驱动搜索与选择，不显示窗口、不抢前台。
.DESCRIPTION
  被测实例以 --tray 启动（不显示窗口），查询走真实的 UI→服务命名管道链路。
  QUERY 等价于在搜索框输入文本并等结果回来；NAV 等价于按 ↑/↓ 移动选择。
  本脚本不注入任何键鼠事件，也不会把窗口切到前台。
.EXAMPLE
  pwsh -File .\tools\Test-LpUiResults.ps1 -LpQuery report
#>
[CmdletBinding()]
param(
    [string]$LpQuery = 'report',

    [ValidateSet('Debug', 'Release')]
    [string]$LpConfiguration = 'Debug',

    [switch]$LpStop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$LpRepoRoot = Split-Path -Parent $PSScriptRoot
$LpUiExe = Join-Path $LpRepoRoot "src\Pathleap.UI\bin\x64\$LpConfiguration\net8.0-windows10.0.22621.0\Pathleap.UI.exe"
$LpServiceExe = Join-Path $LpRepoRoot "src\Pathleap.Service\bin\x64\$LpConfiguration\net8.0-windows\Pathleap.Service.exe"

$LpResults = [System.Collections.Generic.List[object]]::new()

function Add-LpCheck {
    param([string]$LpName, [bool]$LpPassed, [string]$LpDetail)

    $LpResults.Add([pscustomobject]@{ Name = $LpName; Passed = $LpPassed; Detail = $LpDetail })
}

function Invoke-LpUiRequest {
    param(
        [Parameter(Mandatory)] [hashtable]$LpPayload,
        [int]$LpTimeoutMs = 5000
    )

    $LpClient = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', 'Pathleap.UI', [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $LpClient.Connect($LpTimeoutMs)
        $LpWriter = [System.IO.StreamWriter]::new($LpClient, [System.Text.Encoding]::UTF8, 1024, $true)
        $LpWriter.WriteLine(($LpPayload | ConvertTo-Json -Compress))
        $LpWriter.Flush()

        $LpReader = [System.IO.StreamReader]::new($LpClient, [System.Text.Encoding]::UTF8, $false, 1024, $true)
        $LpLine = $LpReader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($LpLine)) {
            throw 'UI 管道返回空响应'
        }

        return $LpLine | ConvertFrom-Json
    }
    finally {
        $LpClient.Dispose()
    }
}

function Wait-LpUiReady {
    param([int]$LpWaitSeconds = 20)

    $LpDeadline = [datetime]::UtcNow.AddSeconds($LpWaitSeconds)
    while ([datetime]::UtcNow -lt $LpDeadline) {
        try {
            $LpStatus = Invoke-LpUiRequest -LpPayload @{ command = 'STATUS' } -LpTimeoutMs 500
            if ($LpStatus.ok) { return $LpStatus }
        }
        catch {
            # UI 仍在启动，继续轮询。
        }

        Start-Sleep -Milliseconds 300
    }

    throw '等待 UI 管道超时（20 秒）'
}

if (-not (Test-Path -LiteralPath $LpUiExe)) {
    throw "UI 尚未编译: $LpUiExe。请先运行 tools\Build-Lp.ps1。"
}

# 1. 后台实例：服务与 UI 都不显示窗口。
$LpUiRunning = @(Get-Process -Name 'Pathleap.UI' -ErrorAction SilentlyContinue)
if ($LpUiRunning.Count -eq 0) {
    Write-Host '启动服务（--console，无窗口）…' -ForegroundColor Cyan
    Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = "`"$LpServiceExe`" --console"
    } | Out-Null

    Write-Host '启动常驻 UI（--tray，不显示窗口）…' -ForegroundColor Cyan
    Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = "`"$LpUiExe`" --tray"
    } | Out-Null
}

$LpUiStatus = Wait-LpUiReady
Add-LpCheck -LpName 'UI 控制管道可用' -LpPassed ([bool]$LpUiStatus.ok) -LpDetail $LpUiStatus.message

# 2. 查询：等价于在搜索框输入关键词。
$LpQueryReply = Invoke-LpUiRequest -LpPayload @{ command = 'QUERY'; query = $LpQuery } -LpTimeoutMs 15000
$LpHitCount = if ($null -ne $LpQueryReply.hits) { @($LpQueryReply.hits).Count } else { 0 }
Add-LpCheck -LpName "查询 '$LpQuery' 返回结果" -LpPassed ([bool]$LpQueryReply.ok -and $LpHitCount -gt 0) `
    -LpDetail ("results=$LpHitCount, selected=" + $LpQueryReply.message)
Add-LpCheck -LpName '查询耗时已回传' -LpPassed ([double]$LpQueryReply.queryMs -ge 0) `
    -LpDetail ("queryMs=" + $LpQueryReply.queryMs)

$LpFirstHit = $null
if ($LpHitCount -gt 0) {
    $LpFirstHit = @($LpQueryReply.hits)[0]
    Add-LpCheck -LpName '首条结果与关键词匹配' -LpPassed ($LpFirstHit.name -match [regex]::Escape($LpQuery)) `
        -LpDetail ("name=" + $LpFirstHit.name + ", path=" + $LpFirstHit.path)
    Add-LpCheck -LpName '结果带完整路径' -LpPassed (-not [string]::IsNullOrWhiteSpace($LpFirstHit.path)) `
        -LpDetail $LpFirstHit.path
    Add-LpCheck -LpName '默认选中第 0 行' -LpPassed ($LpQueryReply.message -match 'selected=0') `
        -LpDetail $LpQueryReply.message
}

# 3. 键盘导航：等价于 ↑/↓ 与 PgUp/PgDn。
$LpNavDown1 = Invoke-LpUiRequest -LpPayload @{ command = 'NAV'; samples = 1 }
$LpNavDown2 = Invoke-LpUiRequest -LpPayload @{ command = 'NAV'; samples = 1 }
$LpNavUp = Invoke-LpUiRequest -LpPayload @{ command = 'NAV'; samples = -1 }
$LpNavHome = Invoke-LpUiRequest -LpPayload @{ command = 'NAV'; samples = -99 }
$LpNavEnd = Invoke-LpUiRequest -LpPayload @{ command = 'NAV'; samples = 99 }
$LpEndResults = if ($LpNavEnd.message -match 'results=(\d+)') { [int]$Matches[1] } else { 0 }

Add-LpCheck -LpName '↓ 选中第 1 行' -LpPassed ($LpNavDown1.message -match 'selected=1') -LpDetail $LpNavDown1.message
Add-LpCheck -LpName '再 ↓ 选中第 2 行' -LpPassed ($LpNavDown2.message -match 'selected=2') -LpDetail $LpNavDown2.message
Add-LpCheck -LpName '↑ 回到第 1 行' -LpPassed ($LpNavUp.message -match 'selected=1') -LpDetail $LpNavUp.message
Add-LpCheck -LpName '上界钳制到第 0 行' -LpPassed ($LpNavHome.message -match 'selected=0') -LpDetail $LpNavHome.message
Add-LpCheck -LpName "下界钳制到最后一行（$($LpEndResults - 1)）" `
    -LpPassed ($LpEndResults -gt 0 -and $LpNavEnd.message -match "selected=$($LpEndResults - 1)") -LpDetail $LpNavEnd.message

# 4. 清空查询 → 结果清空、回到空状态。
$LpEmptyReply = Invoke-LpUiRequest -LpPayload @{ command = 'QUERY'; query = '' } -LpTimeoutMs 5000
Add-LpCheck -LpName '清空查询后结果归零' -LpPassed ($LpEmptyReply.message -match 'results=0') -LpDetail $LpEmptyReply.message

$LpFailed = @($LpResults | Where-Object { -not $_.Passed })
Write-Host ''
Write-Host '==== 结果列表后台验证 ====' -ForegroundColor Cyan
foreach ($LpResult in $LpResults) {
    $LpTag = if ($LpResult.Passed) { 'PASS' } else { 'FAIL' }
    $LpColor = if ($LpResult.Passed) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}  {2}" -f $LpTag, $LpResult.Name, $LpResult.Detail) -ForegroundColor $LpColor
}

if ($LpStop) {
    Write-Host ''
    Write-Host '关闭被测实例 …' -ForegroundColor Cyan
    try {
        Invoke-LpUiRequest -LpPayload @{ command = 'QUIT' } | Out-Null
    }
    catch {
        Write-Host '  实例已退出或管道不可用。' -ForegroundColor DarkGray
    }
}

if ($LpFailed.Count -gt 0) {
    throw "结果列表验证未通过：$($LpFailed.Count)/$($LpResults.Count) 项失败。"
}

Write-Host ''
Write-Host "结果列表验证通过：$($LpResults.Count)/$($LpResults.Count) 项（后台口径，未占用前台）。" -ForegroundColor Green
