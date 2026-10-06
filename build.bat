@echo off
rem ============================================================
rem  ScyroxTray 一键编译（使用系统自带 .NET Framework 4.8 的 csc）
rem  产物：bin\ScyroxTray.exe
rem ============================================================
setlocal
set ROOT=%~dp0
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [ERROR] 找不到 csc.exe，请安装 .NET Framework 4.x
  exit /b 1
)

if not exist "%ROOT%bin" mkdir "%ROOT%bin"

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
  /win32manifest:"%ROOT%res\app.manifest" ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  /out:"%ROOT%bin\ScyroxTray.exe" ^
  "%ROOT%src\Hid.cs" "%ROOT%src\ScyroxProtocol.cs" "%ROOT%src\ScyroxV2.cs" "%ROOT%src\BatteryService.cs" ^
  "%ROOT%src\TrayApp.cs" "%ROOT%src\Program.cs"

if errorlevel 1 (
  echo [ERROR] 编译失败
  exit /b 1
)
echo [OK] 已生成 %ROOT%bin\ScyroxTray.exe
endlocal
