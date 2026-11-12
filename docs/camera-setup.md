# Cameras, configurations and troubleshooting

[Back to README](../README.md)

The viewer supports a mixture of recorder channels and standalone RTSP cameras. A **configuration** is a named collection, such as Home or Workshop. One collection is active at a time; up to 32 collections can be saved. The web application has no fixed camera-count limit. Practical capacity depends on server resources, network bandwidth, browser performance and camera/recorder connection limits. Switching collections reconnects live viewing and switches the web server's recording settings and library.

Open **Cameras & configurations** in the web menu. Changes remain a draft until **Save and activate**. The web editor also offers explicit activation of an existing saved collection, duplication, deletion of an inactive collection, and portable configuration import/export.

Each camera in the web editor has its own tab, labelled with its position in the collection and name (for example, **1. Garage**). Use **Move left** or **Move right** to reorder the selected camera. Numbers update immediately after moving or removing a camera; no separate display number is saved. Disabled cameras still occupy a position, have a struck-through tab label and can be selected for editing. The selected tab shows the camera settings under the full heading, such as **1. Garage - Recorder template** or **2. Driveway - RTSP**. Adding a camera adds and selects its tab. Use the left/right arrow keys, Home or End to switch tabs when a tab has keyboard focus.

Live tiles, camera selectors and metadata use these same collection positions. Recording settings omit disabled cameras, so their labels can have gaps while retaining the same numbers as Camera setup. Historical cameras no longer in the collection appear by name in the recording library.

## Video timeout

In the web editor, **Video timeout (seconds)** appears above **Portable configuration**. Set it to **300**, for example, to return a camera tile to its default screen after five minutes without a new picture. A sleeping motion-activated camera shows **Waiting for activity**. Fresh video automatically restores the picture and resets the timeout.

The setting applies to all cameras in the selected collection and is saved, duplicated and included in portable configuration exports. **0** (the default) keeps the last picture indefinitely. The timeout also applies when opening or reconnecting a browser; an old cached picture does not start a new countdown. Recording and snapshot downloads are unaffected.

## Feed retry delays

In **Cameras & configurations**, edit **Feed retry delays (seconds)** as a comma-separated list of positive whole seconds. One schedule applies to native video, browser software decoding and server JPEG previews for all continuous cameras in the selected collection. It is saved with the collection, retained when duplicated, and included in portable configuration exports.

The default is **1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 8, 8, 16, 32**. Each failed connection attempt advances to the next delay; the final delay repeats indefinitely. For example, **2, 5, 10** waits two seconds after the first failure, five after the second, and ten after every subsequent failure. Thirty seconds of healthy video resets the sequence. **Restore default retry delays** replaces the draft field with the default; **Save and activate** applies it. Existing configurations and older imports receive the default automatically.

Each value uses a 32-bit seconds count (maximum **2147483647**); the number of entries has no fixed limit. Empty entries, zero, negative values and fractions are rejected. The schedule controls the wait after a failure is detected. Stall detection stays at eight seconds without encoded video fragments or thirty seconds without JPEG frames. Browser WebSocket reconnection stays at two seconds after closure. Motion-activated cameras continue using their availability polling interval while asleep. **Video timeout** controls the retained picture separately, and recording recovery keeps its own policy.

## Add cameras

- **RTSP:** enter a name, full `rtsp://host:port/path` URL, username and password. Keep credentials out of the URL. A substream URL is optional. Without one, the main stream is used for viewing and motion analysis at either quality setting. RTSP over TCP is used for playback and tests.
- **Recorder:** choose the NVR `/ip{channel:00}/{stream}` or DVR `/ch{channel:00}/{stream}` preset, or supply a custom template. `{channel}`, `{channel:00}` and `{channel0}` expand channel numbers; `{stream}` expands to `0` or `1`. The generated entries become ordinary independent cameras and can be edited individually. The web editor supports selecting a starting recorder channel.
- **ONVIF:** discover devices, select an address, enter ONVIF credentials and read the device's profiles. Alternatively, enter the full HTTP(S) device service URL manually, including its actual port and path. Select a main profile and, optionally, a substream from the same video source. Apply them to a new or existing camera and test video before saving. A recorder may advertise several physical sources, with several quality profiles per source. A profile is not necessarily another camera.

