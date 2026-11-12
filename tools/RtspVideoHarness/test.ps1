$ErrorActionPreference = 'Stop'
& dotnet run --project (Join-Path $PSScriptRoot 'tests/RtspVideoHarness.Tests.csproj') -c Release -- @args
exit $LASTEXITCODE
