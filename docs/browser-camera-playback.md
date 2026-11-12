# Camera playback methods

In **Cameras & configurations**, choose **Live picture playback** separately for
each camera, then **Save and activate**. Existing cameras default to Automatic.
The preference is saved with the camera; Automatic resolves it separately on
each viewing device.

| Choice | Where video is decoded | Cost and availability |
| --- | --- | --- |
| Automatic (recommended) | Chooses native video, then browser software decoding, then server JPEG previews | Shows the planned choice beside the dropdown, checks the actual stream, and shows the method in use on the camera tile. A fallback includes its reason and CPU warning. |
| Native video | The browser's media decoder | Lowest server work: the server packages the selected H.264/H.265 stream without re-encoding. Availability depends on the actual codec profile, browser, OS and device. Hardware acceleration is managed by the browser. |
| Browser software decoding | A WebAssembly decoder in a dedicated browser worker | Warning: extra client CPU and memory. Supports H.264 and H.265 independently of the browser's native codec support. No JPEG encoding is necessary: decoded pixels are drawn directly to a canvas. |
| Server JPEG previews | FFmpeg on the server; the browser displays JPEG pictures | Warning: extra server CPU. Shared between JPEG viewers of the same camera; started only while requested when `Preview:OnDemand` is enabled. |

Forced Native or Browser choices report failure instead of quietly changing
methods. This makes comparisons reproducible. Automatic tries the alternatives
and reports what it selected. An unsupported encoded stream goes directly to
server JPEG previews. Disconnecting, closing the viewer, changing configuration
or entering rewind releases live grid decoding. The configured video timeout
also applies to native and software-decoded pictures; zero retains the last
picture, with a stale indicator when footage stops.

Encoded live cameras use a shared cursor based on fragment production times,
before viewer delivery. Playback compensates for measured source clock pace and
waits for the slowest participating camera's available footage. A camera that
stops sending data leaves synchronization after the eight-second live inactivity
window. Focused sound uses the same cursor; JPEG previews arrive independently.
See [live rewind](live-rewind.md) for historical timing and capture-time limits.

After startup, ordinary fragment arrivals advance the live cursor steadily even
when fragments have different lengths or arrive with jitter. An ahead camera holds
its current picture briefly until the cursor catches up, rather than replaying
older pictures. Buffer underruns also hold the cursor without accumulating a jump
when footage arrives. Once fresh footage is buffered, accumulated delay recovers
automatically at up to 1.1× the shared live pace, returning to normal near live.
The freshest observed server production time and elapsed browser time reveal
delay even when decoder backpressure leaves only a small local buffer; browser
and server wall clocks need not agree. Native video, focused sound and software
decoding follow that same recovery rate; source clock compensation still applies. Recovery advances by elapsed time and
never re-anchors the cursor on ordinary packet arrivals. Headroom follows the
longest fragment in the retained timing window, with an extra fragment of
hysteresis to avoid reacting to ordinary short/long delivery jitter. The clock's
`catchUpRate` constructor option controls the recovery pace and must exceed one.
Reconnects and changes in visible cameras can establish a new live position.

