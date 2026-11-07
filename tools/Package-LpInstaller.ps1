#Requires -Version 7.0
<#
.SYNOPSIS
  用 Inno Setup 生成 Windows 安装包（.exe），输出到 dist。
.DESCRIPTION
  输入是 Package-Lp.ps1 生成的 dist\Pathleap-win-x64 目录；版本号只从 UI 程序的
  ProductVersion 读取（源头仍是 Directory.Build.props），不在脚本里另写。
  产物名：Pathleap-<版本>-win-x64-setup.exe
.EXAMPLE
  pwsh -File .\tools\Package-Lp.ps1
  pwsh -File .\tools\Package-LpInstaller.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$LpConfiguration = 'Release',

    # 留空时依次查找 PATH、Inno Setup 的默认安装位置
    [string]$LpIsccPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$LpRepoRoot = Split-Path -Parent $PSScriptRoot
$LpUiExe = Join-Path $LpRepoRoot "src\Pathleap.UI\bin\x64\$LpConfiguration\net8.0-windows10.0.22621.0\Pathleap.UI.exe"
$LpOutputRoot = Join-Path $LpRepoRoot 'dist'
$LpStageDir = Join-Path $LpOutputRoot 'Pathleap-win-x64'
$LpIssFile = Join-Path $LpRepoRoot 'installer\Pathleap.iss'

if (-not (Test-Path -LiteralPath $LpUiExe)) {
    throw "UI 尚未编译: $LpUiExe。请先运行 Build-Lp.ps1。"
}

if (-not (Test-Path -LiteralPath $LpStageDir)) {
    throw "未找到打包目录: $LpStageDir。请先运行 Package-Lp.ps1。"
}

if (-not (Test-Path -LiteralPath $LpIssFile)) {
    throw "安装包脚本不存在: $LpIssFile"
}

# 版本号唯一来源：构建产物上的 ProductVersion（MSBuild 从 Directory.Build.props 注入）。
$LpProductVersion = (Get-Item -LiteralPath $LpUiExe).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($LpProductVersion)) {
    throw "无法从 $LpUiExe 读取 ProductVersion。"
}

$LpProductVersion = $LpProductVersion.Trim()
if ($LpProductVersion -notmatch '^[0-9]+[.][0-9]+[.][0-9]+(-alpha[0-9]+)?$') {
    throw "安装包版本号格式不合法: $LpProductVersion"
}

# 安装器文件版本必须是四段数字；去掉预发布后缀并补 0。
$LpNumericVersion = ($LpProductVersion -split '-')[0]
$LpNumericParts = @($LpNumericVersion.Split('.') | ForEach-Object { [int]$_ })
while ($LpNumericParts.Count -lt 4) {
    $LpNumericParts += 0
}

$LpNumericVersion4 = ($LpNumericParts -join '.')

$LpIscc = $LpIsccPath
if ([string]::IsNullOrWhiteSpace($LpIscc)) {
    $LpIsccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $LpIsccCommand) {
        $LpIscc = $LpIsccCommand.Source
    }
}

if ([string]::IsNullOrWhiteSpace($LpIscc)) {
    $LpCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )
    foreach ($LpCandidate in $LpCandidates) {
        if (Test-Path -LiteralPath $LpCandidate) {
            $LpIscc = $LpCandidate
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($LpIscc) -or -not (Test-Path -LiteralPath $LpIscc)) {
    throw '未找到 ISCC.exe。请安装 Inno Setup 6（CI 用 choco install innosetup），或用 -LpIsccPath 指定。'
}

$LpOutputName = "Pathleap-$LpProductVersion-win-x64-setup"
$LpSetupPath = Join-Path $LpOutputRoot "$LpOutputName.exe"

Write-Host "生成安装包: $LpProductVersion（文件版本 $LpNumericVersion4）" -ForegroundColor Cyan
$LpIsccArgs = @(
    '/Qp',
    "/DAppVersion=$LpProductVersion",
    "/DAppNumericVersion=$LpNumericVersion4",
    "/DSourceDir=$LpStageDir",
    "/DOutputDir=$LpOutputRoot",
    "/DOutputBaseName=$LpOutputName",
    $LpIssFile
)

& $LpIscc @LpIsccArgs
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup 编译失败（exit=$LASTEXITCODE）"
}

if (-not (Test-Path -LiteralPath $LpSetupPath)) {
    throw "安装包未生成: $LpSetupPath"
}

$LpSetupItem = Get-Item -LiteralPath $LpSetupPath
Write-Host ("安装包完成: {0}（{1:0.0} MB）" -f $LpSetupItem.FullName, ($LpSetupItem.Length / 1MB)) -ForegroundColor Green

return $LpSetupPath
