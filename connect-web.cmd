@echo off
setlocal
if "%~1"=="" (
  echo Usage: connect-web.cmd http://server:5080/ [--browser edge^|chrome^|chromium^|brave^|default]
  exit /b 1
)
cd /d "%~dp0"
set "viewer=artifacts\web"
if exist "SecurityCameraViewer.Web.dll" set "viewer=."
if not exist "%viewer%\SecurityCameraViewer.Web.dll" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-web.ps1"
  if errorlevel 1 exit /b 1
)
if exist "%viewer%\SecurityCameraViewer.Web.exe" (
  "%viewer%\SecurityCameraViewer.Web.exe" --connect %*
) else (
  dotnet "%viewer%\SecurityCameraViewer.Web.dll" --connect %*
)
exit /b %errorlevel%
