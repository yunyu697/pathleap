#Requires -Version 7.0
<#
.SYNOPSIS
  安全删除模块。所有删除/清理操作必须通过本模块。
.DESCRIPTION
  约束（全局工程规则）：
    - 只允许删除白名单根目录之下的路径；
    - 拒绝通配符、相对路径、根目录本身和深度不足的路径；
    - 拒绝 junction / symlink 等重解析点，避免沿链接删到白名单之外；
    - 打印解析后的绝对路径、子项数量与大小；
    - 支持 -WhatIf 干跑；
    - 禁止对被拼接出来的路径直接递归删除，必须先经过白名单校验。
#>
Set-StrictMode -Version Latest

function Remove-LpItemSafely {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$LpTargetPath,

        [Parameter(Mandatory = $true)]
        [string[]]$LpAllowedRoots,

        [switch]$LpAllowFiles,

        [switch]$LpAllowReparsePoint
    )

    if ([string]::IsNullOrWhiteSpace($LpTargetPath)) {
        throw '目标路径为空。'
    }

    if ($LpTargetPath -match '[*?\[\]]') {
        throw "目标路径包含通配符，拒绝执行: $LpTargetPath"
    }

    if ($LpAllowedRoots.Count -eq 0) {
        throw '必须至少提供一个白名单根目录。'
    }

    $LpFullPath = [System.IO.Path]::GetFullPath($LpTargetPath)
    if (-not (Test-Path -LiteralPath $LpFullPath)) {
        Write-Host "目标不存在，跳过: $LpFullPath" -ForegroundColor Yellow
        return
    }

    $LpItem = Get-Item -LiteralPath $LpFullPath -Force
    if ($LpItem.PSIsContainer -and $LpItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint -and -not $LpAllowReparsePoint) {
        throw "目标是重解析点（junction/symlink），拒绝递归删除: $($LpItem.FullName)"
    }

    $LpResolved = [System.IO.Path]::GetFullPath($LpItem.FullName)

    $LpMatchedRoot = $null
    foreach ($LpRoot in $LpAllowedRoots) {
        $LpRootFull = [System.IO.Path]::GetFullPath($LpRoot)
        if ($LpResolved.Equals($LpRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "拒绝删除白名单根目录本身: $LpResolved"
        }

        $LpRelative = [System.IO.Path]::GetRelativePath($LpRootFull, $LpResolved)
        if ($LpRelative.StartsWith('..', [System.StringComparison]::Ordinal) -or
            [System.IO.Path]::IsPathRooted($LpRelative)) {
            continue
        }

        $LpSegments = $LpRelative -split [System.IO.Path]::DirectorySeparatorChar
        if ($LpSegments.Count -lt 2) {
            throw "路径深度不足（要求至少两级）: $LpResolved"
        }

        $LpMatchedRoot = $LpRootFull
        break
    }

    if ($null -eq $LpMatchedRoot) {
        throw "路径不在白名单范围内，拒绝删除: $LpResolved"
    }

    if (-not $LpItem.PSIsContainer -and -not $LpAllowFiles) {
        throw "目标是文件，当前调用未显式允许删除文件: $LpResolved"
    }

    Write-Host '即将删除（已解析绝对路径）：' -ForegroundColor Yellow
    Write-Host "  目标    : $LpResolved"
    Write-Host "  白名单  : $LpMatchedRoot"
    Write-Host "  类型    : $(if ($LpItem.PSIsContainer) { '目录' } else { '文件' })"
    Write-Host "  属性    : $($LpItem.Attributes)"

    if ($LpItem.PSIsContainer) {
        $LpChildren = @(Get-ChildItem -LiteralPath $LpResolved -Force -ErrorAction SilentlyContinue)
        $LpSize = 0
        $LpMeasure = Get-ChildItem -LiteralPath $LpResolved -Force -Recurse -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum
        if ($null -ne $LpMeasure.Sum) {
            $LpSize = $LpMeasure.Sum
        }

        Write-Host "  直接子项: $($LpChildren.Count)"
        Write-Host ("  递归大小: {0:N2} MB" -f ($LpSize / 1MB))
    }

    if ($PSCmdlet.ShouldProcess($LpResolved, '递归删除')) {
        Remove-Item -LiteralPath $LpResolved -Recurse -Force
        Write-Host "已删除: $LpResolved" -ForegroundColor Green
    }
}

Export-ModuleMember -Function Remove-LpItemSafely
