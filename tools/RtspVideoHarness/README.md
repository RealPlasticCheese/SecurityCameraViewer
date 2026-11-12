# RTSP video test harness

A separate .NET 10 console tool for feeding sample videos or a YouTube video/livestream into Security Camera Viewer's web server. It hosts its own MediaMTX RTSP server and uses FFmpeg to produce a continuous H.264/AAC stream. Video sizes, frame rates and source codecs can differ: every input is fitted into the configured output size, with black padding and a fixed frame rate. Source audio is converted to 48 kHz stereo AAC. Missing or shorter audio and black gaps are filled with silence; audio longer than the video is trimmed. Use `--no-audio` for a video-only camera. Inputs are decoded as needed, without preconverting the folder or loading whole videos into memory.

## Run

Install the **.NET 10 SDK** and **FFmpeg with the libx264 encoder and FFprobe** on PATH. Windows can install FFmpeg with `winget install Gyan.FFmpeg`; Linux distributions commonly provide `ffmpeg` and `dotnet-sdk-10.0` packages. The first run builds the tool and downloads checksum-verified MediaMTX 1.21.1 from its official release. Later folder runs work offline. Alternatively use `--ffmpeg`, `--ffprobe` and `--mediamtx` with your own executables.

From the repository on Windows:

```bat
tools\RtspVideoHarness\run.cmd "D:\Sample videos"
tools\RtspVideoHarness\run.cmd "D:\Sample videos" --order created --blank-ms 2000
tools\RtspVideoHarness\run.cmd "D:\Sample videos" --blank-ms 5000 --offline-gaps --port 8555
```

On Linux:

```bash
bash tools/RtspVideoHarness/run.sh /data/videos
bash tools/RtspVideoHarness/run.sh /data/videos --order created --blank-ms 2000
bash tools/RtspVideoHarness/run.sh /data/videos --blank-ms 5000 --offline-gaps --port 8555
```

The default URL is **rtsp://127.0.0.1:8554/sample**. Add it as a manual RTSP camera in the web viewer; use TCP and leave username/password empty. Playback begins immediately and loops until **Ctrl+C**; all viewers see the same position. `--once` exits after the final file, with no trailing gap. A viewer joining later starts at the current position.

Omitting the folder uses the **caller's current directory**, including on the first run that builds the tool. Relative folder paths also resolve against that directory. Scripts can be invoked by absolute path from any directory. Use `--folder PATH`, or `-- PATH` for a folder name starting with a dash.

## YouTube videos and active livestreams

Pass a quoted YouTube URL in place of the folder, or use `--youtube URL`. Supply one video or active livestream; a watch URL with a playlist parameter plays only that video. Folder sorting/filtering options apply only to folder inputs.

```bat
tools\RtspVideoHarness\run.cmd "https://www.youtube.com/watch?v=VIDEO_ID" --port 8555
tools\RtspVideoHarness\run.cmd --youtube "https://www.youtube.com/@CHANNEL/live" --once
```

```bash
bash tools/RtspVideoHarness/run.sh 'https://www.youtube.com/watch?v=VIDEO_ID' --port 8555
bash tools/RtspVideoHarness/run.sh --youtube 'https://www.youtube.com/@CHANNEL/live' --once
```

