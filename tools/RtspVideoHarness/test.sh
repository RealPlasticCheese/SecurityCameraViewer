#!/usr/bin/env bash
set -euo pipefail
tool_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec dotnet run --project "$tool_dir/tests/RtspVideoHarness.Tests.csproj" -c Release -- "$@"
