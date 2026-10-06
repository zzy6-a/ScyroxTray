<#
  ScyroxTray 卸载脚本（Windows / PowerShell 5.1 及以上）

  做四件事：
    [1] 停掉安装目录里的实例
    [2] 删掉 HKCU 的 Run 项（开机自启）
    [3] 删掉桌面快捷方式
    [4] 删掉安装目录
  可选：
    -PurgeSettings  同时删掉程序设置（HKCU\Software\ScyroxTray）与崩溃日志

  用法：
    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -PurgeSettings
    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Dir "D:\Tools\ScyroxTray"
    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [string]$Dir,
  [switch]$PurgeSettings,
  [switch]$ForceRunKey
)

$ErrorActionPreference = 'Stop'

$appName = 'ScyroxTray'
$runKey  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$settingsKey = 'HKCU:\Software\ScyroxTray'
$exeName = 'ScyroxTray.exe'

function Write-Step { param([string]$m) Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok   { param([string]$m) Write-Host "    [OK] $m" -ForegroundColor Green }
function Write-Warn { param([string]$m) Write-Host "    [!] $m" -ForegroundColor Yellow }

if (-not $Dir) { $Dir = Join-Path $env:LOCALAPPDATA "Programs\$appName" }
$targetExe = Join-Path $Dir "bin\$exeName"

Write-Host ''
Write-Host "  $appName 卸载" -ForegroundColor Cyan
Write-Host '  ====================' -ForegroundColor Cyan
Write-Ok "安装目录 = $Dir"

# ---------- [1] 停进程 ----------
Write-Step '停止实例'
$killed = 0
foreach ($proc in @(Get-Process -Name $appName -ErrorAction SilentlyContinue)) {
  $procPath = $null
  try { $procPath = $proc.Path } catch { }
  if ($procPath -and ($procPath -eq $targetExe)) {
    if ($PSCmdlet.ShouldProcess("$($proc.Id) $procPath", '停止进程')) {
      try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; $killed++ } catch { Write-Warn "停止失败：$($_.Exception.Message)" }
    }
  } else {
    Write-Warn "PID $($proc.Id) 运行在别处（$procPath），不动它"
  }
}
if ($killed -gt 0) { Write-Ok "已停止 $killed 个实例" } else { Write-Ok '没有需要停止的实例' }

# ---------- [2] Run 项 ----------
Write-Step '开机自启'
try {
  $existing = (Get-ItemProperty -Path $runKey -Name $appName -ErrorAction SilentlyContinue).$appName
  if ($existing) {
    # 只删指向**这个安装目录**的 Run 项：别处的另一份安装不该被这个脚本牵连。
    $oldExe = $existing.Trim('"')
    if ($oldExe -eq $targetExe -or $ForceRunKey) {
      if ($PSCmdlet.ShouldProcess("$runKey\$appName", '删除')) {
        Remove-ItemProperty -Path $runKey -Name $appName -ErrorAction SilentlyContinue
        Write-Ok 'Run 项已删除'
      }
    } else {
      Write-Warn "Run 项指向另一份安装（$existing），保留未删。要强删加 -ForceRunKey"
    }
  } else {
    Write-Ok '本来就没有 Run 项'
  }
} catch { Write-Warn "处理 Run 项失败：$($_.Exception.Message)" }

# ---------- [3] 快捷方式 ----------
Write-Step '桌面快捷方式'
$lnkPath = Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk"
if (Test-Path $lnkPath) {
  if ($PSCmdlet.ShouldProcess($lnkPath, '删除')) {
    Remove-Item $lnkPath -Force -ErrorAction SilentlyContinue
    Write-Ok '已删除'
  }
} else {
  Write-Ok '没有快捷方式'
}

# ---------- [4] 安装目录 ----------
Write-Step '安装目录'
if (Test-Path $Dir) {
  if ($PSCmdlet.ShouldProcess($Dir, '删除目录')) {
    Remove-Item -LiteralPath $Dir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $Dir) { Write-Warn "删不掉（可能还有文件被占用）：$Dir" } else { Write-Ok "已删除 $Dir" }
  }
} else {
  Write-Ok '目录不存在'
}

# ---------- 设置 ----------
Write-Step '程序设置'
if ($PurgeSettings) {
  if (Test-Path $settingsKey) {
    if ($PSCmdlet.ShouldProcess($settingsKey, '删除注册表项')) {
      Remove-Item -Path $settingsKey -Recurse -Force -ErrorAction SilentlyContinue
      Write-Ok '已删除 HKCU\Software\ScyroxTray'
    }
  } else { Write-Ok '注册表项不存在' }

  $logDir = Join-Path $env:LOCALAPPDATA $appName
  if (Test-Path $logDir) {
    if ($PSCmdlet.ShouldProcess($logDir, '删除日志目录')) {
      Remove-Item -LiteralPath $logDir -Recurse -Force -ErrorAction SilentlyContinue
      Write-Ok "已删除 $logDir"
    }
  }
} else {
  Write-Warn '保留程序设置与日志（加 -PurgeSettings 可一并删除）'
}

Write-Host ''
Write-Host '  卸载完成。' -ForegroundColor Green
Write-Host ''
