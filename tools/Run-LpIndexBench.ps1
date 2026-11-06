#Requires -Version 7.0
<#
.SYNOPSIS
  索引与查询基准：触发 INDEX_BUILD（可选）后对 Pathleap.Service 连续发 SEARCH，统计服务端查询耗时与管道往返。

.DESCRIPTION
  只读操作：不改索引文件、不删除任何内容。结果写入指定 JSON 文件（默认放在 perf 目录）。
  查询耗时分两段记录：
    - queryMs   : 服务端从收到请求到算完结果（含映射内存扫描、路径重建、DTO 组装）
    - roundTrip : 客户端发请求到收到响应（含管道与序列化开销）
#>
[CmdletBinding()]
param(
    [string]$LpRoot = '',
    [ValidateSet('', 'auto', 'usn', 'walk', 'synthetic')]
    [string]$LpMode = '',
    [switch]$LpBuild,
    [string[]]$LpQueries = @('report', 'project_0000123', '.dll', 'zzzzzz', 'report dll'),
    [int]$LpIterations = 50,
    [int]$LpLimit = 30,
    [int]$LpBuildTimeoutMs = 600000,
    [string]$LpOutputJson = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$LpServicePipe = 'Pathleap.Service'
$LpRepoRoot = Split-Path -Parent $PSScriptRoot
$LpCandidates = @(
    (Join-Path $LpRepoRoot 'src\Pathleap.Service\bin\x64\Debug\net8.0-windows\Pathleap.Service.exe'),
    (Join-Path $LpRepoRoot 'src\Pathleap.Service\bin\x64\Release\net8.0-windows\Pathleap.Service.exe')
) | Where-Object { Test-Path -LiteralPath $_ }

# 取最新编译的那个：避免跑到旧 Release 产物上测到过时行为。
$LpServiceExe = $LpCandidates |
    Sort-Object -Property @{ Expression = { (Get-Item -LiteralPath $_).LastWriteTimeUtc } } -Descending |
    Select-Object -First 1

if ([string]::IsNullOrWhiteSpace($LpOutputJson)) {
    $LpOutputJson = Join-Path (Split-Path -Parent $PSScriptRoot) ('perf\index-bench-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}

function Send-LpServiceRequest {
    param(
        [Parameter(Mandatory)] [hashtable]$LpPayload,
        [int]$LpConnectTimeoutMs = 3000,
        [int]$LpReadTimeoutMs = 5000
    )
    $LpPipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $LpServicePipe, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $LpPipe.Connect($LpConnectTimeoutMs)
        $LpReader = [System.IO.StreamReader]::new($LpPipe, [System.Text.UTF8Encoding]::new($false))
        $LpWriter = [System.IO.StreamWriter]::new($LpPipe, [System.Text.UTF8Encoding]::new($false))
        $LpWriter.AutoFlush = $true
        $LpWriter.WriteLine(($LpPayload | ConvertTo-Json -Compress))

        $LpTask = $LpReader.ReadLineAsync()
        $LpDeadline = [DateTime]::UtcNow.AddMilliseconds($LpReadTimeoutMs)
        while (-not $LpTask.IsCompleted -and [DateTime]::UtcNow -lt $LpDeadline) {
            Start-Sleep -Milliseconds 5
        }

        if (-not $LpTask.IsCompleted) {
            throw "等待服务响应超时（$LpReadTimeoutMs ms）"
        }

        $LpLine = $LpTask.GetAwaiter().GetResult()
        if ([string]::IsNullOrWhiteSpace($LpLine)) {
            throw '服务返回空响应'
        }

        return $LpLine | ConvertFrom-Json
    }
    finally {
        $LpPipe.Dispose()
    }
}

function Get-LpPercentile {
    param([double[]]$LpValues, [double]$LpPercentile)
    if ($LpValues.Count -eq 0) { return 0 }
    $LpSorted = $LpValues | Sort-Object
    $LpRank = [math]::Ceiling(($LpPercentile / 100.0) * $LpSorted.Count)
    $LpIndex = [math]::Min($LpSorted.Count - 1, [math]::Max(0, $LpRank - 1))
    return [math]::Round([double]$LpSorted[$LpIndex], 3)
}

function Start-LpServiceIfNeeded {
    $LpPing = $null
    try { $LpPing = Send-LpServiceRequest @{ Command = 'PING' } -LpReadTimeoutMs 1500 } catch { }
    if ($LpPing -and $LpPing.Ok) { return $false }

    if (-not (Test-Path -LiteralPath $LpServiceExe)) {
        throw "找不到服务可执行文件: $LpServiceExe。请先运行 tools\Build-Lp.ps1。"
    }

    Write-Host "启动服务: $LpServiceExe" -ForegroundColor DarkGray
    Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = "`"$LpServiceExe`""
    } | Out-Null
    for ($LpI = 0; $LpI -lt 40; $LpI++) {
        Start-Sleep -Milliseconds 250
        try {
            $LpPing = Send-LpServiceRequest @{ Command = 'PING' } -LpReadTimeoutMs 800
            if ($LpPing.Ok) { return $true }
        }
        catch {
            # 服务仍在启动，继续轮询。
        }
    }

    throw '服务启动后 10 秒内未响应 PING。'
}

$LpStartedService = Start-LpServiceIfNeeded

if ($LpBuild) {
    Write-Host '触发索引构建…' -ForegroundColor Cyan
    $LpBuildPayload = @{ Command = 'INDEX_BUILD' }
    if ($LpRoot) { $LpBuildPayload.Root = $LpRoot }
    if ($LpMode) { $LpBuildPayload.Mode = $LpMode }
    $LpBuildWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $LpBuildReply = Send-LpServiceRequest $LpBuildPayload -LpReadTimeoutMs $LpBuildTimeoutMs
    $LpBuildWatch.Stop()
    if (-not $LpBuildReply.Ok) {
        throw "索引构建失败: $($LpBuildReply.Message)"
    }

    Write-Host ("索引构建完成：{0} 条，服务端 {1:0} ms，客户端观测 {2:0} ms" -f `
        $LpBuildReply.Index.EntryCount, $LpBuildReply.Index.BuildMs, $LpBuildWatch.Elapsed.TotalMilliseconds) -ForegroundColor Green
}

$LpStatusReply = Send-LpServiceRequest @{ Command = 'INDEX_STATUS' }
if (-not $LpStatusReply.Index.Loaded) {
    throw '索引未装载，请加 -LpBuild 或在已有索引上运行。'
}

Write-Host ("索引: {0} 条 / {1:0.0} MB / 来源 {2} / 装载 {3:0.00} ms" -f `
    $LpStatusReply.Index.EntryCount, ($LpStatusReply.Index.FileBytes / 1MB), $LpStatusReply.Index.Source, $LpStatusReply.Index.LoadMs) -ForegroundColor DarkGray

$LpResultQueries = New-Object System.Collections.Generic.List[object]
$LpAllQueryMs = New-Object System.Collections.Generic.List[double]
$LpAllRoundTripMs = New-Object System.Collections.Generic.List[double]

foreach ($LpQuery in $LpQueries) {
    # 先跑一次预热（JIT/页表），不计入统计。
    $LpWarmupReply = Send-LpServiceRequest @{ Command = 'SEARCH'; Query = $LpQuery; Limit = $LpLimit }

    $LpQueryMs = New-Object System.Collections.Generic.List[double]
    $LpRoundTripMs = New-Object System.Collections.Generic.List[double]
    $LpHitCount = 0
    $LpCandidateCount = 0
    for ($LpIter = 0; $LpIter -lt $LpIterations; $LpIter++) {
        $LpWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $LpReplyItem = Send-LpServiceRequest @{ Command = 'SEARCH'; Query = $LpQuery; Limit = $LpLimit }
        $LpWatch.Stop()
        if (-not $LpReplyItem.Ok) {
            throw "查询失败（$LpQuery）: $($LpReplyItem.Message)"
        }

        $LpQueryMs.Add([double]$LpReplyItem.QueryMs)
        $LpRoundTripMs.Add($LpWatch.Elapsed.TotalMilliseconds)
        $LpHitCount = $LpReplyItem.Hits.Count
        $LpCandidateCount = $LpReplyItem.CandidateCount
    }

    $LpQueryArray = [double[]]$LpQueryMs.ToArray()
    $LpRoundTripArray = [double[]]$LpRoundTripMs.ToArray()
    foreach ($LpValue in $LpQueryArray) { $LpAllQueryMs.Add($LpValue) }
    foreach ($LpValue in $LpRoundTripArray) { $LpAllRoundTripMs.Add($LpValue) }

    $LpSummary = [pscustomobject]@{
        query          = $LpQuery
        iterations     = $LpIterations
        hits           = $LpHitCount
        candidates     = $LpCandidateCount
        queryP50       = Get-LpPercentile -LpValues $LpQueryArray -LpPercentile 50
        queryP95       = Get-LpPercentile -LpValues $LpQueryArray -LpPercentile 95
        queryP99       = Get-LpPercentile -LpValues $LpQueryArray -LpPercentile 99
        queryMax       = [math]::Round(($LpQueryArray | Measure-Object -Maximum).Maximum, 3)
        roundTripP50   = Get-LpPercentile -LpValues $LpRoundTripArray -LpPercentile 50
        roundTripP95   = Get-LpPercentile -LpValues $LpRoundTripArray -LpPercentile 95
    }
    $LpResultQueries.Add($LpSummary)
    Write-Host ("[{0}] P50={1:0.000} P95={2:0.000} P99={3:0.000} max={4:0.000} ms（往返 P95={5:0.00}）hits={6}" -f `
        $LpQuery, $LpSummary.queryP50, $LpSummary.queryP95, $LpSummary.queryP99, $LpSummary.queryMax, $LpSummary.roundTripP95, $LpHitCount)
}

