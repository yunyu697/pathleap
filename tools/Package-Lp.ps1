#Requires -Version 7.0
<#
.SYNOPSIS
  把 UI / Service 的构建产物打包成可分发 zip（不入库）。
.DESCRIPTION
  只做复制与压缩，不做任何删除。产物布局：
    Pathleap-win-x64/
      Pathleap.UI.exe + WinUI 运行时依赖
      Pathleap.Service.exe + 依赖（UI 会在同目录拉起它）
      README.md
.EXAMPLE
  pwsh -File .\tools\Package-Lp.ps1 -LpConfiguration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$LpConfiguration = 'Release',

    [string]$LpOutputRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$LpRepoRoot = Split-Path -Parent $PSScriptRoot
$LpUiDir = Join-Path $LpRepoRoot "src\Pathleap.UI\bin\x64\$LpConfiguration\net8.0-windows10.0.22621.0"
$LpServiceDir = Join-Path $LpRepoRoot "src\Pathleap.Service\bin\x64\$LpConfiguration\net8.0-windows"

if ([string]::IsNullOrWhiteSpace($LpOutputRoot)) {
    $LpOutputRoot = Join-Path $LpRepoRoot 'dist'
}

$LpUiExe = Join-Path $LpUiDir 'Pathleap.UI.exe'
$LpServiceExe = Join-Path $LpServiceDir 'Pathleap.Service.exe'

if (-not (Test-Path -LiteralPath $LpUiExe)) {
    throw "UI 尚未编译: $LpUiExe。"
}

if (-not (Test-Path -LiteralPath $LpServiceExe)) {
    throw "服务尚未编译: $LpServiceExe。"
}

$LpStageDir = Join-Path $LpOutputRoot 'Pathleap-win-x64'

# 版本号取自程序集（源头是 Directory.Build.props，构建时可能被 CI 的 tag 覆盖），不在脚本里另写。
$LpProductVersion = (Get-Item -LiteralPath $LpUiExe).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($LpProductVersion)) {
    throw "无法从 $LpUiExe 读取 ProductVersion。"
}

$LpZipPath = Join-Path $LpOutputRoot "Pathleap-$LpProductVersion-win-x64.zip"

New-Item -ItemType Directory -Path $LpOutputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $LpStageDir -Force | Out-Null

# UI 目录已经包含除服务之外的全部依赖（Build-Lp.ps1 会把服务复制进来）。
$LpFiles = Get-ChildItem -LiteralPath $LpUiDir -File | Where-Object { $_.Extension -notin @('.pdb', '.xml') }
foreach ($LpFile in $LpFiles) {
    Copy-Item -LiteralPath $LpFile.FullName -Destination (Join-Path $LpStageDir $LpFile.Name) -Force
}

$LpServiceFiles = Get-ChildItem -LiteralPath $LpServiceDir -File |
    Where-Object { $_.Extension -notin @('.pdb', '.xml') -and $_.Name -like 'Pathleap.Service.*' }
foreach ($LpFile in $LpServiceFiles) {
    Copy-Item -LiteralPath $LpFile.FullName -Destination (Join-Path $LpStageDir $LpFile.Name) -Force
}

Copy-Item -LiteralPath (Join-Path $LpRepoRoot 'README.md') -Destination (Join-Path $LpStageDir 'README.md') -Force

Compress-Archive -Path (Join-Path $LpStageDir '*') -DestinationPath $LpZipPath -CompressionLevel Optimal -Force

$LpZipItem = Get-Item -LiteralPath $LpZipPath
Write-Host ("打包完成: {0}（{1:0.0} MB）" -f $LpZipPath, ($LpZipItem.Length / 1MB)) -ForegroundColor Green
Write-Host '注意：这是开发构建，运行机需要 .NET 8 Desktop Runtime 与 Windows App Runtime（未做 self-contained 打包）。' -ForegroundColor DarkGray

return $LpZipPath