All three playback methods share the collection's configurable
[feed retry delays](camera-setup.md#feed-retry-delays). Repeated failures advance
through the schedule, its final delay repeats indefinitely, and thirty seconds
of healthy video resets it.

Native and browser software grid connections use that schedule after queue
overflow, a dropped WebSocket or a decoder acknowledgement timeout. Each delay
gets up to 20% extra jitter so cameras reconnect at different times. Transient
transport failures keep the selected decoder: Automatic does not abandon native
video just because a viewer temporarily falls behind. Only actual codec or
decoding failures select another method. Forced modes also retry transient
failures indefinitely. Thirty seconds of continuously advancing pictures reset
the browser's backoff; buffered or acknowledged packets alone do not.

While reconnecting, the last picture is marked stale and the tile shows the reason
and a countdown, such as **Viewer fell behind. Reconnecting in 4 seconds.** The
configured video timeout still controls expiry of that picture. A network return,
restored page or stalled viewer returning to the foreground refreshes its live
connection. Healthy background and picture-in-picture playback continue.

If a decoder fails, an explicitly selected Native or Browser software mode
creates a fresh decoder on the feed retry schedule. The tile shows the countdown
while playback is unavailable. Thirty seconds of healthy video reset this
backoff. A codec the browser explicitly rejects is skipped until the viewer
detects a different codec or starts a new player after a configuration change.

In Automatic, the viewer keeps the fallback visible and tests a higher-ranked
decoder in the background. With a working fallback, probes are spaced by 30,
60, 120 and then 300 seconds, with up to 20% extra jitter. Without fresh pictures,
the viewer uses the feed retry delays. Each tile tests one decoder at a time and
switches only after the probe produces advancing frames for at least two seconds.

The tile's playback label includes the delivered video codec, for example
**Native video · H.265** or **Browser software decoding · H.264**. Its tooltip
includes the full codec/profile MIME description. JPEG previews remain labelled
**Server JPEG previews**.

Beside this label, automatic main-stream RTSP description checks show the camera's
audio codec and a small **Audio OFF** / **Audio ON** toggle, or **No Audio** when
no receiving audio track is advertised. The check does not play media or start a
decoder. While audio plays, these labels follow the delivered video and audio:
live sound can use compatibility video conversion and converts audio to AAC.
Unknown availability shows **Checking audio** until a description succeeds.
See [live audio](live-audio.md) for selection and Ctrl+click behavior.

The dropdown reports native support after receiving the camera's codec metadata.
Before connection it says the codec will be checked. A successful capability
check is provisional until frames play: browser support checks cannot guarantee
that every bitstream will decode ([MDN](https://developer.mozilla.org/en-US/docs/Web/API/MediaSource/isTypeSupported_static)).
Browser software decoding requires WebAssembly, workers and a transferable
[OffscreenCanvas](https://developer.mozilla.org/en-US/docs/Web/API/HTMLCanvasElement/transferControlToOffscreen).
It works with the application's normal LAN HTTP setup and does not require
WebCodecs, a CDN or cross-origin isolation. If native playback is unavailable,
a browser/OS update or a camera's H.264 substream may make it available; an update
alone does not guarantee H.265 support.

## Mobile camera layout

On mobile, a camera area that is wider than it is tall automatically uses a compact
grid: four cameras appear in two rows and two columns. This also works when Android
split screen changes the available area. A taller area keeps the stacked mobile
layout. The breakpoint follows the camera area, rather than the phone's orientation.

The **Sideways camera layout** button beside **Full screen** forces that compact
grid even in a tall split. Select it again to return to automatic layout. Focusing
a camera and returning to the grid retain the choice. Saved camera positions and
divider proportions stay separate for stacked and sideways views; resetting one
view does not erase the other.

## Picture-in-picture

In Chrome on Android, choose **Full screen** in the viewer menu, then press Home.
Fullscreen keeps the normal camera layout, hamburger menu, enhancements, zoom and
playback controls. A separate video behind the viewer supplies the current camera
view to Android's automatic PiP. The floating window shows every selected grid
slot together, including named waiting cameras; a focused view shows that camera.
In a focused native view, the displayed video also remains eligible for automatic
PiP: Chrome can choose it or the captured video, and both show the same camera.
Covered and loading videos cannot displace the displayed fullscreen picture.

PiP includes original camera pictures and camera names. Enhancements, zoom,
detection boxes and viewer controls remain in the main viewer, and their settings
are retained when returning from PiP. Native, software-decoded and JPEG pictures
can all appear in the captured grid. Availability and automatic entry remain
controlled by the browser and phone; capture requires canvas capture and video
frame callbacks.

Capture starts only for Android live-view fullscreen. Its resolution follows the
available viewport pixels and device pixel ratio, with no camera-count limit or
additional server decoder. Foreground capture follows the viewer's animation
clock; visible PiP follows its video compositor when the foreground viewer is
hidden. Leaving fullscreen, rebuilding the camera view or closing the page
releases the capture track and callbacks. Desktop fullscreen and historical
playback retain their existing behavior.

Only decoded videos displayed in the current camera view are otherwise offered to
PiP. Native video progress keeps the picture fresh when Chrome throttles background
frame callbacks; stalled footage still obeys the configured video timeout.

## Other camera processing

These choices control **live grid pictures**. Recording, motion/object analysis,
focused audio and rewind retain their existing settings and pipelines. Analysis
still needs decoded small frames. Snapshots and draft stream tests can briefly
request JPEG generation. Rewind and audio can still use their existing H.264
compatibility conversion for an incompatible source.

Native and browser software viewers share one `grid.copy` FFmpeg process per
camera. That process copies encoded video into fragmented MP4; it does not decode
video or convert audio. Audio/rewind use their separate shared process. JPEG
viewers share the existing preview process. Consequently, recording/detection,
rewind, snapshots or another viewer choosing JPEG can keep server CPU above zero
even when your own grid uses native video.

Detection boxes follow the displayed MP4 frame automatically, including when
buffering changes or a camera reconnects. The viewer retains timestamped detection
metadata and selects results for each camera's actual playback position. This adds
no video decoding or AI checks and needs no manual delay setting. See
[detection history, timing and resource budgets](motion-recording.md#live-detection-observations).

The client acknowledges each compressed fragment only after it has been accepted
or decoded. Remaining packets are bounded by the existing configurable
`LiveMedia:MaximumQueuedPackets` and `LiveMedia:MaximumQueuedBytes` budgets.
Queue overflow discards the old compressed backlog and reconnects at a fresh
keyframe with the same method. Native media-buffer cleanup runs in batches,
retaining roughly eight to sixteen seconds behind playback rather than awaiting
eviction after every fragment. This reduces cleanup work on the acknowledgement
path while bounding retained media; MSE can also retain codec dependencies.
Software decoding preserves reference frames and B-frame
presentation order, skips late display frames, and resets its presentation clock
after a network pause. It does not accumulate a video history on the client.

## Comparing methods on the NUC

1. Use the same cameras, stream quality, recording/detection/rewind settings and
   number of viewing browsers for each capture. Keep Chrome on the NUC in the
   same state; use the laptop as the viewer throughout.
2. Force all compared cameras to one method. Save and activate, allow playback
   to settle, and confirm the tile labels. Automatic can select different methods
   on different devices, so use the forced choices for a controlled comparison.
3. Run the existing server metrics capture for each method, using a separate
   output directory:

   ```bash
   server_pid=$(systemctl show security-camera-viewer.service -p MainPID --value)
   bash profile-web.sh --process-id "$server_pid" --mode metrics \
     --duration-seconds 3600 \
     --output-directory "artifacts/profiling/native-$(date -u +%Y%m%d-%H%M%S)"
   ```

4. Near the end, use **Export playback benchmark** on each active camera. The
   small JSON contains the browser, selected/resolved method, codec support,
   displayed frame count/rate, skipped software frames and mean decode time.
   Recovery diagnostics include the last interruption reason, reconnect count,
   retry countdown, acknowledgement durations, and the queue occupancy/budgets
   immediately before overflow. Protocol fields distinguish the page's negotiated
   protocol, WebSocket transport (`ws`/`wss`) and the server-side HTTP hop; a proxy
   can use different protocols on its two sides. Server warnings record queue
   overflow and send/acknowledgement timeout metadata without camera URLs,
   credentials or video content.
   It contains no camera URLs, passwords or footage. Compare server runtime CPU
   together with FFmpeg CPU tagged `preview`, `grid.copy`, `analysis`, `recording`,
   `live.copy` and `live.transcode`; inspect client CPU separately.

Frame rates include startup since the latest method selection. Native decode
time is unmeasured and exported as zero; it does not mean decoding is free.
The skipped count measures software presentation skips, not network losses or
native hardware-decoder drops. JPEG decode time also includes the asynchronous
image decode and event-loop delay. Use the exports to confirm comparable picture
rates and modes, rather than treating those timing fields as equivalent profilers.

## Local checks and benchmark

The repository retains the tests and compact results, without recordings or
large profiling captures. From the repository root:

```powershell
dotnet publish src/SecurityCameraViewer.Web -c Release -o artifacts/browser-playback-web
node --test tests/web-*.test.cjs
node --test tests/test_picture_in_picture_browser.mjs
node tests/test_camera_decoder.mjs
node tests/test_camera_playback_browser.mjs
node tests/test_camera_playback_browser.mjs --pip-fullscreen
node tests/test_camera_playback_browser.mjs --hevc
node tests/test_camera_playback_browser.mjs --sync
node tests/test_camera_playback_browser.mjs --sync-recovery
node tests/test_camera_playback_browser.mjs --live-latency
node tests/test_camera_playback_browser.mjs --benchmark
```

The browser integration test creates its own local RTSP fixture, server and
headless Chrome profile and stops those processes afterward. It checks concurrent
native/software/JPEG playback, selective JPEG demand, the configuration choices
and Automatic status, zoom, disposal/reconnect and fallback. `--hevc` exercises
real H.265 RTSP/software playback. The codec tests compare 48 decoded frames per
profile with native FFmpeg RGBA output for H.264 Baseline/Main/High and H.265
Main/Main10, including B-frame ordering and malformed-input checks.

`--sync` enables detection against a shared moving-object fixture after a stable
background warmup. It compares native and software-decoded pixels with the
timestamp-selected boxes within the detector's sampling tolerance, rejects
future detections and checks that paused footage holds the overlay timeline.
It also compares simultaneous native and software pictures of the same moving
object to catch cross-camera playback offsets.
`--sync-recovery` additionally runs the RTSP source with frame intervals 20% longer
than its advertised media clock and pauses it for four seconds. It verifies that native and software
boxes stay aligned and fresh motion results return in the same stream epoch.

`--live-latency` delays only one viewer's native fragment consumer for four seconds,
twice. It checks that native and software pictures catch up automatically without
reconnecting or using the playback controls, then return to normal speed. The
unit simulation additionally repeats stalls across four minutes of playback and
checks monotonic, bounded cursor advancement and source pace compensation.

`--queue-recovery` delays actual WebSocket acknowledgements until a small test
queue overflows. It checks forced Native, forced Browser software decoding and
Automatic, including the retained stale picture, retry countdown, exported queue
diagnostics and automatic recovery with the same decoder without JPEG or reload.
It also verifies that network restoration starts a fresh live epoch.

`--benchmark` currently uses Windows process CPU APIs. It compares three identical
640×360 H.264 cameras at 10 fps in one local Chrome viewer, without recording,
detection or rewind, warming each mode before sampling for 30 seconds. It samples
the server, its FFmpeg children, and the isolated Chrome process tree separately;
the RTSP generator is excluded.

## Decoder source and distribution

The bundled decoder-only WebAssembly binary is about 2.3 MiB. It is loaded only
for Browser software decoding, with one worker/decoder per camera. Its initial
memory is 64 MiB and can grow with the stream's reference-frame requirements;
this is client memory, separate from the server's rewind budget.

The decoder uses FFmpeg 8.0.1 under LGPL-2.1-or-later, built without GPL/nonfree
components, encoders, networking or demuxers. The browser parses only the server's
fragmented MP4 output. The module is independently replaceable, and published
servers include the verified corresponding FFmpeg source archive, wrapper, build
script and licenses, following [FFmpeg's distribution guidance](https://ffmpeg.org/legal.html).
See [the decoder README](../src/SecurityCameraViewer.Web/wwwroot/vendor/camera-decoder/README.md)
and [build script](../tools/browser-decoder/build.sh). The first publish fetches
the pinned source archive; subsequent publishes use the verified intermediate
copy. Playback never downloads that archive.
