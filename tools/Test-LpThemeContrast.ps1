#Requires -Version 7.0
<#
.SYNOPSIS
  校验每套颜色主题在亮/暗两种变体下的对比度。
.DESCRIPTION
  读取 src\Pathleap.UI\Theme\ColorThemes.json（与运行期同一份数据），
  按 WCAG 相对亮度计算：正文/背景、次级文字/背景、按钮文字/按钮底色、状态色/背景。
  不满足阈值的项会逐条列出并以退出码 1 结束。
#>
[CmdletBinding()]
param(
    [string]$LpThemeFile = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Pathleap.UI\Theme\ColorThemes.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-LpChannel {
    param([string]$LpHex)
    $LpRaw = $LpHex.TrimStart('#')
    if ($LpRaw.Length -eq 8) {
        $LpRaw = $LpRaw.Substring(2)
    }

    if ($LpRaw.Length -ne 6) {
        throw "颜色格式不支持: $LpHex"
    }

    return @(
        [Convert]::ToInt32($LpRaw.Substring(0, 2), 16),
        [Convert]::ToInt32($LpRaw.Substring(2, 2), 16),
        [Convert]::ToInt32($LpRaw.Substring(4, 2), 16)
    )
}

function Get-LpLuminance {
    param([string]$LpHex)
    $LpLinear = foreach ($LpValue in (Get-LpChannel $LpHex)) {
        $LpNorm = $LpValue / 255.0
        if ($LpNorm -le 0.03928) {
            $LpNorm / 12.92
        }
        else {
            [Math]::Pow((($LpNorm + 0.055) / 1.055), 2.4)
        }
    }

    return 0.2126 * $LpLinear[0] + 0.7152 * $LpLinear[1] + 0.0722 * $LpLinear[2]
}

function Get-LpContrast {
    param([string]$LpForeground, [string]$LpBackground)
    $LpA = Get-LpLuminance $LpForeground
    $LpB = Get-LpLuminance $LpBackground
    $LpHi = [Math]::Max($LpA, $LpB)
    $LpLo = [Math]::Min($LpA, $LpB)
    return [Math]::Round(($LpHi + 0.05) / ($LpLo + 0.05), 2)
}

$LpDocument = Get-Content -LiteralPath $LpThemeFile -Raw | ConvertFrom-Json
$LpFailures = [System.Collections.Generic.List[string]]::new()
$LpRows = [System.Collections.Generic.List[object]]::new()

foreach ($LpTheme in $LpDocument.themes) {
    foreach ($LpVariantName in @('dark', 'light')) {
        $LpVariant = $LpTheme.$LpVariantName
        $LpLabel = "$($LpTheme.id)/$LpVariantName"

        $LpChecks = @(
            @{ Name = '正文 Tex1/窗口'; Foreground = $LpVariant.text1; Background = $LpVariant.window; Min = 7.0 },
            @{ Name = '正文 Tex1/卡片'; Foreground = $LpVariant.text1; Background = $LpVariant.surface2; Min = 7.0 },
            @{ Name = '正文 Tex1/控件'; Foreground = $LpVariant.text1; Background = $LpVariant.surface3; Min = 7.0 },
            @{ Name = '正文 Tex1/按钮'; Foreground = $LpVariant.text1; Background = $LpVariant.surface4; Min = 7.0 },
            @{ Name = '次级 Tex2/窗口'; Foreground = $LpVariant.text2; Background = $LpVariant.window; Min = 4.5 },
            @{ Name = '次级 Tex2/卡片'; Foreground = $LpVariant.text2; Background = $LpVariant.surface2; Min = 4.5 },
            @{ Name = '次级 Tex2/控件'; Foreground = $LpVariant.text2; Background = $LpVariant.surface3; Min = 4.5 },
            @{ Name = '提示 Tex3/窗口'; Foreground = $LpVariant.text3; Background = $LpVariant.window; Min = 4.5 },
            @{ Name = '提示 Tex3/卡片'; Foreground = $LpVariant.text3; Background = $LpVariant.surface2; Min = 4.5 },
            @{ Name = '弱提示 Tex4/窗口'; Foreground = $LpVariant.text4; Background = $LpVariant.window; Min = 3.0 },
            @{ Name = '按钮字/强调底'; Foreground = $LpVariant.accentInk; Background = $LpVariant.accent; Min = 4.5 },
            @{ Name = '按钮字/悬停底'; Foreground = $LpVariant.accentInk; Background = $LpVariant.accentHover; Min = 4.5 },
            @{ Name = '按钮字/按下底'; Foreground = $LpVariant.accentInk; Background = $LpVariant.accentPressed; Min = 4.5 },
            @{ Name = '强调色/窗口'; Foreground = $LpVariant.accent; Background = $LpVariant.window; Min = 3.0 },
            @{ Name = '成功/窗口'; Foreground = $LpVariant.ok; Background = $LpVariant.window; Min = 4.5 },
            @{ Name = '警告/窗口'; Foreground = $LpVariant.warn; Background = $LpVariant.window; Min = 4.5 },
            @{ Name = '失败/窗口'; Foreground = $LpVariant.danger; Background = $LpVariant.window; Min = 4.5 },
            @{ Name = '标题栏字形/标题栏底'; Foreground = $LpVariant.text2; Background = $LpVariant.caption1; Min = 4.5 },
            @{ Name = '标题栏悬停字形/标题栏底'; Foreground = $LpVariant.text1; Background = $LpVariant.caption1; Min = 4.5 },
            @{ Name = '关闭图标/关闭悬停红'; Foreground = '#FFFFFF'; Background = '#C42B1C'; Min = 4.5 }
        )

        foreach ($LpCheck in $LpChecks) {
            $LpRatio = Get-LpContrast $LpCheck.Foreground $LpCheck.Background
            $LpOk = $LpRatio -ge $LpCheck.Min
            if (-not $LpOk) {
                $LpFailures.Add(("{0} {1}: {2} < {3}" -f $LpLabel, $LpCheck.Name, $LpRatio, $LpCheck.Min))
            }

            $LpRows.Add([pscustomobject]@{
                Theme = $LpTheme.id
                Variant = $LpVariantName
                Check = $LpCheck.Name
                Ratio = $LpRatio
                Min = $LpCheck.Min
                Ok = $LpOk
            })
        }
    }
}

Write-Host ("配色主题: {0} 套（{1} 项检查）" -f $LpDocument.themes.Count, $LpRows.Count) -ForegroundColor Cyan
$LpRows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

if ($LpFailures.Count -gt 0) {
    Write-Host '未通过项:' -ForegroundColor Red
    $LpFailures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host '全部对比度检查通过。' -ForegroundColor Green
exit 0
