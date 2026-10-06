<#
  ScyroxTray 一键安装脚本（Windows / PowerShell 5.1 及以上）

  做五件事：
    [1] 停掉**目标目录里**正在运行的实例（不会碰别处的实例）
    [2] 把整个程序包复制到安装目录（默认 %LOCALAPPDATA%\Programs\ScyroxTray）
    [3] 桌面快捷方式（默认创建，-NoShortcut 可关）
    [4] 开机自启（-Autostart 打开；若注册表里已有旧路径，会自动改指到新位置）
    [5] 启动程序并自检

  用法（在解压出来的目录里）：
    powershell -ExecutionPolicy Bypass -File .\install.ps1
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -Autostart
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -Dir "D:\Tools\ScyroxTray"
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -NoShortcut -NoLaunch
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -WhatIf     # 只看会做什么

  说明：
    - 只写三个地方：安装目录、桌面快捷方式、HKCU 的 Run 项。全部是当前用户范围，不需要管理员。
    - 程序自己的设置（图标样式、刷新间隔等）存在 HKCU\Software\ScyroxTray，本脚本不动它。
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [string]$Dir,
  [switch]$Autostart,
  [switch]$NoShortcut,
  [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$appName  = 'ScyroxTray'
$runKey   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$exeName  = 'ScyroxTray.exe'

function Write-Step { param([string]$m) Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok   { param([string]$m) Write-Host "    [OK] $m" -ForegroundColor Green }
function Write-Warn { param([string]$m) Write-Host "    [!] $m" -ForegroundColor Yellow }
function Write-Err  { param([string]$m) Write-Host "    [X] $m" -ForegroundColor Red }

# ---------- 源目录 ----------
$srcDir = $PSScriptRoot
$srcExe = Join-Path $srcDir "bin\$exeName"
if (-not (Test-Path $srcExe)) {
  Write-Err "找不到 $srcExe —— 脚本必须和 bin\ 目录放在同一层"
  exit 1
}

# ---------- 安装目录 ----------
if (-not $Dir) { $Dir = Join-Path $env:LOCALAPPDATA "Programs\$appName" }
$targetExe = Join-Path $Dir "bin\$exeName"

Write-Host ''
Write-Host "  $appName 安装" -ForegroundColor Cyan
Write-Host '  ====================' -ForegroundColor Cyan
Write-Ok "源目录   = $srcDir"
Write-Ok "安装目录 = $Dir"

$srcIsTarget = $false
if (Test-Path $targetExe) {
  try { $srcIsTarget = ((Resolve-Path $srcExe).Path -eq (Resolve-Path $targetExe).Path) } catch { }
}

# ---------- [1] 停掉目标目录里的实例 ----------
Write-Step '检查正在运行的实例'

$running = @(Get-Process -Name $appName -ErrorAction SilentlyContinue)
$stopped = 0
foreach ($proc in $running) {
  $procPath = $null
  try { $procPath = $proc.Path } catch { }
  # 只停目标目录里的那个：别处的实例（比如你原来的开发目录）不该被这个脚本动
  if ($procPath -and ($procPath -eq $targetExe)) {
    if ($PSCmdlet.ShouldProcess("$($proc.Id) $procPath", '停止进程')) {
      try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; $stopped++ } catch { Write-Warn "停止进程 $($proc.Id) 失败：$($_.Exception.Message)" }
    }
  }
}
if ($stopped -gt 0) { Write-Ok "已停止目标目录里的 $stopped 个实例" }
elseif ($running.Count -gt 0) { Write-Warn "有 $($running.Count) 个实例在别处运行（本脚本不动它们）" }
else { Write-Ok '没有实例在运行' }

# ---------- [2] 复制文件 ----------
Write-Step '复制程序文件'

if ($srcIsTarget) {
  Write-Ok '源目录就是安装目录，跳过复制'
} else {
  if (-not (Test-Path $Dir)) { New-Item -ItemType Directory -Path $Dir -Force | Out-Null }
  $items = @('bin', 'src', 'res', 'docs', 'build.bat', 'README.md', 'install.ps1', 'uninstall.ps1')
  foreach ($item in $items) {
    $from = Join-Path $srcDir $item
    if (Test-Path $from) { Copy-Item $from (Join-Path $Dir $item) -Recurse -Force }
  }
  $copied = (Get-ChildItem $Dir -Recurse -File -ErrorAction SilentlyContinue).Count
  Write-Ok "已复制到 $Dir（$copied 个文件）"
}

if (-not (Test-Path $targetExe)) { Write-Err "复制后仍然找不到 $targetExe"; exit 1 }
$exeSize = [math]::Round((Get-Item $targetExe).Length / 1KB, 1)
Write-Ok "主程序 $targetExe（$exeSize KB）"

# ---------- [3] 桌面快捷方式 ----------
if ($NoShortcut) {
  Write-Step '桌面快捷方式（-NoShortcut，跳过）'
} else {
  Write-Step '桌面快捷方式'
  $lnkPath = Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk"
  if ($PSCmdlet.ShouldProcess($lnkPath, '创建快捷方式')) {
    try {
      $shell = New-Object -ComObject WScript.Shell
      $lnk = $shell.CreateShortcut($lnkPath)
      $lnk.TargetPath = $targetExe
      $lnk.WorkingDirectory = Split-Path $targetExe -Parent
      $lnk.Description = 'SCYROX 鼠标电量托盘'
      $lnk.Save()
      Write-Ok "已创建 $lnkPath"
    } catch {
      Write-Warn "创建快捷方式失败（不影响使用）：$($_.Exception.Message)"
    }
  }
}

# ---------- [4] 开机自启 ----------
Write-Step '开机自启'

$existing = $null
try { $existing = (Get-ItemProperty -Path $runKey -Name $appName -ErrorAction SilentlyContinue).$appName } catch { }

if ($Autostart) {
  if ($PSCmdlet.ShouldProcess("$runKey\$appName", "写入 `"$targetExe`"")) {
    try {
      New-ItemProperty -Path $runKey -Name $appName -Value "`"$targetExe`"" -PropertyType String -Force | Out-Null
      Write-Ok "已设为开机自启：$targetExe"
    } catch {
      Write-Err "写入 Run 项失败：$($_.Exception.Message)"
    }
  }
} elseif ($existing) {
  # 已在自启里。只在**旧位置已经不存在**（说明程序被搬走了）时才改指过来；
  # 旧位置还在就说明那是另一份安装，不该被这个脚本抢走自启。
  $oldExe = $existing.Trim('"')
  if ($oldExe -eq $targetExe) {
    Write-Ok "开机自启已开启且指向正确：$existing"
  } elseif (-not (Test-Path $oldExe)) {
    if ($PSCmdlet.ShouldProcess("$runKey\$appName", "从 $existing 改指到 $targetExe")) {
      try {
        New-ItemProperty -Path $runKey -Name $appName -Value "`"$targetExe`"" -PropertyType String -Force | Out-Null
        Write-Ok "旧位置已不存在，开机自启改指到新位置（原：$existing）"
      } catch {
        Write-Warn "改写 Run 项失败：$($_.Exception.Message)"
      }
    }
  } else {
    Write-Warn "开机自启指向另一份安装（$existing），未改动。想让它指向这里就加 -Autostart"
  }
} else {
  Write-Warn '当前未开启开机自启（想开就加 -Autostart，或在程序右键菜单里勾「开机自启」）'
}

# ---------- [5] 启动 ----------
Write-Step '启动'

if ($NoLaunch) {
  Write-Warn '按 -NoLaunch 跳过启动'
} else {
  Start-Process -FilePath $targetExe -WorkingDirectory (Split-Path $targetExe -Parent)
  Start-Sleep -Seconds 2
  $proc = @(Get-Process -Name $appName -ErrorAction SilentlyContinue)
  if ($proc.Count -gt 0) {
    Write-Ok "已启动（PID $($proc[0].Id)，占用 $([math]::Round($proc[0].WorkingSet64 / 1MB, 1)) MB）"
  } else {
    Write-Err '进程没有起来。看 %LOCALAPPDATA%\ScyroxTray\error.log'
  }
}

# ---------- 完成 ----------
Write-Host ''
Write-Host '  安装完成' -ForegroundColor Green
Write-Host ''
Write-Host "  安装目录：$Dir" -ForegroundColor White
Write-Host '  任务栏托盘（^ 隐藏图标区）会出现一个白色电量数字；左键双击刷新，右键出菜单。' -ForegroundColor White
Write-Host ''
Write-Host '  要卸载：powershell -ExecutionPolicy Bypass -File .\uninstall.ps1' -ForegroundColor Yellow
Write-Host ''
