# Profiling the web server

Use a Release build and repeatable footage to identify expensive operations before choosing a C# optimization or a Rust library. The capture scripts attach to an already running server; they do not change settings, start cameras, or stop the server. All capture output defaults to the ignored `artifacts/profiling/` directory.



## Install the capture tools

From the repository root, install the versions validated with this workflow using the .NET SDK:

```text
dotnet tool install dotnet-trace --version 10.0.750501 --tool-path artifacts/profiling-tools --configfile NuGet.Config
dotnet tool install dotnet-counters --version 10.0.750501 --tool-path artifacts/profiling-tools --configfile NuGet.Config
```

Tools remain local to the repository. Alternatively, supply an installed executable with `-ToolPath` on Windows or `--tool-path` on Linux. Older versions can lack the duration option or these trace profiles.

Build with `build-web.ps1` or `bash build-web.sh`, then start the server normally. Use the capture tool's process list to find its PID:

```powershell
artifacts/profiling-tools/dotnet-trace.exe ps
```

```bash
artifacts/profiling-tools/dotnet-trace ps
```

Choose the web server process, rather than a build process or the separate RTSP harness. Run collectors as the server's user; on Linux use the same `TMPDIR` as the server if one was set. Record the application commit, host CPU, camera count, stream resolution, configured detection and AI frame rates, model/device, browser count, and enabled recording/audio/rewind features alongside the captures.

## Collect stages, stacks, and allocations

