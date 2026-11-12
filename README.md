# Security Camera Viewer

Created by [Frank Tzanabetis](https://plasticcheese.com). The project is distributed under the [MIT License](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md) for bundled components.

**Installable builds:** Download packages from [GitHub Releases](https://github.com/RealPlasticCheese/SecurityCameraViewer/releases). See [installation and upgrades](docs/installation.md) for Linux and Windows packages.

**Sample RTSP camera:** use the separate [video test harness](tools/RtspVideoHarness/README.md) to play local files in filename or date order, or stream a YouTube video/active livestream with audio. It supports black gaps, offline gaps to simulate sleeping cameras, and configurable ports for multiple instances, with its own Windows/Linux build and run scripts.

**Live rewind (web):** enable a rolling server buffer, then pause, rewind, scrub, change playback speed or return to live in the grid and focused camera. Duration and memory capacity are configurable without a fixed history ceiling. See [playback controls, capacity and gaps](docs/live-rewind.md), and the [playback incident report and troubleshooting checklist](docs/rewind-playback-incident-2026-10-12.md).

**NVR recordings (RaySharp):** browse the recorder's hard drive, prepare recorded H.264 clips for browser playback, and save/download MP4 copies in a separate server section. Uses portable managed networking and FFmpeg on Windows or Linux, without the vendor's native SDK. See [connection, playback, exports, and current limitations](docs/nvr-recordings.md).

**RTSP & ONVIF service:** share the active camera configuration with phone viewers, NVRs and other clients through the existing web server. The hamburger menu provides client credentials, RTSP and ONVIF addresses, individual-camera/recorder discovery, snapshots and motion events. MediaMTX runs as a managed companion process on Windows or Linux. See [easy setup, client connections and compatibility](docs/streaming-service.md).

A local live viewer built with C# / .NET 10. The maintained application is the **cross-platform ASP.NET Core web server**, which runs on Windows and Linux. No browser plugins or cloud account are needed for standard RTSP viewing.

**Performance profiling:** capture runtime counters, listener-enabled processing-stage metrics, managed stacks, and allocation samples with Windows/Linux scripts. See [profiling setup, repeatable workloads, and interpreting results](docs/profiling.md).

**Web-only application:** the former Windows WPF and Linux Avalonia viewers have been removed. Windows and Linux remain supported server hosts. For a separate desktop window, use the [installed web app or browser app launcher](docs/browser-app.md); both connect to the same web server.

**Battery cameras (web):** select **Motion-activated / battery camera** for intermittent RTSP feeds, choose a local poll interval of **0.1–3600 seconds** with up to one decimal place (default **1 second**), and optionally record whenever video is available. Sleeping tiles retain a timestamped last image. See [battery camera setup, schedules, and limitations](docs/battery-cameras.md).

**Camera setup:** mix independent RTSP cameras and recorder channels in named configurations. Add cameras manually, generate recorder channels from presets, or discover ONVIF devices and select Media/Media2 profiles. The web setup UI shows metadata and exports redacted JSON reports for troubleshooting. Existing recorder settings migrate while retaining camera identities. See [camera setup, credentials, discovery and diagnostic exports](docs/camera-setup.md).

See [authentication support and future work](docs/authentication-support.md) for supported methods, decoder/runtime dependencies, unsupported methods and testing limitations.

## Web server (Windows or Linux)

The web version fills the viewport with a responsive camera grid, using 1px borders and spacing and compact labels over the video. There is no fixed camera-count limit; capacity depends on server resources and camera/recorder connection limits. Camera numbers follow their positions in **Cameras & configurations**; **Move left** and **Move right** reorder them. The floating hamburger menu at the top left contains all controls and connection status: Connect, Disconnect, Connection settings with an RTSP test, main/substream quality, camera focus, camera grid, and fullscreen. Double-click a picture or choose **Focus camera** in the menu to enlarge it; **Esc** closes the menu first, then returns to the grid. **F11** or **Full screen** beside the menu title at the top right toggles browser fullscreen where supported; the button changes to **Exit full screen** while active.

**Install on Android:** open the viewer in a compatible browser such as Chrome, using an **HTTPS address with a certificate trusted by the phone**. When the browser offers installation, **Install app** appears at the bottom of the hamburger menu and opens its installation prompt. It is hidden when installation is unavailable or the viewer is running as an installed app. You can also use the browser's **Install app** or **Add to Home screen** option. The viewer uses a web app manifest and the browser installation prompt, following the same approach as feManage; it opens in its own standalone window with a camera icon. Chrome may require a tap and some time on the page before offering installation. Plain HTTP through a LAN IP does not qualify for the in-app prompt; localhost only helps when browsing on the server itself. See [Chrome's install criteria](https://web.dev/articles/install-criteria). The server must remain running and reachable for live video and recordings.

For trusted HTTPS at `https://viewer.example.com:5080/`, follow [Namecheap DNS and Let's Encrypt setup](docs/https-namecheap.md). The `web-https.sh` script prompts for ports, configures Caddy and automatic certificate renewal, and can move an existing viewer to an internal HTTP port with `sudo bash web-https.sh ports`. It requires no router port forwarding or CA installation on phones. For access by private IP instead, follow [HTTPS on a Linux NUC and Android installation](docs/https-linux-nuc.md).

The server requires **FFmpeg on PATH** and the **.NET 10 ASP.NET Core Runtime** (included with the SDK). Building requires the .NET 10 SDK. The web project has no WPF, Avalonia, VLC, Windows credential APIs, or third-party NuGet dependencies; it uses the shared Core, Recording, and Nvr libraries.

**Live picture playback:** choose Automatic, Native video, Browser software decoding or Server JPEG previews per camera in **Cameras & configurations**. Automatic reports the planned and actual method; availability and client/server CPU warnings appear beside the selection. [Playback guide and benchmarking](docs/browser-camera-playback.md).

**Live audio:** each camera's bottom bar shows its audio codec and a text-sized **Audio OFF** / **Audio ON** toggle, including in the grid. **No Audio** appears when its stream has no sound. A normal click to turn sound on mutes other cameras; **Ctrl+click** keeps them audible. The menu provides the same focused-camera toggle and global **Volume**. [Live audio guide](docs/live-audio.md).

**Camera layout:** drag a divider to resize the full aligned horizontal or vertical bar. Hold **Ctrl** while dragging to resize just that segment. Dividers have a 12-pixel grab area and highlight on hover; they also support arrow keys, with Ctrl for segment-only changes. At a staggered T junction, touching edges move together to keep the pictures rectangular without gaps or overlap. Drag a picture onto another picture to swap their positions, or onto an empty grid position to move it there. Playback continues during layout changes.

**Camera zoom:** double-click a picture or choose **Focus camera** to expand it, then click and drag over the picture to select an area. The selection keeps the viewing area's aspect ratio and stays within the camera picture; releasing zooms that area to fill the view without stretching. You can also scroll up over the picture to zoom in toward the mouse pointer, or scroll down to zoom out; scrolling all the way out restores the full picture. Wheel zoom is limited to 32× the fitted picture size and works before or after selecting an area. Once zoomed, drag the picture to pan around at the same zoom level; panning stops at the picture's edges. This also works with picture enhancements, while listening to live audio and on retained last pictures. Scrolling over controls keeps their normal behavior, and Ctrl/Command+scroll keeps the browser's zoom gesture. Press **Esc**, double-click the picture, or choose **Reset** to restore the full picture while keeping the camera expanded. Another **Esc** or double-click returns to the grid; Esc closes an open menu or dialog first. Zoom is local to this browser and resets when leaving the camera or changing configuration; it does not change the camera, recordings, or saved grid layout.

**Picture enhancements:** in the expanded camera view, use the switch to the right of **Enhance** to turn processing on or off. Select **Enhance** or its downward chevron to open the settings; the chevron points up while expanded. Collapsing settings keeps processing running and the switch remains visible. The panel contains Balanced, Low light and Detail presets, plus brightness, contrast, gamma/shadows, edge-preserving noise reduction, sharpening strength/radius, saturation and vibrance sliders. Each camera’s slider values and switch preference save automatically to the server, survive page refreshes and server restarts, and are shared across browsers. Switching off preserves every slider value and shows the original picture immediately. **Reset settings** restores Balanced. **Undo** and **Redo** beside it restore slider, preset and reset edits made since the panel was opened; each slider drag counts as one action, and reopening the panel starts a fresh undo history. Undo/redo also save to the server and work while enhancements are off. Other open browsers pick up saved settings when their enhancement panel is closed, so active edits are not interrupted. If saving fails, the panel retains your edits and offers **Retry saving**. Enhancement works with zoom, retained last pictures and live video with audio. Returning to the grid closes the panel and keeps opted-in cameras enhanced; hidden cameras release their renderer until visible again. Recordings keep their original pictures.

**Overlay transparency:** use the slider below Volume in the viewer menu to fade camera labels, detection boxes and metadata, zoom/enhancement controls, sound controls and layout dividers. 0% keeps overlays fully visible; 100% hides them. The preference is saved in this browser. Camera pictures keep their original opacity, and the menu remains available to adjust transparency.

**Touch zoom:** in an expanded camera, use two fingers to pinch in or out around their midpoint. Placing the second finger cancels an unfinished rectangle selection; moving both fingers together also pans the zoomed picture. Pinch out to the full picture or use **Reset** to restore it. A single finger keeps rectangle selection when unzoomed and drag-to-pan when zoomed. Lifting either finger ends the pinch; lift both fingers before starting another gesture. This uses touch input directly and works alongside wheel zoom and enhancements without detecting a tablet model.

Processing uses a bundled, MIT-licensed copy of [glfx.js](https://evanw.github.io/glfx.js/docs/) and requires browser WebGL support. If processing fails, the original picture remains available and the controls report it. Work is limited to 10 updates per second and up to 1280 pixels on either axis / 720p total pixels; larger sources are scaled proportionally for processing. Hidden tabs and inactive cameras release processing resources. These filters improve the visibility of captured detail; they do not recover missing detail or increase the source resolution.

Enhancement tests: run `node --test tests/web-camera-enhancements.test.cjs` for controller, filter-chain and resource lifecycle checks. To verify actual GPU output, copy `tests/enhancement-browser-harness.html` and `tests/enhancement-browser-harness.js` into a test publish's `wwwroot`, open `/enhancement-browser-harness.html` and choose **Run pixel tests**. It uses generated pixels to verify neutral output, brightness, gamma, grayscale, noise variance and sharpening. These harness files are not part of the application publish.

Hover highlights every segment that will move; pressing or releasing **Ctrl** updates that highlight immediately. Drag within **10 CSS pixels** of another compatible parallel divider to snap into exact alignment; the destination line highlights too. Releasing the drag joins the alignment for subsequent full-bar drags. **Double-click** a divider (or focus it and press **Enter**) to realign that segment with the nearest compatible parallel line. If no compatible line exists, it balances the adjacent space. These changes save immediately for the current configuration and breakpoint without resetting other sizes or feed positions.

Sizes and positions save immediately in this browser, separately for each camera configuration and each existing screen-width breakpoint: **mobile** (up to 480 CSS pixels), **wide mobile** (481–600), **tablet** (601–760), and **desktop** (above 760). Both mobile ranges start with a vertical stack. Changing one range does not overwrite another; crossing a breakpoint restores its saved layout. Sizes are proportions of the viewing area, so each layout scales with its window and fullscreen. Custom layouts retain their arrangement; **Reset camera layout** in the menu restores the automatic layout for the current breakpoint only. Adding or removing cameras resets an incompatible layout. Browser storage must be available to retain changes after reloading; layouts are not shared with other browsers.

**Keep screen awake** in the hamburger menu prevents automatic screen sleep while this browser tab is visible. It starts off on each page load and applies only to that browser tab. Turn it off to restore normal screen sleep; switching away pauses it, and returning requests it again. The browser may decline or release it because of permissions or power-saving settings, which the menu reports. This uses the browser's Screen Wake Lock API and requires **HTTPS or localhost**; plain HTTP access through a LAN IP cannot use it.

### Windows

Double-click `launch-web.cmd`. It builds the server if necessary, starts it, and leaves the console open with the URLs. To rebuild after changes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build-web.ps1
.\launch-web.cmd
```

### Linux

Install the .NET 10 SDK and FFmpeg using your distribution's packages (for example `sudo apt install dotnet-sdk-10.0 ffmpeg` on a distribution that provides .NET 10), then:

```bash
bash build-web.sh
bash run-web.sh
```

The same published server DLL runs on either OS with the ASP.NET Core Runtime and FFmpeg:

```bash
dotnet artifacts/web/SecurityCameraViewer.Web.dll
```

It listens on all network interfaces at port **5080** and prints:

```text
Local: http://localhost:5080/
LAN:   http://<this-computer's-LAN-IP>:5080/
```

Open the local URL on the server machine, or the printed LAN URL on another device. If a firewall blocks LAN access, allow the server's TCP port on your private network. By default this is a trusted-LAN viewer. Open **Integrations & API** in the hamburger menu to enable scoped API tokens and browser sign-in. Connect, Disconnect, settings, and quality are shared across browsers; camera focus and fullscreen belong to each browser. Stop the server with **Ctrl+C**.

### Desktop app windows and existing-server shortcuts

Add `--open-browser` to `launch-web.cmd` or `run-web.sh` to open an app window after the local server starts. Use `connect-web.cmd "http://camera-server:5080/"` or `bash connect-web.sh "http://camera-server:5080/"` to open an existing server without starting another one. Select Edge, Chrome, Chromium or Brave with `--browser`; automatic selection falls back to the default browser if necessary.

For everyday use from another computer, install the site as a web app and pin its shortcut once. See [browser choices, startup options, connection scripts and HTTPS addresses](docs/browser-app.md).

### Linux service: run automatically at startup

On Linux Mint and other Linux distributions using systemd, `web-service.sh` installs a system service that starts at boot, before login, and restarts after failures. It runs the published server from this checkout as your normal Linux user. Interactive installation prompts for the HTTP port; `--port PORT` skips the prompt. A fresh installation defaults to `0.0.0.0:5080`. Reinstalling preserves the existing managed port and binding, including loopback-only HTTP after HTTPS port migration. With HTTPS on 5080, open the Caddy HTTPS address; the internal HTTP listener is only accessible on the NUC.

From your normal account, in the project folder:

```bash
bash build-web.sh
# Stop any manually running web server with Ctrl+C before installing.
bash web-service.sh install
bash web-service.sh status
```

The script requests `sudo` for system changes. It checks the published files, FFmpeg, .NET 10 ASP.NET Core Runtime, and service-user access before installing `/etc/systemd/system/security-camera-viewer.service`. A self-contained Linux publish is also supported without an installed .NET runtime. Installation enables startup and starts the service immediately; reinstalling updates the unit and restarts it. It does not build the app or install dependencies.

Keep this checkout and `artifacts/web` in place, on storage available before login. An encrypted home directory unlocked only at login is unsuitable for this setup; put the checkout and selected settings document on storage available at boot instead. The script does not copy the application elsewhere. If you move the checkout, run `install` again from its new location.

**Camera settings and passwords:** installation selects `${XDG_DATA_HOME:-$HOME/.local/share}/SecurityCameraViewer.Web/settings.json` and records that absolute path in the unit. When invoked through `sudo`, it defaults to the original user's `~/.local/share`; use `--settings` for a custom location. Enter passwords in the browser and save; they are stored in the private unified settings document and survive restarts. For an existing installation, stop the service, run the [one-time settings converter](docs/settings.md), and reinstall the generated service unit before starting the updated server.

Configure the recorder in the browser's **Connection** menu; automatic connection is enabled by default. If disabled in your saved connection section, set `AutoConnect` to `true` while the service is stopped.

Service management commands:

| Command | Effect |
|---|---|
| `bash web-service.sh status` | Show service state and recent messages; exits nonzero if inactive/missing |
| `bash web-service.sh logs` | Follow the last 100 log entries; Ctrl+C stops following, not the service |
| `bash web-service.sh start` | Start the installed service |
| `bash web-service.sh stop` | Stop now, leaving its startup setting unchanged |
| `bash web-service.sh restart` | Restart after a password or configuration change |
| `bash web-service.sh disable` | Stop now and disable startup |
| `bash web-service.sh enable` | Enable startup and start now |
| `bash web-service.sh show` | Show the installed unit and any systemd overrides |
| `bash web-service.sh uninstall` | Stop, disable, and remove the unit; preserve app files, settings, passwords, and custom drop-ins |
| `bash web-service.sh --help` | Show commands and install options |

To customize installation, supply any of these options. Relative file paths are resolved from your current directory; executable options accept either a path or a command on your PATH:

```bash
bash web-service.sh install --port 8080 \
  --settings "$HOME/.local/share/SecurityCameraViewer.Web/settings.json"

# Only needed when automatic executable discovery or account selection differs:
bash web-service.sh install --user YOUR_USER \
  --dotnet /path/to/dotnet --ffmpeg /usr/bin/ffmpeg
```

Run the script without `sudo` so it can discover tools such as `$HOME/.dotnet/dotnet` on your PATH. If you run it directly as root, specify `--user`; the server never runs as root. Ports must be between 1024 and 65535. Reinstallation uses the defaults plus the options supplied on that invocation, so repeat your custom options when reinstalling. The script refuses to replace or remove an existing unit that it did not create: back up and remove a previous manually created unit before using `install`.

To update the application from Git, run the root update script:

```bash
bash update.sh
```

Or, from PowerShell on the same Linux systemd host:

```powershell
./update.ps1
```

Both scripts run `bash web-service.sh stop`, `git pull`, `bash build-web.sh`, and `bash web-service.sh start`, in that order, from the repository folder. They stop at the first failed step; a pull or build failure leaves the service stopped so you can resolve the error before restarting it. The PowerShell version also requires Bash and uses the existing Linux service manager.

The update scripts use the default framework-dependent build. For a self-contained publish, run the four commands manually with your existing options on `build-web.sh`. If you change between framework-dependent and self-contained builds, rerun `install` with your chosen service options to refresh the launch command.

**LAN troubleshooting:** use `hostname -I` to find the Mint machine's LAN address, and check `status` / `logs` if the page does not load. A manually running copy can occupy port 5080 and prevent the service starting. If localhost works but another device cannot connect, check Mint's firewall; the installer makes no firewall changes. For example, if UFW is enabled and your trusted LAN is `192.168.1.0/24`, allow only that subnet (substitute your actual subnet and port):

```bash
sudo ufw allow from 192.168.1.0/24 to any port 5080 proto tcp
```

Keep access limited to your trusted network; enable **Integrations & API** for authentication and automation access. For the underlying service options, see the [systemd service documentation](https://www.freedesktop.org/software/systemd/man/latest/systemd.service.html).

### Recorder settings and startup options

Recorder defaults are `192.0.2.10`, RTSP port `554`, username `admin`, four channels, and `/ip{channel:00}/{stream}`. See [camera setup](docs/camera-setup.md) for recorder presets and supported path placeholders. Open **Connection** to change the address, RTSP credentials, camera count, names, or path; **Test connection** checks camera 1. All web settings are saved in one private document under the OS local application data directory in `SecurityCameraViewer.Web/settings.json`; feature accessors share one singleton persistence service.

Passwords entered in the browser are persisted in the private unified settings file. They are omitted from ordinary state/configuration APIs, browser video URLs and FFmpeg command arguments. Shared camera, per-camera, ONVIF, NVR and streaming client passwords survive restart. See [settings, file permissions and one-time migration](docs/settings.md). The web server does not import Windows DPAPI passwords.

All launch commands accept the following server options:

```bash
# Listen on a different port:
dotnet artifacts/web/SecurityCameraViewer.Web.dll --urls http://0.0.0.0:8080
# Explicit configuration and password locations (use paths appropriate to your OS):
dotnet artifacts/web/SecurityCameraViewer.Web.dll --settings /path/settings.json
# FFmpeg outside PATH:
dotnet artifacts/web/SecurityCameraViewer.Web.dll --ffmpeg /path/to/ffmpeg
# Local fixture video, clearly labelled TEST VIDEO; does not save settings:
dotnet artifacts/web/SecurityCameraViewer.Web.dll --demo
```

For a self-contained Windows publish, use `build-web.ps1 -SelfContained -Runtime win-x64`. On Linux, use `bash build-web.sh -r linux-x64 --self-contained true` (or `linux-arm64`). FFmpeg remains a separate system dependency. Copy the entire published folder, including `wwwroot`.

### Playback and shared code

The server decodes RTSP over TCP to complete JPEG frames, delivered through one WebSocket per browser. Output is silent, at up to **10 fps and 1280 × 720**, preserving the camera's aspect ratio. This works without browser codec plugins and supports the input codecs available in the installed FFmpeg. JPEG delivery uses more bandwidth than compressed H.264 video; substreams reduce input/decoder load.

There is one decoder per camera regardless of how many browsers connect. Only the latest complete frame is retained. Each browser acknowledges decoded batches, so slow viewers skip frames without building a queue or stalling other viewers. Camera loss retains the last picture with an amber age label; LIVE requires fresh decoded frames. Stalls restart after 30 seconds with the shared retry schedule (2, 5, 10, 20, then 30 seconds). Disconnect stops all decoders and retries.

For synchronized live video and sound, click **Audio OFF** beside the audio codec in any camera's bottom bar, or use the same toggle for the focused camera in the hamburger menu. It changes to **Audio ON**; click again to release that camera's session. Normal activation mutes other cameras; Ctrl+click keeps them audible. **Volume** applies to all selected cameras. The server checks stream descriptions automatically before any click, showing **No Audio** for silent cameras. Listening uses an on-demand main-stream session shared by listeners on that camera and is independent of recording. See [live audio](docs/live-audio.md) for compatibility, resources and recovery.

`SecurityCameraViewer.Core` owns settings/path validation, RTSP and ONVIF communication, recovery decisions, and the FFmpeg JPEG preview session. The web server references Core, Recording and Nvr; none requires a desktop UI framework or VLC. Browser Native video playback remains supported and is separate from the removed desktop viewers.

For supported camera speakers, the focused-video overlay also provides **Hold to talk**. It is shown only after ONVIF RTSP backchannel and TCP transport verification. Hold the primary pointer, Space or Enter; release to stop. Microphone capture requires HTTPS (or localhost), browser permission and AudioWorklet support. Sending audio requires `control` access when authentication is enabled. See [push-to-talk support and safety limits](docs/push-to-talk.md).

### Continuous recording and motion events (web server)

Open the hamburger menu and choose **Motion & recording settings**. Recording starts disabled. Choose a folder on the server, enable recording, and save. The server continues monitoring with every browser closed and with live viewing disconnected. The live **Quality** setting does not affect recordings. Saving connection or recording settings restarts recording workers and marks any open clips as interrupted.

Choose **Events**, **Continuous**, or **Record whenever video is available** per camera. Continuous mode saves consecutive segments throughout its recording schedule, with optional motion markers and webhooks. Available-video mode retains battery camera sleep/wake behavior. All modes share the writer, SQLite catalogue, camera/date file layout, protection and retention. See [continuous recording and archive storage](docs/continuous-recording.md) for setup, capacity, migration and backup.

In Event mode, pre-event video stays in a **bounded RAM buffer of compressed main-stream video**. There is no rolling disk cache, disk-backed segment directory, or temporary video written while idle. Disk writes start only after motion is detected. The defaults retain approximately **10 seconds before motion** and **20 seconds after the last motion**. Footage is copied without re-encoding; long activity is split into overlapping clips. An event is finalized into an MP4 and a thumbnail after capture. Finalization briefly needs both the captured and finalized files; storage admission reserves this space.

Named detection templates provide reusable method, sensitivity, moving-area and analysis controls. Cameras select a default template; inclusion zones can inherit it or select another. The separate **Motion detection templates** form in the hamburger menu includes optional adaptive noise, persistence, lighting and shadow filters, starter templates, and JSON import/export. Existing policies migrate without enabling filters. Use **Edit this template** beside an assignment to open its settings while preserving pending recording edits. Templates and recording settings have separate Save buttons. Substream selection stays with each camera. See [motion and recording](docs/motion-recording.md) for tuning and template behaviour. Recordings always use the main stream. The first detector analyses 320×180 grayscale frames at up to 5 fps using adaptive background subtraction, lighting compensation and a three-frame debounce after a two-second warmup. It detects movement, including weather and shadows; it does not classify people or vehicles.

**Zones and schedules:** draw multiple rectangles or freeform polygons over the camera snapshot, give each a name, and choose whether it detects or excludes motion. Polygon drawing uses clicks for vertices and **Finish polygon** (or Enter); Escape cancels the draft. A camera supports up to 32 zones, with up to 64 vertices per polygon. Zones default to all day, every day. Add weekly schedule entries with selected weekdays and either all-day coverage or a time range. Multiple entries are combined. The explicit **Zone schedule timezone** controls all schedules; **Use this browser's timezone** selects your local IANA timezone, such as `Australia/Brisbane`.

**Zone actions:** define reusable named Record or Webhook actions in the same dialog. Cameras default to Record; zones inherit those defaults or select their own actions, including none. A doorway can record and invoke a webhook even while a driveway is already recording. Webhooks support an optional JSON body and a per-zone cooldown, with background retries. Existing zone names are preserved and remain editable. See [zone action behavior](docs/motion-recording.md#reusable-zone-actions).

- Start times are inclusive and end times exclusive. `00:00–06:00` followed by `06:00–19:00` has no gap.
- Overnight ranges belong to their starting weekday: Friday `22:00–06:00` ends Saturday morning.
- Exclusions override inclusions. With no inclusion zones configured, the whole picture is monitored except active exclusions. If inclusion zones exist but none are active, detection pauses.
- Schedule boundaries reset the detector's two-second warmup. An existing event retains its post-event tail. Daylight-saving changes follow the selected timezone's local clock.

**View recordings** in the hamburger menu provides camera/date filters, thumbnails, playback, MP4 downloads, protection from automatic cleanup, and deletion. Select a camera and date to see recorded coverage, seek to activity markers and play consecutive segments. Times use the browser's timezone. Refresh the list to see newly finished events. The oldest unprotected events expire by age or storage budget; protected events still count toward that budget. If protected/active footage or low disk space prevents recording, a recording error appears and the workers retry without interrupting live viewing. Failed finalization retains the captured data for download. Completed fragments from interrupted events are recovered on server startup.

Recording settings belong to each camera configuration in the unified `settings.json`. Use `--settings /absolute/path.json` to select the complete settings document. New media lives under configuration, stable camera ID and UTC date folders; `archive.db` holds metadata. Existing `events` sidecars import automatically without moving media or changing recording IDs. Back up the complete folder with the server stopped before upgrading; older versions cannot discover the new layout. Changing folders preserves the old library; select its folder again to browse it. These endpoints require authentication once integrations are enabled.

**Capacity and compatibility:** the default compressed ring limit is 32 MB per camera, in addition to FFmpeg decoder and bounded working buffers. At 2 Mb/s, ten seconds is about 2.5 MB per camera. The ring keeps extra keyframe coverage when capacity permits. High bitrate, long keyframe intervals, startup or reconnect can shorten pre-event coverage; the settings dialog reports the current seconds/bytes buffered. Configure camera keyframes approximately every 1–2 seconds when possible; this implementation rejects fragments over 16 MB per MP4 box or 30 seconds. Event mode and continuous mode with detection use separate recording and analysis RTSP sessions. Continuous mode without detection uses only the recording session. Verify your recorder's connection capacity. The server and cameras must remain running. RAM footage is lost on shutdown/crash; the application does not prevent the OS from paging memory under system-wide memory pressure.

H.264 MP4 offers broad browser compatibility. Other MP4-compatible source codecs can be retained, but browser codec support varies; use **Download MP4** with a compatible player if necessary. Recordings automatically include audio tracks when the RTSP stream provides them, for both continuous and motion-activated cameras. Audio is encoded as AAC for MP4 playback; source video is preserved without re-encoding. Streams without audio continue to record video. Live sound is optional through each camera's Audio ON/OFF toggle. Test detection and retention on your actual camera scenes before relying on event coverage.

Implementation boundaries and detector replacement are described in [the motion recording design](docs/motion-recording.md).

AI and hybrid settings expose the selected model's object types and numeric controls. The built-in models include all 80 COCO classes, including animals. The default **People and all vehicles** preset triggers people, bicycles (pushbikes), cars, motorcycles, buses, trucks, trains, boats and airplanes, with no ignored types. Choose Trigger, Ignore or No action per type; an ignored animal never prevents a person from triggering recording. The selectable **Original defaults (people and cars)** preset restores the previous configuration. Add compatible models using data-only descriptions in the model folder and choose **Refresh models**; see [model descriptions and presets](docs/ai-models.md).

Enable **Show detection overlays** in the viewer menu to see the existing detector's regions over live video. The **Show detection metadata text** checkbox directly below it shows or hides labels and details while keeping detection boxes visible. This metadata preference defaults on and is saved in this browser. Labels identify the method and, for AI, the object class and confidence. Ignored objects remain visible with dotted gray outlines and an Ignored label. Hover or click/tap a label for model, device, timing and zone details; keyboard users can press Enter and close with Escape. Dashed outlines are candidates. Boxes follow zoom and expire when results become stale. This browser preference defaults off and adds no AI inference. See the **?** guide in Motion & recording settings for configuration advice.

### Home Assistant, APIs and webhooks

Open **Integrations & API** in the existing hamburger menu. Enable integration access, save the initial administrator token, then create a separate Home Assistant token with `read` and `trigger` permissions. Add `control` for temporary arming overrides; reserve `admin` for configuration. Enabling integrations also secures the existing browser/API routes.

The versioned `/api/v1` API provides camera/zone discovery, snapshots, recording triggers, metadata/media, protection/deletion, recording settings, temporary arming, health and live SSE events. Outgoing webhooks cover named-zone motion transitions, recording lifecycle, camera availability and storage warnings. Configure camera/zone filters, event types, cooldowns, signing-secret rotation and delivery tests in the menu.

The durable delivery queue runs inside the same web-server process on Linux or Windows. No broker or separately installed database server is needed. Recording metadata uses an embedded SQLite catalogue; the separate delivery queue persists small event/delivery metadata. Event-mode pre-event video stays in RAM. See [integration setup, API semantics and Home Assistant examples](docs/integrations.md), also available through the menu's setup guide. The authenticated OpenAPI specification is `/api/v1/openapi.json`.

Run motion/recording verification (FFmpeg, ffprobe and Python required; no real cameras are contacted):

```powershell
dotnet publish src/SecurityCameraViewer.Web -c Release -o artifacts/recording-web -p:RestoreConfigFile=NuGet.Config
dotnet run --project tests/SecurityCameraViewer.Recording.Tests -c Release -- artifacts/recording-web/SecurityCameraViewer.Web.dll
```

The tests cover zone geometry, weekly/overnight schedules, timezone interpretation, warmup/debounce, RAM limits, zero idle recording writes, playable stream-copied clips, crash recovery, protected retention, media paths, four simultaneous cameras, independent viewing controls, and API validation.

### Web regression tests

The Linux service installer has isolated tests using Bash and Python (no root access, service changes, or camera connections):

```bash
python3 tests/test_web_service.py
python3 tests/test_web_https.py
# On Windows with Git Bash:
python tests/test_web_service.py --bash "C:/Program Files/Git/bin/bash.exe"
python tests/test_web_https.py --bash "C:/Program Files/Git/bin/bash.exe"
```

These mock systemd and privileged commands to check install/reinstall, command dispatch, input/dependency validation, path escaping, and preservation of settings/passwords on uninstall. A real Linux boot remains a separate smoke test: install, reboot, check `bash web-service.sh status`, and open the local and LAN URLs. The installer also runs `systemd-analyze verify` when available on the target machine.

Run the browser layout, install-prompt and wake-lock lifecycle tests with Node.js (no extra packages):

```bash
node --test tests/web-wake-lock.test.cjs
node --test tests/web-app-install.test.cjs
node --test tests/web-camera-layout.test.cjs
node --test tests/web-camera-collection.test.cjs
node --test tests/web-recording-filters.test.cjs
node --test tests/web-recording-cameras.test.cjs
node --test tests/web-detection-overlays.test.cjs
node --test tests/web-detection-templates.test.cjs
dotnet run --project tests/SecurityCameraViewer.Web.Tests -c Release -- --recording-filters-only
```

To verify camera footer sizing and badge placement in a real Chromium browser (no camera or server required):

```bash
node --test tests/test_camera_footer_browser.mjs
```

The test uses the application's tile builder and CSS to cover wide and narrow tiles, long names, playback and recording states, and audio controls. Set `SCV_TEST_BROWSER` to a Chromium executable if it is not installed in a standard location.

To check local HTTPS proxy handling, secure sign-in cookies, video WebSockets and install assets without a camera, publish the web server and run:

```bash
python tests/test_https_proxy.py artifacts/web/SecurityCameraViewer.Web.dll
```

After building the web server, run from the repository root:

```bash
dotnet restore tests/SecurityCameraViewer.Web.Tests --configfile NuGet.Config
dotnet run --project tests/SecurityCameraViewer.Web.Tests --no-restore -c Release
```

Tests require Python and FFmpeg. They start their own loopback-only RTSP fixture and web server on temporary ports, without contacting your recorder. Coverage includes complete/fragmented/bounded frames, two browser connections, slow consumers, login rejection, settings persistence, four streams, stalls, recorder restart, quality, disconnect, and shutdown during playback and startup.

## Core protocol and decoder tests

These checks exercise shared camera configuration, RTSP/ONVIF protocols, credential handling, JPEG framing, decoder failures and shutdown without contacting a real recorder:

```powershell
dotnet run --project tests/SecurityCameraViewer.Tests -c Release
```

For manual browser layout, fullscreen and reconnection testing, start `python tests/rtsp_fixture.py` (requires FFmpeg), then launch the web server with `--demo`. This mode labels the picture **TEST VIDEO** and does not save settings. Stop and restart the fixture to exercise automatic reconnection.

## Dependencies

The server requires FFmpeg and the .NET 10 ASP.NET Core Runtime, or a self-contained publish. The recording library uses Microsoft ONNX Runtime and Microsoft.Data.Sqlite; Intel GPU inference optionally uses an externally installed OpenVINO runtime. Browser decoder and enhancement assets have their own notices under `wwwroot/vendor`. See [third-party notices](THIRD-PARTY-NOTICES.md) and preserve the licenses and corresponding source included with published assets.
