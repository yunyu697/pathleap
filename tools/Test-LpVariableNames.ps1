#Requires -Version 7.0
<#
.SYNOPSIS
  工程脚本自定义变量命名检查。
.DESCRIPTION
  规则（全局工程约束）：
    1. 所有脚本自定义变量必须使用 Lp 前缀；
    2. 不得把 PowerShell 自动/只读变量当作自定义变量（赋值或参数声明）；
    3. 只读取自动变量（如 $PSCmdlet、$LASTEXITCODE、$PSScriptRoot）是允许的。
  例外：标准序言 `$ErrorActionPreference = 'Stop'` 是 PowerShell 约定用法，不算自定义变量。
.NOTES
  所有变量（含本脚本自身）均使用 Lp 前缀，避免与 PowerShell 内置变量冲突。
#>
[CmdletBinding()]
param(
    [string[]]$LpScriptPath = @((Join-Path $PSScriptRoot '.')),
    [string[]]$LpExtraDenied = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$LpDenied = @(
    '_', 'args', 'input', 'error', 'pwd', 'home', 'host', 'pid',
    'true', 'false', 'null', 'PSItem', 'MyInvocation', 'Matches',
    'LASTEXITCODE', 'PSCommandPath', 'PSScriptRoot', 'PSCmdlet',
    'PSBoundParameters', 'PSDefaultParameterValues', 'ErrorActionPreference',
    'WarningPreference', 'VerbosePreference', 'DebugPreference', 'InformationPreference',
    'ProgressPreference', 'ConfirmPreference', 'WhatIfPreference', 'ExecutionContext',
    'StackTrace', 'Sender', 'Event', 'EventArgs', 'this', 'PSVersionTable'
) + $LpExtraDenied

$LpDeniedSet = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]($LpDenied | ForEach-Object { $_.ToLowerInvariant() })
)

function Test-LpVariableName {
    param(
        [string]$LpName
    )

    if ([string]::IsNullOrWhiteSpace($LpName)) {
        return $null
    }

    $LpClean = $LpName.TrimStart('$')
    if ($LpClean.Contains(':')) {
        # 作用域/环境变量（$env:、$script:）不参与自定义变量命名检查。
        return $null
    }

    if ($LpDeniedSet.Contains($LpClean.ToLowerInvariant())) {
        return "使用了 PowerShell 自动/只读变量名: `$$LpClean"
    }

    if (-not $LpClean.StartsWith('Lp', [System.StringComparison]::Ordinal)) {
        return "自定义变量缺少 Lp 前缀: `$$LpClean"
    }

    return $null
}

function Get-LpDeclaredVariables {
    param(
        [System.Management.Automation.Language.ScriptBlockAst]$LpAst
    )

    $LpResults = [System.Collections.Generic.List[object]]::new()

    # 参数声明
    $LpParamAsts = $LpAst.FindAll(
        { param($LpNode) $LpNode -is [System.Management.Automation.Language.ParameterAst] },
        $true
    )
    foreach ($LpParamAst in $LpParamAsts) {
        $LpResults.Add([pscustomobject]@{
                Name = $LpParamAst.Name.VariablePath.UserPath
                Extent = $LpParamAst.Extent
                Kind = 'param'
            })
    }

    # 赋值目标、foreach 迭代变量、for 初始化变量
    $LpAssignmentAsts = $LpAst.FindAll(
        {
            param($LpNode)
            $LpNode -is [System.Management.Automation.Language.AssignmentStatementAst] -or
            $LpNode -is [System.Management.Automation.Language.ForEachStatementAst] -or
            $LpNode -is [System.Management.Automation.Language.ForStatementAst]
        },
        $true
    )
    foreach ($LpAssignmentAst in $LpAssignmentAsts) {
        if ($LpAssignmentAst -is [System.Management.Automation.Language.AssignmentStatementAst]) {
            $LpLeft = $LpAssignmentAst.Left
            if ($LpLeft -is [System.Management.Automation.Language.VariableExpressionAst]) {
                $LpResults.Add([pscustomobject]@{
                        Name = $LpLeft.VariablePath.UserPath
                        Extent = $LpLeft.Extent
                        Kind = 'assignment'
                        RightText = $LpAssignmentAst.Right.Extent.Text
                    })
            }

            continue
        }

        if ($LpAssignmentAst -is [System.Management.Automation.Language.ForEachStatementAst]) {
            if ($LpAssignmentAst.Variable -is [System.Management.Automation.Language.VariableExpressionAst]) {
                $LpResults.Add([pscustomobject]@{
                        Name = $LpAssignmentAst.Variable.VariablePath.UserPath
                        Extent = $LpAssignmentAst.Variable.Extent
                        Kind = 'foreach'
                    })
            }

            continue
        }

        # for 语句的初始化部分通常是赋值语句，已被上面的 AssignmentStatementAst 覆盖；
        # 这里仅收集显式变量初始化，避免重复。
    }

    return $LpResults
}

$LpFiles = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
foreach ($LpPath in $LpScriptPath) {
    if (-not (Test-Path -LiteralPath $LpPath)) {
        throw "脚本路径不存在: $LpPath"
    }

    $LpItem = Get-Item -LiteralPath $LpPath -Force
    if ($LpItem.PSIsContainer) {
        $LpFound = Get-ChildItem -LiteralPath $LpItem.FullName -Recurse -File -Force -Include '*.ps1', '*.psm1'
        foreach ($LpFile in $LpFound) {
            $LpFiles.Add($LpFile)
        }
    }
    else {
        $LpFiles.Add($LpItem)
    }
}

$LpViolations = [System.Collections.Generic.List[object]]::new()
foreach ($LpFile in $LpFiles) {
    $LpTokens = $null
    $LpErrors = $null
    $LpAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $LpFile.FullName,
        [ref]$LpTokens,
        [ref]$LpErrors
    )

    if ($LpErrors.Count -gt 0) {
        foreach ($LpParseError in $LpErrors) {
            $LpViolations.Add([pscustomobject]@{
                    File = $LpFile.FullName
                    Line = $LpParseError.Extent.StartLineNumber
                    Variable = '<parse-error>'
                    Reason = $LpParseError.Message
                })
        }

        continue
    }

    $LpDeclared = Get-LpDeclaredVariables -LpAst $LpAst
    foreach ($LpDeclaration in $LpDeclared) {
        $LpName = $LpDeclaration.Name
        if ($LpName -eq 'ErrorActionPreference') {
            # 仅允许标准序言写法；其他用途仍视为违规。
            if ($LpDeclaration.Kind -eq 'assignment' -and
                $LpDeclaration.RightText -match "^['`"]Stop['`"]$") {
                continue
            }
        }

        $LpReason = Test-LpVariableName -LpName $LpName
        if ($null -ne $LpReason) {
            $LpViolations.Add([pscustomobject]@{
                    File = $LpFile.FullName
                    Line = $LpDeclaration.Extent.StartLineNumber
                    Variable = $LpName
                    Reason = $LpReason
                })
        }
    }
}

if ($LpViolations.Count -gt 0) {
    Write-Host "变量命名检查未通过（$($LpViolations.Count) 项）：" -ForegroundColor Red
    foreach ($LpViolation in $LpViolations) {
        Write-Host ("  {0}:{1}  {2}  -> {3}" -f $LpViolation.File, $LpViolation.Line, $LpViolation.Variable, $LpViolation.Reason)
    }

    throw "变量命名检查未通过（$($LpViolations.Count) 项）。"
}

Write-Host "变量命名检查通过：$($LpFiles.Count) 个脚本/模块。" -ForegroundColor Green
