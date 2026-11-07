#Requires -Version 7.0
<#
.SYNOPSIS
  USN 主路径的一次性提权验证（必须以管理员身份运行）。
.DESCRIPTION
  产品路径：管理员身份启动服务 → IPC INDEX_BUILD(mode=usn, root=C:\) → 子进程用 FSCTL_ENUM_USN_DATA 枚举整卷。
  本脚本只做验证，不常驻、不改系统设置；结束时关闭自己启动的服务进程，结果写入 perf\usn-elevated-result.json。
  非管理员运行会直接退出码 3，不会降级成遍历。
#>
[CmdletBinding()]
param(
    [string]$LpServiceExe = '',
    [string]$LpRoot = 'C:\',
    [string]$LpOutputJson = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$LpPerfDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'perf'
if (-not (Test-Path -LiteralPath $LpPerfDir)) {
    New-Item -ItemType Directory -Path $LpPerfDir -Force | Out-Null
}

# 提权窗口一闪而过时保留现场：错误写文件，控制台全量转录。
$LpErrorFile = Join-Path $LpPerfDir 'usn-elevated-error.txt'
trap {
    ($_ | Out-String) | Set-Content -LiteralPath $LpErrorFile -Encoding utf8
    exit 1
}

Start-Transcript -Path (Join-Path $LpPerfDir 'usn-elevated-console.txt') -Force | Out-Null

$LpRepoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($LpServiceExe)) {
    $LpServiceExe = Join-Path $LpRepoRoot 'src\Pathleap.Service\bin\x64\Release\net8.0-windows\Pathleap.Service.exe'
}

if ([string]::IsNullOrWhiteSpace($LpOutputJson)) {
    $LpOutputJson = Join-Path $LpRepoRoot 'perf\usn-elevated-result.json'
}

function Test-LpAdmin {
    $LpIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $LpPrincipal = [System.Security.Principal.WindowsPrincipal]::new($LpIdentity)
    return $LpPrincipal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-LpAdmin)) {
    Write-Host '需要管理员身份：请通过 Start-Process -Verb RunAs 启动本脚本。' -ForegroundColor Red
    exit 3
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class LpVolumeProbe
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}
'@

function Invoke-LpServiceRequest {
    param(
        [Parameter(Mandatory)] [hashtable]$LpPayload,
        [int]$LpTimeoutMs = 5000
    )

    $LpClient = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'Pathleap.Service', [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $LpClient.Connect(3000)
        $LpWriter = [System.IO.StreamWriter]::new($LpClient, [System.Text.Encoding]::UTF8, 1024, $true)
        $LpWriter.WriteLine(($LpPayload | ConvertTo-Json -Compress))
        $LpWriter.Flush()
        $LpReader = [System.IO.StreamReader]::new($LpClient, [System.Text.Encoding]::UTF8, $false, 1024, $true)

        $LpTask = $LpReader.ReadLineAsync()
        $LpDeadline = [datetime]::UtcNow.AddMilliseconds($LpTimeoutMs)
        while (-not $LpTask.IsCompleted -and [datetime]::UtcNow -lt $LpDeadline) {
            Start-Sleep -Milliseconds 20
        }

        if (-not $LpTask.IsCompleted) {
            throw "等待服务响应超时（$LpTimeoutMs ms）"
        }

        $LpLine = $LpTask.GetAwaiter().GetResult()
        if ([string]::IsNullOrWhiteSpace($LpLine)) {
            throw '服务返回空响应'
        }

        return $LpLine | ConvertFrom-Json
    }
    finally {
        $LpClient.Dispose()
    }
}

