#Requires -Version 7.0
<#
.SYNOPSIS
  通过系统「设置 > 屏幕」的缩放下拉框实时切换显示器缩放（注册表+广播方式在本机实测不生效）。
.DESCRIPTION
  用 UIA 打开 ms-settings:display，找到缩放 ComboBox，展开并选择目标百分比，关闭窗口。
  设置窗偶尔会变成无内容的空壳（内容进程已退），此时自动关掉重开再试一轮。
  调用方自行用 --layout-probe 验证生效。会短暂占用前台。
.EXAMPLE
  pwsh -NoProfile -File tools\Set-LpScaleViaSettings.ps1 -LpPercent 125
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(100, 125, 150, 175, 200, 225, 250)]
    [int]$LpPercent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type -Namespace 'LpSettingsNative' -Name 'Win32' -MemberDefinition @'
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool PostMessageW(System.IntPtr hWnd, uint msg, System.IntPtr wParam, System.IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(System.IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
'@

$LpAutomation = [System.Windows.Automation.AutomationElement]
$LpWindowCond = New-Object System.Windows.Automation.PropertyCondition(
    $LpAutomation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)

function Find-LpSettingsWindow {
    # 设置窗是 UWP 宿主窗口（类 ApplicationFrameWindow，标题「设置」/「Settings」），
    # 内容进程与宿主进程不同，按标题找顶层窗口最可靠。
    $LpWins = $LpAutomation::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $LpWindowCond)
    for ($LpI = 0; $LpI -lt $LpWins.Count; $LpI++) {
        if ($LpWins[$LpI].Current.Name -match '^(设置|Settings)$') { return $LpWins[$LpI] }
    }
    return $null
}

function Wait-LpSettingsWindow {
    param([switch]$LpGone, [int]$LpSec = 15)
    $LpDeadline = (Get-Date).AddSeconds($LpSec)
    while ((Get-Date) -lt $LpDeadline) {
        $LpWin = Find-LpSettingsWindow
        if ($LpGone) { if (-not $LpWin) { return $null } }
        elseif ($LpWin) { return $LpWin }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Get-LpScaleCombo {
    param($LpRootWin)
    # 缩放 ComboBox：控件名含「缩放/Scale」，或子项文本形如 "200%"
    $LpComboCond = New-Object System.Windows.Automation.PropertyCondition(
        $LpAutomation::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
    $LpCombos = $LpRootWin.FindAll([System.Windows.Automation.TreeScope]::Descendants, $LpComboCond)
    for ($LpI = 0; $LpI -lt $LpCombos.Count; $LpI++) {
        $LpCombo = $LpCombos[$LpI]
        if ($LpCombo.Current.Name -match '缩放|Scale') { return $LpCombo }
        $LpChildren = $LpCombo.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                $LpAutomation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
        for ($LpJ = 0; $LpJ -lt $LpChildren.Count; $LpJ++) {
            if ($LpChildren[$LpJ].Current.Name -match '^\d{3}(\.\d+)?%') { return $LpCombo }
        }
    }
    return $null
}

function Wait-LpScaleCombo {
    param($LpRootWin, [int]$LpSec)
    $LpDeadline = (Get-Date).AddSeconds($LpSec)
    while ((Get-Date) -lt $LpDeadline) {
        $LpCombo = Get-LpScaleCombo -LpRootWin $LpRootWin
        if ($LpCombo) { return $LpCombo }
        Start-Sleep -Milliseconds 700
    }
    return $null
}

function Close-LpSettingsWindow {
    $LpWin = Find-LpSettingsWindow
    if ($LpWin) {
        [LpSettingsNative.Win32]::PostMessageW($LpWin.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Wait-LpSettingsWindow -LpGone -LpSec 6 | Out-Null
    }
}

# 打开（或导航到）显示设置页
$LpWin = Wait-LpSettingsWindow -LpSec 1
if (-not $LpWin) {
    Start-Process 'ms-settings:display'
    $LpWin = Wait-LpSettingsWindow -LpSec 15
    if (-not $LpWin) { throw '等不到系统设置窗口' }
}

$LpCombo = Wait-LpScaleCombo -LpRootWin $LpWin -LpSec 10
if (-not $LpCombo) {
    # 空壳窗口（内容进程已退）或页面没导航到位：关掉重开再试一轮
    Write-Host '第一轮没找到缩放下拉框，重开设置窗再试' -ForegroundColor Yellow
    Close-LpSettingsWindow
    Start-Process 'ms-settings:display'
    $LpWin = Wait-LpSettingsWindow -LpSec 15
    if (-not $LpWin) { throw '重开后等不到系统设置窗口' }
    $LpCombo = Wait-LpScaleCombo -LpRootWin $LpWin -LpSec 15
    if (-not $LpCombo) { Close-LpSettingsWindow; throw '找不到缩放下拉框（已重试）' }
}

# 若当前不在显示页（例如设置窗停在主页），强制导航一次再确认
if (-not (Get-LpScaleCombo -LpRootWin (Find-LpSettingsWindow))) {
    Start-Process 'ms-settings:display'
    Start-Sleep -Milliseconds 1200
    $LpCombo = Wait-LpScaleCombo -LpRootWin (Find-LpSettingsWindow) -LpSec 10
    if (-not $LpCombo) { Close-LpSettingsWindow; throw '导航到显示页后仍找不到缩放下拉框' }
}

# 展开下拉：先试 ExpandCollapsePattern，不行就激活窗口 + 把焦点移到 Combo 后 SendKeys 选
$LpExpanded = $false
$LpExpandPattern = $null
try {
    if ($LpCombo.TryGetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$LpExpandPattern)) {
        $LpExpandPattern.Expand()
        $LpExpanded = $true
    }
} catch {
    Write-Host ("ExpandCollapsePattern.Expand() 异常: {0} → fallback 到 SendKeys" -f $_.Exception.Message)
}

if (-not $LpExpanded) {
    # Fallback: 把设置窗激活到前台，用 InvokePattern 点击 Combo（等价于点击展开按钮）
    [void][LpSettingsNative.Win32]::ShowWindow($LpWin.Current.NativeWindowHandle, 9)   # SW_RESTORE
    [void][LpSettingsNative.Win32]::SetForegroundWindow($LpWin.Current.NativeWindowHandle)
    Start-Sleep -Milliseconds 400
    # 用 InvokePattern（Win11 的设置下拉框 InvokePattern 会展开）
    $LpInvokeCombo = $null
    try {
        if ($LpCombo.TryGetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern, [ref]$LpInvokeCombo)) {
            $LpInvokeCombo.Invoke()
            $LpExpanded = $true
        }
    } catch {
        Write-Host ("InvokePattern.Invoke() 也异常: {0} → 试 SendKeys" -f $_.Exception.Message)
    }
}

if (-not $LpExpanded) {
    # 终极 fallback：SendKeys Alt+↓（系统快捷键展开聚焦的下拉框）
    $LpCombo.SetFocus()
    Start-Sleep -Milliseconds 200
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.SendKeys]::SendWait('%{DOWN}')
    Start-Sleep -Milliseconds 800
}

Start-Sleep -Milliseconds 500

# 在弹出层（可能是独立窗口）里找目标百分比项
$LpItemCond = New-Object System.Windows.Automation.PropertyCondition(
    $LpAutomation::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$LpItems = $LpAutomation::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $LpItemCond)
$LpTarget = $null
$LpNames = @()
for ($LpI = 0; $LpI -lt $LpItems.Count; $LpI++) {
    $LpN = $LpItems[$LpI].Current.Name
    $LpNames += $LpN
    if ($LpN -match ('^{0}%' -f $LpPercent)) { $LpTarget = $LpItems[$LpI]; break }
}
if (-not $LpTarget) {
    $LpChildItems = $LpCombo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $LpItemCond)
    for ($LpI = 0; $LpI -lt $LpChildItems.Count; $LpI++) {
        if ($LpChildItems[$LpI].Current.Name -match ('^{0}%' -f $LpPercent)) { $LpTarget = $LpChildItems[$LpI]; break }
    }
}
if (-not $LpTarget) {
    Close-LpSettingsWindow
    throw ("找不到 {0}% 选项；可见项: [{1}]" -f $LpPercent, ($LpNames -join ', '))
}

$LpSelectPattern = $null
if ($LpTarget.TryGetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$LpSelectPattern)) {
    $LpSelectPattern.Select()
}
else {
    $LpInvokePattern = $null
    if (-not $LpTarget.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern, [ref]$LpInvokePattern)) {
        Close-LpSettingsWindow
        throw '目标项既不支持 SelectionItem 也不支持 Invoke'
    }
    $LpInvokePattern.Invoke()
}
Write-Host ("已选择 {0}%" -f $LpPercent)

Start-Sleep -Milliseconds 800
Close-LpSettingsWindow
exit 0