The run script installs the latest checksum-verified **[yt-dlp](https://github.com/yt-dlp/yt-dlp)** official binary into the tool’s `artifacts` folder on first YouTube use. It uses this tool-local copy even if an older system version is on PATH. An existing local copy is retained; `--yt-dlp PATH` explicitly selects your own installation and skips automatic installation. Local folder playback does not require yt-dlp. YouTube extraction also requires a supported JavaScript runtime on PATH: **Deno 2.3+ or Node.js 22+**. Deno is enabled by yt-dlp by default; the harness also enables Node.js. See [yt-dlp's JavaScript runtime setup](https://github.com/yt-dlp/yt-dlp/wiki/EJS). Official binaries include the matching JavaScript challenge scripts; a manually installed Python version should include yt-dlp's default dependencies.

You can install a system copy using `sudo apt install yt-dlp` on distributions that package it. Distribution repositories can provide older releases: Ubuntu's `2024.04.09-1`, for example, lacks options used here and has older YouTube extraction code. The default tool-local installation avoids that dependency on the system package version. To deliberately use a current system installation, pass `--yt-dlp /usr/bin/yt-dlp` (or its actual path).

If you see `no such option: --no-plugin-dirs` or `--js-runtimes`, update the local copy and retry without a custom extractor override. From this tool’s folder:

```bash
bash install-ytdlp.sh
bash run.sh 'https://www.youtube.com/watch?v=VIDEO_ID'
```

The installer leaves apt-managed packages unchanged. It is optional if you supply a current extractor yourself; updating only the distro package may still leave you with an old release. [yt-dlp documents package-manager update differences](https://github.com/yt-dlp/yt-dlp/wiki/Installation).

yt-dlp resolves the media URLs; FFmpeg streams directly from them, including separate video and audio tracks and required HTTP headers. **An active livestream plays while it is live**, starting near the current HLS live edge. It does not wait for the broadcast to finish or save a complete video. YouTube buffering, network conditions and decoding add latency. Stop with Ctrl+C; `--once` also stops when the source ends. Without `--once`, a completed video is resolved again and replayed, with the configured black/offline gap. URLs are refreshed on each replay, and extraction can add a startup pause. Gaps occur between completed plays, so a continuously running livestream has no periodic gaps.

The default selects the best video and available audio. Output `--size` does not reduce the downloaded source resolution; use a yt-dlp format selector to control source bandwidth and decoding cost, for example:

```bash
bash tools/RtspVideoHarness/run.sh 'https://www.youtube.com/watch?v=VIDEO_ID' --youtube-format 'bv*[height<=720]+ba/bv*[height<=720]' --size 1280x720
```

Upcoming broadcasts, unavailable/restricted videos, extractor failures and network errors stop with diagnostics. Increase `--timeout-seconds` if URL extraction needs more than the default 30 seconds. If YouTube changes its extraction requirements, update the bundled binary by running `install-ytdlp.ps1` or `bash install-ytdlp.sh` again. These scripts also accept a release date (`-Version YYYY.MM.DD` on Windows, positional on Linux). Linux automatic installation uses the standalone x86_64/ARM64 builds, which require glibc 2.17+; on other architectures the Python executable requires Python 3.10+. Supply your own yt-dlp for other environments.

## Gaps and multiple instances

`--blank-ms N` defaults to **0**, so no blank frames or disconnect are inserted. With a positive gap, the default sends black frames while retaining the same RTSP publisher and client connections. Black gaps round up to an output frame: at 25 fps a 1 ms gap becomes 40 ms. Gaps also apply from the last video back to the first when looping.

Add **`--offline-gaps`** to close the publisher between files for the configured gap, then publish the next file to the same URL. MediaMTX stays running but disconnects existing readers. This simulates a camera that wakes up and becomes unavailable again; the viewer must reconnect. With a zero gap this switch does not interrupt the stream. Decoder startup and reconnect work can add a little time between files.

Run multiple instances with distinct **`--port`** values (for example 8554 and 8555). Each instance owns a unique temporary configuration and its selected RTSP TCP port; UDP and other protocol/API listeners are disabled. Audio uses an additional OS-assigned loopback TCP port internally, so it needs no manual port configuration. Stop one instance without affecting the others.

By default the server binds to **127.0.0.1**. For a viewer running on another machine, add `--listen 0.0.0.0` and use `rtsp://<harness-machine-IP>:8554/sample`; allow that TCP port through the host firewall. IPv6 bind addresses also work. This sample server has no client authentication, so bind it only to a trusted test network. Publishing is restricted to the selected local address.

## Options

Run `run.cmd --help` or `bash run.sh --help` for usage.

| Option | Default | Behavior |
| --- | --- | --- |
| `[folder]` / `--folder PATH` | Current directory | Sample video directory |
| `[YouTube-URL]` / `--youtube URL` | Off | A YouTube video or active livestream instead of a folder |
| `--yt-dlp PATH` | Beside the harness DLL / PATH | YouTube URL extractor executable |
| `--youtube-format FORMAT` | `bv*+ba/bv*` (`bv*` with `--no-audio`) | yt-dlp format selector; source resolution/bandwidth can be constrained here |
| `--order name\|created\|modified` | `name` | Filename order or filesystem creation/modification UTC date, oldest first |
| `--descending` | Off | Reverse the selected order |
| `--recursive` | Off | Include subdirectories |
| `--extensions .mp4,.mkv` | Common video extensions | Override the extension filter; `'*'` includes every file |
| `--blank-ms N` | `0` | Gap between videos in whole milliseconds |
| `--offline-gaps` | Off | Disconnect instead of black frames during positive gaps |
| `--no-audio` | Off | Disable audio; otherwise publish source audio and silence as stereo AAC |
| `--once` | Off | Play one pass and stop |
| `--port N` | `8554` | RTSP TCP port, 1 through 65535 |
| `--listen IP` | `127.0.0.1` | Local IPv4/IPv6 bind address |
| `--path NAME` | `sample` | Stream path; supports nested segments such as `camera/main` |
| `--size WIDTHxHEIGHT` | `1280x720` | Even output dimensions for YUV420/H.264 |
| `--fps N` | `25` | Positive output frame rate |
| `--ffmpeg PATH` | `ffmpeg` | FFmpeg executable |
| `--ffprobe PATH` | Beside FFmpeg / PATH | FFprobe executable |
| `--mediamtx PATH` | Beside the harness DLL | MediaMTX executable |
| `--timeout-seconds N` | `30` | Process startup/shutdown and YouTube URL extraction deadline |

Filename sorting is ordinal, case-insensitive, with an ordinal tie-breaker; `clip10.mp4` sorts before `clip2.mp4`. Date ties use filename order. Creation dates are filesystem dates, not embedded recording timestamps, and can change when files are copied or depend on filesystem support. The playlist is snapshotted at startup; restart to include new files. Default extensions are MP4, MKV, AVI, MOV, M4V, WebM, WMV, ASF, FLV, MPG, MPEG, TS, MTS, M2TS, VOB, OGV, 3GP, H264, 264, H265 and HEVC. Use `--extensions` for any other FFmpeg-supported container.

Resolution and frame rate control CPU and memory usage. The tool retains individual raw frames and child-process pipes, rather than caching the playlist. Dimensions must fit an actual .NET array; available memory and FFmpeg capabilities can impose additional practical constraints. No file-count or video-duration cap is imposed. Corrupt/unreadable videos, missing video tracks, missing executables, or an occupied port stop the tool with a nonzero exit code and diagnostics. Ctrl+C shuts down its child processes.

## Separate build and tests

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/RtspVideoHarness/build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/RtspVideoHarness/test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/RtspVideoHarness/test.ps1 --integration --mediamtx tools/RtspVideoHarness/artifacts/mediamtx.exe
```

```bash
bash tools/RtspVideoHarness/build.sh
bash tools/RtspVideoHarness/test.sh
bash tools/RtspVideoHarness/test.sh --integration --mediamtx tools/RtspVideoHarness/artifacts/mediamtx
```

The standalone solution is `RtspVideoHarness.slnx`; the main application build is unaffected. Build output is local to `tools/RtspVideoHarness/artifacts`. After building, the .NET 10 runtime is sufficient; run `dotnet /path/to/artifacts/RtspVideoHarness.dll [folder] [options]` or copy the entire artifacts directory to another host with the same OS/architecture for MediaMTX.

For an offline build, use `build.ps1 -SkipMediaMtx` or `build.sh --skip-mediamtx` and supply MediaMTX yourself. To select a release on a fresh install use `build.ps1 -MediaMtxVersion VERSION` or `MEDIAMTX_VERSION=VERSION bash build.sh`. An existing binary is retained; replace it explicitly to upgrade.

The unit suite has no external test packages or media dependencies. It covers URL and metadata parsing, unknown live codecs, HTTP headers, format selection, extractor failures, deadlines and cancellation using a fake extractor subprocess. Script regression tests exercise real PowerShell/Bash entry points with local stubs for an old system extractor, first-use installation, existing local binaries, explicit overrides, argument/caller-directory preservation and failure exit codes. PowerShell cases run on Windows; Bash cases run on Linux or Windows when Git Bash is installed. The opt-in integration suite generates tiny video fixtures with FFmpeg and verifies real RTSP decoding, source audio, silence, black gaps, offline disappearance/recovery, independent instances, startup failures and cancellation cleanup. A local HTTP server tests split remote video/audio and an advancing HLS livestream that has not ended. These tests require MediaMTX, FFprobe and FFmpeg with libx264, but no YouTube access or yt-dlp installation.

External components: [MediaMTX](https://mediamtx.org/docs/kickoff/install) ([MIT license](https://github.com/bluenviron/mediamtx/blob/v1.21.1/LICENSE)) provides RTSP hosting; [FFmpeg](https://ffmpeg.org/ffmpeg-formats.html#rawvideo) handles video/audio decoding and encoding; [yt-dlp](https://github.com/yt-dlp/yt-dlp) resolves YouTube media URLs. They are managed child processes, not dependencies of the maintained viewer.
