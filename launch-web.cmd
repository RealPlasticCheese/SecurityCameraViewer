@echo off
setlocal
cd /d "%~dp0"
where ffmpeg >nul 2>nul
if errorlevel 1 (
  echo FFmpeg is required. Install FFmpeg and add its bin directory to PATH.
  pause
  exit /b 1
)
if not exist "artifacts\web\SecurityCameraViewer.Web.dll" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-web.ps1"
  if errorlevel 1 (
    pause
    exit /b 1
  )
)
if exist "artifacts\web\SecurityCameraViewer.Web.exe" (
  "artifacts\web\SecurityCameraViewer.Web.exe" %*
) else (
  dotnet "artifacts\web\SecurityCameraViewer.Web.dll" %*
)
if errorlevel 1 pause
