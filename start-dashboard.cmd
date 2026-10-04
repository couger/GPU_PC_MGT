@echo off
rem GPU_PC_MGT · 一键启动：采集端 + 本地页面
setlocal
cd /d "%~dp0"

set "PS=powershell"
where pwsh >nul 2>nul && set "PS=pwsh"

where node >nul 2>nul
if errorlevel 1 (
  echo [x] 找不到 node.exe，请先安装 Node.js，或直接用浏览器打开 web\index.html（需要自建服务器读取 data.json）
  pause
  exit /b 1
)

echo [1/2] 启动采集端（独立窗口，关掉它即停止采集）...
start "GPU_PC_MGT collector" %PS% -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\collect.ps1"

echo [2/2] 启动本地页面服务...
start "" http://127.0.0.1:8791/

node "%~dp0tools\server.mjs"