Replace `1234` with the server PID. Run these modes separately against the same workload to reduce collector interference:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File profile-web.ps1 -ProcessId 1234 -Mode metrics -DurationSeconds 60 -OutputDirectory artifacts/profiling/baseline
powershell -NoProfile -ExecutionPolicy Bypass -File profile-web.ps1 -ProcessId 1234 -Mode trace -DurationSeconds 60 -OutputDirectory artifacts/profiling/baseline
powershell -NoProfile -ExecutionPolicy Bypass -File profile-web.ps1 -ProcessId 1234 -Mode allocations -DurationSeconds 60 -OutputDirectory artifacts/profiling/baseline
```

```bash
bash profile-web.sh --process-id 1234 --mode metrics --duration-seconds 60 --output-directory artifacts/profiling/baseline
bash profile-web.sh --process-id 1234 --mode trace --duration-seconds 60 --output-directory artifacts/profiling/baseline
bash profile-web.sh --process-id 1234 --mode allocations --duration-seconds 60 --output-directory artifacts/profiling/baseline
```

Each mode saves its capture and `capture-MODE.txt` with tool version, duration, process ID, resource budgets, and exit status. Existing capture files are refused; use a fresh directory for each repeat. A nonzero collector exit is propagated by the wrapper. Interrupted or failed captures may be incomplete; inspect exit status and collector output.

| Mode | Output | Purpose |
| --- | --- | --- |
| `metrics` | `metrics.csv` | Runtime counters and application stage-duration percentiles, operation rates, and frame rates |
| `trace` | `trace.nettrace` | Sampled managed stacks with runtime events; locate frequently executing or waiting methods |
| `allocations` | `allocations.nettrace` | GC activity and sampled allocations; locate allocation pressure |

The default 60-second duration is a starting point, not a required measurement period. Configure it for the workload. Metrics collection defaults to 64 histogram series because the tool's default of 10 can omit stages once runtime and application series are combined. Adjust this memory budget with `-MaxHistograms` / `--max-histograms` if the collector reports omissions. The trace buffer defaults to the tool's 256 MB; adjust `-TraceBufferMegabytes` / `--trace-buffer-megabytes` to available memory, checking for lost events if reduced.

For an interactive metrics view, run the corresponding executable with:

```text
dotnet-counters monitor --process-id 1234 --counters System.Runtime,SecurityCameraViewer.Processing --maxHistograms 64
```

## Application stage measurements

The `SecurityCameraViewer.Processing` meter is enabled by a subscribing diagnostic collector or another metric listener. With no subscriber, duration scopes do not read the clock or allocate per operation. No per-frame log messages are written. Labels contain fixed stage names; they exclude camera names, credentials, and stream URLs.

The same meter now includes native FFmpeg resources. The existing capture command collects them automatically after deploying the live-media performance change:

| Instrument | Meaning |
| --- | --- |
| `scv.ffmpeg.cpu.time` | Cumulative child CPU seconds; collectors normally display its interval rate, in CPU cores |
| `scv.ffmpeg.working_set` | Active child resident memory, in bytes |
| `scv.ffmpeg.processes` | Active child count |

Series have a fixed `pipeline` label (`preview`, `analysis`, `recording`, `live.copy`, `live.transcode`, or `other`) and numeric `channel`. They contain no URLs, camera names, process arguments or passwords. CPU totals accumulate across observed child restarts; memory and counts fall when capture stops. Process exit can race a resource query, so a child's last observed CPU total is retained if its final OS sample is unavailable. No process resource queries run without a subscribing collector; a child that starts and exits entirely while collection is detached cannot have its CPU recovered later. An hour-long CSV capture is suitable for later analysis; send `metrics.csv` and `capture-metrics.txt`, along with the server commit, active camera/viewer count and times when the workload changed.

For CPU rates, 1.0 CPU second per second represents one fully occupied logical core, equivalent to 100% in Linux's per-process convention. Divide by the host's logical core count for its share of total CPU capacity. Ignore the collector's initial interval when comparing steady workloads. The .NET process CPU counter measures the server; the FFmpeg instruments measure its tracked native children. Neither includes Chrome, the RTSP test harness or unrelated processes. A capture from a build predating these instruments cannot retrospectively reveal native child CPU usage.

JPEG previews run while `/api/video` consumers are connected. Closing the final browser releases the preview decoders; a first snapshot briefly acquires preview demand, and the last picture remains available afterward. A hidden browser tab can still hold its socket open. Recording and motion analysis remain independent. Enabled rewind still captures live media without a browser, but compatible H.264 is copied into fragmented MP4 rather than decoded and encoded again.

`scv.processing.duration` records elapsed milliseconds. `scv.processing.operations` counts finished scopes, including exceptions and cancellation. Neither instrument distinguishes success from failure. `scv.frames` counts frames at pipeline stages. The `stage` label identifies:

| Stages | Meaning |
| --- | --- |
| `ai.wait` | Waiting to enter the shared AI lane, including cancelled waits |
| `ai.prepare`, `ai.decode` | Model input preparation and model output decoding |
| `ai.session` | Session lookup or cold model loading; failure here does not imply native inference ran |
| `ai.inference` | Model session execution, including the binding's output conversion/copying |
| `image.grayscale`, `image.resize` | RGB conversion and per-template downsampling; bypassed conversions are not timed |
| `motion.total` | Adaptive analysis after grayscale conversion, including nested stages and policy evaluation |
| `motion.smooth`, `motion.illumination`, `motion.background` | Optional smoothing, lighting estimation, and background/noise/shadow updates |
| `motion.persistence`, `motion.regions` | Temporal region qualification and displayed connected-region extraction |
| `jpeg.parse_chunk` | Parsing and synchronous publication of a received chunk, excluding input waits; this is not per-frame latency |
| `media.fragment_copy` | Allocating and combining a fragmented MP4 payload, excluding input/output waits |
| `analysis.captured`, `analysis.dequeued`, `analysis.dropped` | Analysis frames read from FFmpeg, taken for analysis, or replaced in the latest-frame queue |
| `jpeg.completed` | Complete JPEG frames successfully published |

Stage measurements aggregate across cameras/templates/models. Parent durations include nested stages, so do not add all durations together. Per-stage percentiles describe that collector's reporting interval, not necessarily the complete recording session. Frame counters describe the observed interval; frames queued at attachment/detachment or abandoned during shutdown can prevent exact captured-minus-dequeued-minus-dropped accounting. A dequeued frame may fail during analysis.

The existing browser AI time combines preparation, model setup/execution, and decoding, and excludes the shared-lane wait. Use the separate metrics to distinguish those costs. Stopwatch measurements and `trace`'s `dotnet-sampled-thread-time` profile are elapsed-time evidence, not exact CPU percentages. Use CPU profiling to distinguish calculation from blocked threads.

## Include companion processes and browser cost

FFmpeg and MediaMTX run outside the web-server process. Measure their CPU and resident memory separately; a managed trace does not include their work. On Windows, Task Manager's Details view or a PerfView/WPR CPU capture can identify the individual web server and companion processes. On Linux, identify the relevant PIDs with `ps` and use, for example, `pidstat -u -r -p PID1,PID2,PID3 1` if installed. Exclude harness publishers from the server cost, or run the harness on a separate machine. Record GPU utilization when using GPU inference or browser enhancements.

For native and managed CPU stacks on Linux, the tool's `collect-linux` mode uses OS profiling, requires root and kernel 6.4 or newer, and is separate from these unprivileged wrappers. On Windows, use an ETW CPU profiler such as PerfView. If client rendering is the concern, collect a browser performance profile on the viewing device; server metrics do not measure WebGL processing or browser decoding.

## Repeatable comparison

Use the [RTSP video harness](../tools/RtspVideoHarness/README.md) to replay the same folder with fixed resolution and frame rate. Allow JIT compilation, camera startup, motion warmup, and model loading to finish before steady-state capture; measure cold startup separately. Capture viewing alone, adaptive detection, AI detection, and recording/audio/rewind as separate scenarios, then the actual combined deployment workload. Repeat each scenario and preserve identical settings, footage, and browser load for before/after comparisons.

Inspect traces in PerfView or another compatible viewer. For a quick managed-stack ranking, use `dotnet-trace report PATH/trace.nettrace topN --inclusive`; rankings include nested calls and waiting time. Allocation samples are estimates rather than an exact count of every object. Select an expensive stage supported by both CPU and allocation evidence, then benchmark current C#, optimized C#, and any Rust prototype using identical inputs. Include interop, buffer transfer, and result conversion in the Rust measurement. Repeat the application workload to establish the actual CPU/memory benefit and whether delivered frame rates or dropped-frame rates improved.

References: [dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace), [dotnet-counters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters), [creating metrics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation).

## Validation

```text
dotnet run --project tests/SecurityCameraViewer.Tests -c Release -- --profiling-only
dotnet run --project tests/SecurityCameraViewer.Recording.Tests -c Release -- --profiling-only
python tests/test_profiling_scripts.py
```

On Windows, pass `--bash "C:/Program Files/Git/bin/bash.exe"` to the Python test if needed. Wrapper tests use fake tools and do not attach to the server. Test against a running server separately to verify real diagnostic attachment and capture output on the target host.

For an opt-in integration check using isolated settings, local RTSP footage, and a published server:

```text
python tests/test_profiling_capture.py --assembly artifacts/web/SecurityCameraViewer.Web.dll
```

This requires FFmpeg and the installed diagnostic tools. It verifies all three capture modes, real JPEG/adaptive stage metrics, and continued video delivery, then stops only its own fixture processes and removes its temporary data. Pass `--bash PATH` to exercise the Bash wrapper on Windows with the native diagnostic executables.

## Measured processing changes