ONVIF and RTSP accounts can differ. Imported cameras initially use the supplied ONVIF credentials for RTSP; edit the RTSP credentials if necessary. The web editor can refresh an active, saved camera's ONVIF profiles using server-held credentials without returning the password to the browser. Applying refreshed profiles retains the camera identity. Returned URLs remain editable, which helps when firmware advertises an unreachable address. Stored URIs are not automatically refreshed during every playback retry.

Legacy recorder settings are converted into independent camera entries when opened in the new editor. The original stream paths, names, default password selection and `camera-<channel>` integration identities are preserved. Run the one-time converter before starting the updated web server; keep its backup until migration is verified.

## Discovery and compatibility

In the web editor, **Main stream aspect ratio** and **Substream aspect ratio** sit directly below their RTSP URLs. Each offers **Auto** (the default), **4:3**, **16:9**, **1:1**, **9:16** and **Custom**. Auto preserves the video’s reported shape; choose a fixed ratio if the camera encodes a widescreen picture into a 4:3 frame or reports an incorrect aspect ratio. Custom accepts positive width:height numbers, including decimals, such as `32:9` or `1.85:1`. The viewer stretches the full picture to the selected shape and fits it inside the tile with black bars where needed.

Save and activate to apply the settings. The selected quality uses its stream’s ratio, falling back to the main ratio when no substream URL is configured. Live sound and buffered rewind use the main-stream ratio. Zoom, detection overlays, picture enhancements and camera picture-in-picture follow the corrected picture. These are browser display settings; they do not change the camera, recording files or RTSP relay output. Both choices are saved on the server and included in portable configuration exports.

For sleeping cameras, the web editor offers **Motion-activated / battery camera** mode and a configurable poll interval from **0.1 to 3600 seconds**, with up to one decimal place (default one second). For example, **0.5** means 500 ms. See [battery cameras](battery-cameras.md) for waiting states, Check now, and optional recording of available video.

Discovery sends bounded IPv4 WS-Discovery probes to `239.255.255.250:3702`. The web UI can select a server interface. The web browser's network is not the discovery network: the **viewer server** must be able to reach the device.

Multicast normally stays within a subnet. A camera behind an NVR's private PoE network may be accessible only through the recorder. Firewalls, VLANs, VPN adapters, sleeping cameras and disabled ONVIF services can prevent discovery. A zero-result scan does not prove that a device lacks ONVIF; try its documented service address. ONVIF discovery and RTSP playback use different ports.

The client reads device information, clock, services/capabilities, Media and Media2 profiles, and stream URIs. Older devices without `GetServices` can use the `GetCapabilities` fallback. Authentication supports ONVIF WS-Security UsernameToken digest and HTTP authentication; device UTC time is used to compensate for clock skew. RTSP probing supports Basic, MD5 Digest and SHA-256 Digest. Playback codec support comes from the installed FFmpeg.

Authentication is not universal. Anonymous RTSP and ONVIF endpoints are supported. The RTSP probe accepts MD5/SHA-256 `-sess` variants, legacy Digest without `qop`, and `qop=auth`; it does not implement `auth-int` or SHA-512/256. Playback authentication also depends on the installed FFmpeg version. OAuth/bearer-token login flows, client certificates, WS-Security password-text tokens and `rtsps://` URLs are not supported. Query tokens already included in a valid RTSP URL are passed through, but the viewer does not acquire or renew them.

See the [authentication support matrix and future work](authentication-support.md) for the distinction between implemented methods, runtime-dependent behavior and verified compatibility.

HTTPS certificates are validated normally. Redirects and device-advertised service addresses on another hostname are not followed with credentials; use the correct reachable device address. ONVIF responses have time and size limits, and XML external entities are disabled.

This is a read-only ONVIF setup client, not an ONVIF conformance certification claim. PTZ, camera setting changes, camera-generated event subscriptions and retrieval of recordings from cameras are outside this change. Software motion detection and local event recording remain available in the web server; local recordings automatically include audio when the RTSP stream provides it. Optional [live audio](live-audio.md) uses the configured RTSP main stream, not ONVIF audio controls or a vendor-specific API.

## Names, identities and recording

Camera names and grid order can change without changing camera identity. Display numbers come from collection order, independently of the recorder channel in an RTSP URL. Stable internal channels are retained for recording history, saved filters, temporary arming and legacy integration compatibility; they can contain gaps and are not display numbers. A new camera receives a UUID; integrations should discover the current IDs from `/api/v1/cameras` rather than construct them from tile positions. New IDs use `camera-<UUID>`; migrated IDs retain their previous form.