# 0. 记一条提权证据：管理员能打开卷句柄，非管理员会拿到 5（拒绝访问）。
# 注意：GENERIC_READ = 0x80000000 在 PowerShell 里是 Int32 负数，必须先用 Int64 再转 UInt32。
$LpGenericRead = [uint32]2147483648
$LpVolumeHandle = [LpVolumeProbe]::CreateFileW('\\.\C:', $LpGenericRead, [uint32]3, [IntPtr]::Zero, [uint32]3, [uint32]0, [IntPtr]::Zero)
$LpVolumeError = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
$LpVolumeOpenOk = $LpVolumeHandle -ne [IntPtr]::Zero
if ($LpVolumeOpenOk) {
    [LpVolumeProbe]::CloseHandle($LpVolumeHandle) | Out-Null
}

Write-Host ("管理员身份: True；卷句柄打开: {0}（Win32 错误码 {1}）" -f $LpVolumeOpenOk, $LpVolumeError) -ForegroundColor Cyan

# 1. 清掉非管理员服务实例，换成当前提权身份启动。
Get-Process -Name 'Pathleap.Service' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$LpServiceProcess = Start-Process -FilePath $LpServiceExe -ArgumentList '--console' -PassThru -WindowStyle Hidden
$LpReady = $false
for ($LpI = 0; $LpI -lt 60; $LpI++) {
    try {
        $LpPing = Invoke-LpServiceRequest @{ command = 'PING' } -LpTimeoutMs 1000
        if ($LpPing.ok) { $LpReady = $true; break }
    }
    catch {
        # 服务仍在启动。
    }

    Start-Sleep -Milliseconds 500
}

if (-not $LpReady) {
    throw '提权服务启动后 30 秒内未响应 PING。'
}

# 2. 产品路径：IPC 触发 USN 全量构建。
$LpWatch = [System.Diagnostics.Stopwatch]::StartNew()
$LpBuildReply = Invoke-LpServiceRequest @{ command = 'INDEX_BUILD'; mode = 'usn'; root = $LpRoot } -LpTimeoutMs 900000
$LpWatch.Stop()

$LpStatus = Invoke-LpServiceRequest @{ command = 'INDEX_STATUS' }
$LpServiceInfo = Get-Process -Id $LpServiceProcess.Id

$LpSummary = [pscustomobject]@{
    generatedAt      = (Get-Date).ToString('o')
    isAdmin          = $true
    volumeOpenOk     = $LpVolumeOpenOk
    volumeOpenError  = $LpVolumeError
    root             = $LpRoot
    buildOk          = [bool]$LpBuildReply.ok
    buildMessage     = $LpBuildReply.message
    entryCount       = if ($LpBuildReply.ok) { $LpBuildReply.index.entryCount } else { 0 }
    buildMs          = if ($LpBuildReply.ok) { $LpBuildReply.index.buildMs } else { 0 }
    elapsedMs        = [math]::Round($LpWatch.Elapsed.TotalMilliseconds, 1)
    fileBytes        = if ($LpBuildReply.ok) { $LpBuildReply.index.fileBytes } else { 0 }
    source           = if ($LpBuildReply.ok) { $LpBuildReply.index.source } else { '' }
    indexState       = $LpStatus.index
    servicePeakWsMB  = [math]::Round($LpServiceInfo.PeakWorkingSet64 / 1MB, 1)
    serviceWsMB      = [math]::Round($LpServiceInfo.WorkingSet64 / 1MB, 1)
    servicePrivateMB = [math]::Round($LpServiceInfo.PrivateMemorySize64 / 1MB, 1)
}

$LpOutputDirectory = Split-Path -Parent $LpOutputJson
if ($LpOutputDirectory -and -not (Test-Path -LiteralPath $LpOutputDirectory)) {
    New-Item -ItemType Directory -Path $LpOutputDirectory -Force | Out-Null
}

$LpSummary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $LpOutputJson -Encoding utf8
$LpSummary | Format-List

# 3. 收尾：只停本脚本启动的服务，不留常驻提权进程。
if (-not $LpServiceProcess.HasExited) {
    $LpServiceProcess.Kill()
}

Write-Host "结果已写入: $LpOutputJson" -ForegroundColor Green
Stop-Transcript | Out-Null
