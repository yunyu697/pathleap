#Requires -Version 7.0
<#
.SYNOPSIS
  编译 M1 骨架（Shared / Service / UI）。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$LpConfiguration = 'Debug',

    [ValidateSet('x64')]
    [string]$LpPlatform = 'x64',

    [switch]$LpSkipVariableCheck,

    # CI 在 tag 构建时注入；留空则读取 Directory.Build.props 里的唯一来源
    [string]$LpVersion = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$LpRepoRoot = Split-Path -Parent $PSScriptRoot

# 版本号单一来源：Directory.Build.props；CI tag 构建可用 -LpVersion 覆盖。
if ([string]::IsNullOrWhiteSpace($LpVersion)) {
    $LpPropsRaw = Get-Content -LiteralPath (Join-Path $LpRepoRoot 'Directory.Build.props') -Raw
    $LpVersionMatch = [regex]::Match($LpPropsRaw, '<PathleapVersion>([^<]+)</PathleapVersion>')
    if (-not $LpVersionMatch.Success) {
        throw 'Directory.Build.props 中缺少 <PathleapVersion>。'
    }

    $LpVersion = $LpVersionMatch.Groups[1].Value.Trim()
}

# 版本规则：major 不设上限；minor / patch 取值 0–10；alpha 编号任意。
$LpVersionMatch = [regex]::Match($LpVersion, '^([0-9]+)\.([0-9]+)\.([0-9]+)(?:-alpha[0-9]+)?$')
if (-not $LpVersionMatch.Success) {
    throw "版本号格式不合法: $LpVersion（期望 1.0.0 或 1.0.0-alpha1）。"
}

$LpMinor = [int]$LpVersionMatch.Groups[2].Value
$LpPatch = [int]$LpVersionMatch.Groups[3].Value
if ($LpMinor -gt 10 -or $LpPatch -gt 10) {
    throw "版本号越界: $LpVersion（minor / patch 必须在 0–10，超过时应向前进位）。"
}

Write-Host "版本号: $LpVersion" -ForegroundColor DarkGray

if (-not $LpSkipVariableCheck) {
    & (Join-Path $PSScriptRoot 'Test-LpVariableNames.ps1') -LpScriptPath (Join-Path $PSScriptRoot '.')
}

$LpProjects = @(
    (Join-Path $LpRepoRoot 'src\Pathleap.Shared\Pathleap.Shared.csproj'),
    (Join-Path $LpRepoRoot 'src\Pathleap.Service\Pathleap.Service.csproj'),
    (Join-Path $LpRepoRoot 'src\Pathleap.UI\Pathleap.UI.csproj')
)

foreach ($LpProject in $LpProjects) {
    if (-not (Test-Path -LiteralPath $LpProject)) {
        throw "项目文件不存在: $LpProject"
    }

    Write-Host "编译 $LpProject ..." -ForegroundColor Cyan
    $LpBuildArgs = @(
        'build', $LpProject,
        '--configuration', $LpConfiguration,
        '--property', "Platform=$LpPlatform",
        '--property', "PathleapVersion=$LpVersion",
        '--nologo'
    )
    & dotnet @LpBuildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "编译失败: $LpProject (exit=$LASTEXITCODE)"
    }
}

$LpUiExe = Join-Path $LpRepoRoot "src\Pathleap.UI\bin\$LpPlatform\$LpConfiguration\net8.0-windows10.0.22621.0\Pathleap.UI.exe"
$LpServiceExe = Join-Path $LpRepoRoot "src\Pathleap.Service\bin\$LpPlatform\$LpConfiguration\net8.0-windows\Pathleap.Service.exe"

# 开发布局：把服务进程的文件复制到 UI 输出目录，UI 才能在 AppContext.BaseDirectory 下找到并拉起它。
$LpUiOutputDir = Split-Path -Parent $LpUiExe
$LpServiceOutputDir = Split-Path -Parent $LpServiceExe
$LpServiceArtifacts = @(
    'Pathleap.Service.exe',
    'Pathleap.Service.dll',
    'Pathleap.Service.runtimeconfig.json',
    'Pathleap.Service.deps.json'
)
foreach ($LpArtifact in $LpServiceArtifacts) {
    $LpSource = Join-Path $LpServiceOutputDir $LpArtifact
    if (-not (Test-Path -LiteralPath $LpSource)) {
        throw "服务构建产物缺失: $LpSource"
    }

    Write-Host "  复制 $LpArtifact -> UI 输出目录" -ForegroundColor DarkGray
    Copy-Item -LiteralPath $LpSource -Destination (Join-Path $LpUiOutputDir $LpArtifact) -Force
}

Write-Host '编译完成：' -ForegroundColor Green
Write-Host "  UI      : $LpUiExe"
Write-Host "  Service : $LpServiceExe"