Recording rules are bound to identities, not list positions. A replacement camera does not inherit the removed camera's zones. New collections start with recording disabled and a separate default recording folder. Each collection has its own recording settings and temporary arming state. Switching finishes open clips as interrupted before starting the other collection. Deleting a collection removes its saved configuration, credentials, rules and filters; existing recording files are preserved.

The integration API operates on the active collection. Recording metadata and events include the configuration and camera identity. Camera-filtered API queries compare the saved identity, including for a removed camera; reusing a channel does not change historical clip identities. The older browser library's numbered channel filter can include historical cameras that occupied that channel; each clip retains its recorded camera name.

Each viewed camera uses a playback session. Recording and motion analysis can add two further sessions per camera. Explicit diagnostics briefly add test sessions. Check camera connection limits and CPU/network usage when expanding the grid.

## Passwords and unattended startup

Password fields are separate from metadata. Blank fields keep the current password; use **Clear camera password** for an intentionally empty password. Recorder migration uses the existing shared password. Ordinary configuration responses and diagnostic reports omit credentials.

Shared recorder, per-camera and ONVIF passwords are saved in the private unified `settings.json` and restored after restart on Windows and Linux. See [settings, permissions and one-time migration](settings.md). Web configurations, recording rules, saved filters and credentials are sections of the document selected by `--settings`. Legacy web catalogs and credential files can still be imported using the one-time converter.

Keep settings and migration backups out of source control and restrict them to the server account. Duplication and portable imports create new camera identities and require credentials to be supplied again.

## Inspect and export metadata

Use **Camera metadata** in the web menu. The structured JSON is shown directly and can be downloaded with **Export JSON report**. The ONVIF panel also exports an inspection before the camera has been saved.

Reports contain schema version, timestamps, collection and source IDs, RTSP addresses and transport, ONVIF service addresses, manufacturer/model/firmware/serial/hardware information when supplied, source/profile tokens, advertised codec/resolution/frame rate/bitrate, warnings, and connection tests. Web reports also include runtime/OS information, local interfaces and live camera status.

Tests distinguish an RTSP response advertising video from a successfully decoded frame. When `ffprobe` is available beside the selected FFmpeg executable or on PATH, tests also report actual codec, dimensions, frame rate and bitrate where supplied. Optional inspection failures do not turn a successful playback test into a failure. Tests are timestamped snapshots, not ongoing guarantees of availability.

Exports omit credential fields, strip URL user information and redact **all URL query values**, including vendor-specific token names. They retain names, addresses, identifiers and URL paths, which may still be sensitive. Review before sharing. Exports are troubleshooting documents, not restorable configurations. A portable configuration export contains exact URLs and must be treated separately from a redacted diagnostic report. Raw SOAP XML, authorization headers and FFmpeg stderr are not exported.

All `/api/setup/*` operations, including metadata reads and downloads, require `admin` scope when integration authentication is enabled. The existing same-origin protection applies to browser requests.

## Verification

The automated fixtures contact loopback endpoints only:

```powershell
dotnet build SecurityCameraViewer.slnx -c Release
dotnet run --project tests/SecurityCameraViewer.Tests -c Release
dotnet publish src/SecurityCameraViewer.Web -c Release -o artifacts/onvif-web
python tests/test_camera_setup.py artifacts/onvif-web/SecurityCameraViewer.Web.dll
dotnet run --project tests/SecurityCameraViewer.Web.Tests -c Release -- artifacts/onvif-web/SecurityCameraViewer.Web.dll
dotnet run --project tests/SecurityCameraViewer.Recording.Tests -c Release -- artifacts/onvif-web/SecurityCameraViewer.Web.dll
dotnet run --project tests/SecurityCameraViewer.Integration.Tests -c Release -- artifacts/onvif-web/SecurityCameraViewer.Web.dll
```

Coverage includes correlated UDP discovery and malformed replies, Media/Media2, capability fallback, device clock skew and UsernameToken digest verification, hostile service addresses, XML entity rejection, independent credentials, single-stream fallback, actual video decoding/media properties, identities, named collection persistence, restart credential loading, exports and administrator-only access. Multicast routing and interoperability with physical ONVIF devices still require testing on the target network.
