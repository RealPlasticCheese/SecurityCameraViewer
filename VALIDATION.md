# Web application validation

The maintained application is the ASP.NET Core server and browser UI on Windows and Linux. The former WPF/VLC and Avalonia applications, their launch/build scripts and UI test projects have been removed.

## Build and publish

Build the complete solution with the .NET 10 SDK, then publish the server:

```powershell
dotnet build SecurityCameraViewer.slnx -c Release
dotnet publish src/SecurityCameraViewer.Web -c Release -o artifacts/web
```

On Linux, `bash build-web.sh` publishes the same server. FFmpeg is required for media tests and operation. A self-contained publish includes the .NET runtime, but FFmpeg remains separate.

## Automated checks

The test projects are console runners; use `dotnet run`, not just `dotnet test`. From the repository root:

```powershell
dotnet run --project tests/SecurityCameraViewer.Tests -c Release
dotnet run --project tests/SecurityCameraViewer.Web.Tests -c Release -- artifacts/web/SecurityCameraViewer.Web.dll
dotnet run --project tests/SecurityCameraViewer.Recording.Tests -c Release -- artifacts/web/SecurityCameraViewer.Web.dll
dotnet run --project tests/SecurityCameraViewer.Nvr.Tests -c Release
dotnet run --project tests/SecurityCameraViewer.Streaming.Tests -c Release
dotnet run --project tests/SecurityCameraViewer.Integration.Tests -c Release -- artifacts/web/SecurityCameraViewer.Web.dll
node --test tests/*.test.cjs
python tests/test_camera_setup.py artifacts/web/SecurityCameraViewer.Web.dll
python tests/test_connect_web.py
python tests/test_web_service.py
python tests/test_update.py
```

Use `python3` on Linux. The Bash script checks accept `--bash PATH` on Windows with Git Bash. Server integration tests require Python and FFmpeg and use loopback fixtures rather than the real recorder. Browser rendering checks and optional MediaMTX/schema integration checks have additional prerequisites documented in their feature guides.

Core tests cover RTSP authentication/probing, source identities, diagnostic redaction and complete/partial/oversized JPEG output, decoder errors and cancellation. Web tests retain live playback, audio, reconnect/stall behavior, settings migration, rewind and browser launcher coverage. Recording, NVR, streaming and API tests remain separate.

## Target-host smoke checks

After deploying on Windows or Linux, check live viewing, retained pictures/reconnection, audio, rewind, recording playback and browser app launch. On a Linux service host, also check service startup after reboot. Physical camera compatibility, multicast routing, GPU/browser output and a real Linux boot require the target devices; loopback checks do not establish those results.

See [web usage and regression tests](README.md), [camera setup](docs/camera-setup.md), [browser app launching](docs/browser-app.md) and [settings migration](docs/settings.md) for focused checks. Historical server profiling reports remain valid, including reports of the browser's **Native video** playback method.

## Native viewer removal validation - 5 November 2026

- Release solution build: passed with zero warnings and errors. All remaining solution projects target platform-neutral .NET 10.
- Portable, Windows x64 and Linux x64 framework-dependent server publishes: passed on the Windows validation host. A real Linux boot was not tested.
- Core: 71 reported PASS checks; Web: 1,163; Recording: 347; NVR: 62; Streaming: 177. All five console suites completed successfully.
- JavaScript unit tests: 468 passed, no failures or skips.
- Camera setup: 29 checks passed, including independent credentials, ONVIF inspection, configuration changes and restart recovery. Its API-only fixture explicitly enables preview decoding because no browser is connected to request it.
- Connection launcher: 14 tests passed. Service scripts: 20 passed and one platform-specific check skipped. Update scripts: six passed.
- C# whitespace formatting, Git whitespace checks, 54 local documentation links, solution/project references and published dependency checks: passed; no WPF, Avalonia or VLC dependencies remain.
- Broader API integration suite: 43 checks passed before `GET /api/v1/recordings` returned HTTP 500 at `tests/SecurityCameraViewer.Integration.Tests/Program.cs:327`. The same failure reproduced against an unmodified server published from default-branch commit `109b5ec`. It is a pre-existing failure, not a passing integration result.

Physical cameras, real browser/GPU output and optional MediaMTX integration were not exercised in this removal validation.
