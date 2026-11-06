#Requires -Version 7.0
<#
.SYNOPSIS
  读取一个 .plidx 索引文件，输出体积构成与文件名长度分布（只读，不改文件）。

.DESCRIPTION
  用于“存储开销”实测：把索引体积拆成 头部 / 记录区 / 名字区，并给出每条平均字节数、
  文件名长度分位点、完全重复文件名的占比。结论写进 docs 报告，不允许用估算替代。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$LpIndexFile,
    [int]$LpMaxStatsEntries = 1000000,
    [string]$LpOutputJson = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $LpIndexFile)) {
    throw "索引文件不存在: $LpIndexFile"
}

$LpHeaderSize = 256
$LpBytes = [System.IO.File]::ReadAllBytes($LpIndexFile)
$LpEntryCount = [System.BitConverter]::ToInt32($LpBytes, 8)
$LpRecordSize = [System.BitConverter]::ToUInt16($LpBytes, 12)
if ($LpRecordSize -le 0) { $LpRecordSize = 20 }
$LpArenaOffset = [System.BitConverter]::ToInt64($LpBytes, 16)
$LpArenaSize = [System.BitConverter]::ToInt64($LpBytes, 24)
$LpSourceLength = [System.BitConverter]::ToInt32($LpBytes, 40)
$LpSource = ''
if ($LpSourceLength -gt 0) {
    $LpSource = [System.Text.Encoding]::UTF8.GetString($LpBytes, 44, [Math]::Min($LpSourceLength, $LpBytes.Length - 44))
}

$LpRecordBytes = [long]$LpEntryCount * $LpRecordSize
$LpFileBytes = $LpBytes.LongLength
$LpBlockSize = 5

# 名字长度只能从记录里取；大索引抽样，避免脚本自身成为瓶颈。
$LpNameLengths = [System.Collections.Generic.List[int]]::new()
$LpStatStep = 1
$LpStatCount = $LpEntryCount
if ($LpEntryCount -gt $LpMaxStatsEntries) {
    $LpStatStep = [int][Math]::Ceiling($LpEntryCount / $LpMaxStatsEntries)
    # 抽样步长若正好等于前缀块大小，会只抽到块首（前缀长度恒为 0），故意错开一步。
    if ($LpStatStep % $LpBlockSize -eq 0) {
        $LpStatStep++
    }
    $LpStatCount = [int][Math]::Floor($LpEntryCount / $LpStatStep)
}

$LpNames = [System.Collections.Generic.HashSet[string]]::new()
$LpDuplicateCount = 0
$LpNameBytesTotal = [long]0
$LpSuffixBytesTotal = [long]0
$LpPrefixSavedTotal = [long]0
$LpPrefixLensBase = $LpHeaderSize + $LpEntryCount * $LpRecordSize
for ($LpI = 0; $LpI -lt $LpEntryCount; $LpI += $LpStatStep) {
    $LpRecordBase = $LpHeaderSize + $LpI * $LpRecordSize
    $LpSuffixOffset = [System.BitConverter]::ToUInt32($LpBytes, $LpRecordBase)
    if ($LpRecordSize -eq 16) {
        # v4 布局：偏移 12 的 u16 = 低 10 位名字字节数 / 高 6 位深度；
        # 名字前缀长度在记录区之后的 1 字节数组里。
        $LpNameLength = [System.BitConverter]::ToUInt16($LpBytes, $LpRecordBase + 12) -band 0x03FF
        $LpPrefixLen = [int]$LpBytes[$LpPrefixLensBase + $LpI]
    }
    else {
        $LpNameLength = [System.BitConverter]::ToUInt16($LpBytes, $LpRecordBase + 4)
        $LpPrefixLen = 0
    }
    $LpSuffixLength = $LpNameLength - $LpPrefixLen

    $LpNameLengths.Add([int]$LpNameLength)
    $LpNameBytesTotal += $LpNameLength
    $LpSuffixBytesTotal += $LpSuffixLength
    $LpPrefixSavedTotal += $LpPrefixLen

    if ($LpNameLength -gt 0) {
        $LpName = ''
        if ($LpPrefixLen -gt 0) {
            $LpBlockStart = [int]([Math]::Floor($LpI / $LpBlockSize) * $LpBlockSize)
            $LpBlockBase = $LpHeaderSize + $LpBlockStart * $LpRecordSize
            $LpBlockOffset = [System.BitConverter]::ToUInt32($LpBytes, $LpBlockBase)
            $LpBlockLength = [System.BitConverter]::ToUInt16($LpBytes, $LpBlockBase + 12) -band 0x03FF
            $LpFull = [byte[]]::new($LpNameLength)
            [Array]::Copy($LpBytes, [int]($LpArenaOffset + $LpBlockOffset), $LpFull, 0, $LpPrefixLen)
            [Array]::Copy($LpBytes, [int]($LpArenaOffset + $LpSuffixOffset), $LpFull, $LpPrefixLen, $LpSuffixLength)
            $LpName = [System.Text.Encoding]::UTF8.GetString($LpFull)
        }
        else {
            $LpName = [System.Text.Encoding]::UTF8.GetString($LpBytes, [int]($LpArenaOffset + $LpSuffixOffset), [int]$LpSuffixLength)
        }

        if (-not $LpNames.Add($LpName)) {
            $LpDuplicateCount++
        }
    }
}

