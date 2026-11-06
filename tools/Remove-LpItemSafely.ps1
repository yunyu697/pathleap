#Requires -Version 7.0
<#
.SYNOPSIS
  安全删除脚本入口。必须显式提供白名单根目录，默认只允许当前用户的 %LOCALAPPDATA%\Pathleap。
.EXAMPLE
  .\Remove-LpItemSafely.ps1 -LpTargetPath "$env:LOCALAPPDATA\Pathleap\perf\old-run" -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$LpTargetPath,

    [string[]]$LpAllowedRoots = @((Join-Path $env:LOCALAPPDATA 'Pathleap')),

    [switch]$LpAllowFiles,

    [switch]$LpAllowReparsePoint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'Lp.Safety.psm1') -Force

Remove-LpItemSafely @PSBoundParameters