$LpAllQueryArray = [double[]]$LpAllQueryMs.ToArray()
$LpSummaryAll = [pscustomobject]@{
    generatedAt    = (Get-Date).ToString('o')
    machine        = [pscustomobject]@{
        processor    = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
        logicalCores = [Environment]::ProcessorCount
        osCaption    = (Get-CimInstance Win32_OperatingSystem).Caption
        build        = (Get-CimInstance Win32_OperatingSystem).BuildNumber
    }
    index          = $LpStatusReply.Index
    serviceStarted = $LpStartedService
    iterations     = $LpIterations
    limit          = $LpLimit
    queries        = $LpResultQueries
    overall        = [pscustomobject]@{
        queryP50     = Get-LpPercentile -LpValues $LpAllQueryArray -LpPercentile 50
        queryP95     = Get-LpPercentile -LpValues $LpAllQueryArray -LpPercentile 95
        queryP99     = Get-LpPercentile -LpValues $LpAllQueryArray -LpPercentile 99
        queryMax     = [math]::Round(($LpAllQueryArray | Measure-Object -Maximum).Maximum, 3)
        roundTripP50 = Get-LpPercentile -LpValues ([double[]]$LpAllRoundTripMs.ToArray()) -LpPercentile 50
        roundTripP95 = Get-LpPercentile -LpValues ([double[]]$LpAllRoundTripMs.ToArray()) -LpPercentile 95
    }
}

$LpOutputDirectory = Split-Path -Parent $LpOutputJson
if ($LpOutputDirectory -and -not (Test-Path -LiteralPath $LpOutputDirectory)) {
    New-Item -ItemType Directory -Path $LpOutputDirectory -Force | Out-Null
}

$LpSummaryAll | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $LpOutputJson -Encoding utf8
Write-Host "结果已写入: $LpOutputJson" -ForegroundColor Green
