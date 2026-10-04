@echo off
rem GPU_PC_MGT - launch the native monitor.
setlocal
cd /d "%~dp0"
if not exist "config.json" if exist "config.example.json" copy /y "config.example.json" "config.json" >nul
if not exist "GPUPCMonitor.exe" (
  echo Not built yet. Run build.cmd first.
  pause
  exit /b 1
)
start "" "%~dp0GPUPCMonitor.exe"
