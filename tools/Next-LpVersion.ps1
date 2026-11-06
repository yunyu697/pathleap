#Requires -Version 7.0
<#
.SYNOPSIS
  按项目版本规则计算下一版，可直接写回唯一来源 Directory.Build.props。
.DESCRIPTION
  规则：major 不限；minor / patch 取值 0–10；某一位加 1 后超过 10 就向前进位并归零。
    patch=10 再升 patch  -> minor+1、patch=0
    minor=10 且 patch=10 再进位 -> major+1、minor=0、patch=0
  alpha 编号不设上限，只对 alpha 本身递增。
.EXAMPLE
  pwsh -File .\tools\Next-LpVersion.ps1 -LpBump patch
.EXAMPLE
  pwsh -File .\tools\Next-LpVersion.ps1 -LpBump patch -LpApply
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [ValidateSet('patch', 'minor', 'major', 'alpha')]
    [string]$LpBump = 'patch',

    [switch]$LpApply,

    # 仅用于预览/核对规则，不读文件
    [string]$LpCurrent = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$LpRepoRoot = Split-Path -Parent $PSScriptRoot
$LpPropsFile = Join-Path $LpRepoRoot 'Directory.Build.props'

if ([string]::IsNullOrWhiteSpace($LpCurrent)) {
    $LpPropsRaw = Get-Content -LiteralPath $LpPropsFile -Raw
    $LpVersionMatch = [regex]::Match($LpPropsRaw, '<PathleapVersion>([^<]+)</PathleapVersion>')
    if (-not $LpVersionMatch.Success) {
        throw "Directory.Build.props 中缺少 <PathleapVersion>。"
    }

    $LpCurrent = $LpVersionMatch.Groups[1].Value.Trim()
}

$LpParse = [regex]::Match($LpCurrent, '^([0-9]+)\.([0-9]+)\.([0-9]+)(?:-alpha([0-9]+))?$')
if (-not $LpParse.Success) {
    throw "当前版本格式不合法: $LpCurrent（期望 1.0.0 或 1.0.0-alpha1）。"
}

$LpMajor = [int]$LpParse.Groups[1].Value
$LpMinor = [int]$LpParse.Groups[2].Value
$LpPatch = [int]$LpParse.Groups[3].Value
$LpAlpha = if ($LpParse.Groups[4].Success) { [int]$LpParse.Groups[4].Value } else { -1 }

if ($LpMinor -gt 10 -or $LpPatch -gt 10) {
    throw "当前版本越界: $LpCurrent（minor / patch 必须在 0–10）。"
}

$LpNextMajor = $LpMajor
$LpNextMinor = $LpMinor
$LpNextPatch = $LpPatch
$LpNextAlpha = $LpAlpha

switch ($LpBump) {
    'alpha' {
        $LpNextAlpha = if ($LpAlpha -lt 1) { 1 } else { $LpAlpha + 1 }
    }
    'patch' {
        $LpNextPatch = $LpPatch + 1
        $LpNextAlpha = -1
        if ($LpNextPatch -gt 10) {
            $LpNextPatch = 0
            $LpNextMinor = $LpMinor + 1
            if ($LpNextMinor -gt 10) {
                $LpNextMinor = 0
                $LpNextMajor = $LpMajor + 1
            }
        }
    }
    'minor' {
        $LpNextMinor = $LpMinor + 1
        $LpNextPatch = 0
        $LpNextAlpha = -1
        if ($LpNextMinor -gt 10) {
            $LpNextMinor = 0
            $LpNextMajor = $LpMajor + 1
        }
    }
    'major' {
        $LpNextMajor = $LpMajor + 1
        $LpNextMinor = 0
        $LpNextPatch = 0
        $LpNextAlpha = -1
    }
}

$LpNextVersion = "$LpNextMajor.$LpNextMinor.$LpNextPatch"
if ($LpNextAlpha -ge 1) {
    $LpNextVersion += "-alpha$LpNextAlpha"
}

Write-Host "当前版本: $LpCurrent"
Write-Host "升级类型: $LpBump"
Write-Host "下一版本: $LpNextVersion" -ForegroundColor Green

if (-not $LpApply) {
    Write-Host '（预览模式，未写回。加 -LpApply 才会修改 Directory.Build.props。）' -ForegroundColor DarkGray
    return
}

if (-not $PSCmdlet.ShouldProcess($LpPropsFile, "把 PathleapVersion 从 $LpCurrent 改为 $LpNextVersion")) {
    return
}

$LpUpdatedRaw = [regex]::Replace(
    $LpPropsRaw,
    '<PathleapVersion>[^<]*</PathleapVersion>',
    "<PathleapVersion>$LpNextVersion</PathleapVersion>")

Set-Content -LiteralPath $LpPropsFile -Value $LpUpdatedRaw -Encoding utf8 -NoNewline
Write-Host "已写回 $LpPropsFile（git tag 请用 v$LpNextVersion）。" -ForegroundColor Green
