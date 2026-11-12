#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
dotnet publish src/SecurityCameraViewer.Web/SecurityCameraViewer.Web.csproj -c Release -o artifacts/web "-p:RestoreConfigFile=$PWD/NuGet.Config" --self-contained false "$@"
echo 'Web server built. Run: bash run-web.sh'