$LpSortedLengths = [int[]]$LpNameLengths.ToArray()
[Array]::Sort($LpSortedLengths)

function Get-LpLengthPercentile {
    param([int[]]$LpValues, [double]$LpPercentile)
    if ($LpValues.Count -eq 0) { return 0 }
    $LpIndex = [int][Math]::Floor(($LpPercentile / 100.0) * ($LpValues.Count - 1))
    return $LpValues[$LpIndex]
}

$LpNameBytesAvg = if ($LpStatCount -gt 0) { [Math]::Round($LpNameBytesTotal / $LpStatCount, 1) } else { 0 }
$LpBytesPerEntry = if ($LpEntryCount -gt 0) { [Math]::Round($LpFileBytes / $LpEntryCount, 2) } else { 0 }

$LpResult = [pscustomobject]@{
    file            = (Resolve-Path -LiteralPath $LpIndexFile).Path
    fileBytes       = $LpFileBytes
    fileMB          = [Math]::Round($LpFileBytes / 1MB, 1)
    entryCount      = $LpEntryCount
    recordBytes     = $LpRecordBytes
    arenaBytes      = $LpArenaSize
    headerBytes     = $LpHeaderSize
    bytesPerEntry   = $LpBytesPerEntry
    nameBytesAvg    = $LpNameBytesAvg
    suffixBytes     = $LpSuffixBytesTotal
    suffixBytesAvg  = if ($LpStatCount -gt 0) { [Math]::Round($LpSuffixBytesTotal / $LpStatCount, 1) } else { 0 }
    prefixSaved     = $LpPrefixSavedTotal
    prefixSavedAvg  = if ($LpStatCount -gt 0) { [Math]::Round($LpPrefixSavedTotal / $LpStatCount, 1) } else { 0 }
    prefixLensBytes = if ($LpRecordSize -eq 16) { $LpEntryCount } else { 0 }
    nameBytesP50    = Get-LpLengthPercentile -LpValues $LpSortedLengths -LpPercentile 50
    nameBytesP90    = Get-LpLengthPercentile -LpValues $LpSortedLengths -LpPercentile 90
    nameBytesP99    = Get-LpLengthPercentile -LpValues $LpSortedLengths -LpPercentile 99
    nameBytesMax    = if ($LpSortedLengths.Count -gt 0) { $LpSortedLengths[$LpSortedLengths.Count - 1] } else { 0 }
    sampledEntries  = $LpStatCount
    sampleStep      = $LpStatStep
    duplicateNames  = $LpDuplicateCount
    duplicateRatio  = if ($LpStatCount -gt 0) { [Math]::Round($LpDuplicateCount / $LpStatCount, 3) } else { 0 }
    source          = $LpSource
}

if ([string]::IsNullOrWhiteSpace($LpOutputJson)) {
    $LpOutputJson = Join-Path (Split-Path -Parent $PSScriptRoot) ('perf\index-measure-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}

$LpOutputDirectory = Split-Path -Parent $LpOutputJson
if ($LpOutputDirectory -and -not (Test-Path -LiteralPath $LpOutputDirectory)) {
    New-Item -ItemType Directory -Path $LpOutputDirectory -Force | Out-Null
}

$LpResult | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $LpOutputJson -Encoding utf8
$LpResult | Format-List
Write-Host "结果已写入: $LpOutputJson" -ForegroundColor Green
