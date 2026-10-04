@echo off
rem GPU_PC_MGT - build the native monitor (in-box .NET Framework compiler only).
rem All temporary files go to .tmp\ inside this folder so the full system drive is not touched.
setlocal
cd /d "%~dp0"
if not exist ".tmp" mkdir ".tmp"
set "TEMP=%~dp0.tmp"
set "TMP=%~dp0.tmp"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [x] .NET Framework compiler not found: %CSC%
  exit /b 1
)

echo [1/4] compiling ssh-askpass.exe ...
"%CSC%" /nologo /platform:x64 /optimize+ /target:exe /out:ssh-askpass.exe src\askpass.cs
if errorlevel 1 exit /b 1

echo [2/4] compiling GPUPCMonitor.exe (local viewer) ...
"%CSC%" /nologo /platform:x64 /optimize+ /target:winexe ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.Windows.Forms.DataVisualization.dll ^
  /resource:src\remote-sampler.ps1,Gpm.remote-sampler.ps1 ^
  /win32icon:assets\monitor.ico ^
  /resource:assets\monitor.ico,Gpm.monitor.ico /resource:assets\monitor-warning.ico,Gpm.monitor-warning.ico ^
  /out:GPUPCMonitor.exe ^
  src\Model.cs src\Native.cs src\LocalSampler.cs src\RemoteSampler.cs src\Config.cs src\Ui.cs src\DashboardPanel.cs src\FaultHistory.cs src\FaultHistoryForm.cs src\WidgetForm.cs src\Program.cs src\SettingsForm.cs src\TrayIcon.cs src\TrayUi.cs src\ProbeConfig.cs
if errorlevel 1 exit /b 1

rem The workspace may carry an inherited Low integrity label. The desktop
rem viewer must run at Medium integrity to register its notification icon.
icacls "%~dp0GPUPCMonitor.exe" /setintegritylevel M >nul
if errorlevel 1 (
  echo [x] Cannot set viewer integrity to Medium. Run build.cmd from a normal Windows terminal.
  exit /b 1
)

echo [3/4] compiling gpm-remote.exe (runs ON the monitored machine) ...
"%CSC%" /nologo /platform:x64 /optimize+ /target:exe /out:gpm-remote.exe ^
  /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.Windows.Forms.DataVisualization.dll /win32icon:assets\probe.ico src\ProbeConfig.cs src\RemoteAgent.cs src\Model.cs src\Native.cs src\LocalSampler.cs
if errorlevel 1 exit /b 1

echo [4/4] compiling gpm-probe-tray.exe (signed-in user's status icon) ...
"%CSC%" /nologo /platform:x64 /optimize+ /target:winexe /out:gpm-probe-tray.exe ^
  /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.Windows.Forms.DataVisualization.dll /r:System.ServiceProcess.dll ^
  /win32icon:assets\probe.ico ^
  /resource:assets\probe.ico,Gpm.probe.ico /resource:assets\probe-warning.ico,Gpm.probe-warning.ico ^
  src\ProbeTray.cs src\ProbeSettingsForm.cs src\TrayIcon.cs src\TrayUi.cs src\ProbeConfig.cs src\Config.cs src\Model.cs
if errorlevel 1 exit /b 1
icacls "%~dp0gpm-probe-tray.exe" /setintegritylevel M >nul
if errorlevel 1 exit /b 1

echo.
echo Build OK.
