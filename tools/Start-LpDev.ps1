#Requires -Version 7.0
<#
.SYNOPSIS
  开发期启动：确保服务控制台进程 + UI 常驻进程在运行。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$LpConfiguration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$LpRepoRoot = Split-Path -Parent $PSScriptRoot
$LpUiExe = Join-Path $LpRepoRoot "src\Pathleap.UI\bin\x64\$LpConfiguration\net8.0-windows10.0.22621.0\Pathleap.UI.exe"
$LpServiceExe = Join-Path $LpRepoRoot "src\Pathleap.Service\bin\x64\$LpConfiguration\net8.0-windows\Pathleap.Service.exe"

if (-not (Test-Path -LiteralPath $LpUiExe)) {
    throw "UI 尚未编译: $LpUiExe。请先运行 Build-Lp.ps1。"
}

$LpServiceProcess = Get-Process -Name 'Pathleap.Service' -ErrorAction SilentlyContinue
if ($null -eq $LpServiceProcess) {
    if (Test-Path -LiteralPath $LpServiceExe) {
        Write-Host '启动服务控制台进程 ...' -ForegroundColor Cyan
        Start-Process -FilePath $LpServiceExe -ArgumentList '--console' -WindowStyle Hidden
    }
}
else {
    Write-Host "服务已在运行: PID $($LpServiceProcess.Id)" -ForegroundColor DarkGray
}

$LpUiProcess = Get-Process -Name 'Pathleap.UI' -ErrorAction SilentlyContinue
if ($null -eq $LpUiProcess) {
    Write-Host '启动 UI ...' -ForegroundColor Cyan
    Start-Process -FilePath $LpUiExe
}
else {
    Write-Host "UI 已在运行: PID $($LpUiProcess.Id)，转发显示命令。" -ForegroundColor DarkGray
    Start-Process -FilePath $LpUiExe -ArgumentList '--show' -Wait
}

Write-Host '启动完成。托盘菜单可退出，Ctrl+Alt+Space 可唤出搜索窗。' -ForegroundColor Green
