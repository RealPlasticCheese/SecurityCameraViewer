# Live camera audio

The maintained web viewer can play synchronized live video and microphone audio from any configured RTSP camera whose streams the installed FFmpeg can decode. No camera-brand integration, cloud account or recording setting is required. Listen is one-way reception; optional [push-to-talk](push-to-talk.md) is a separate, capability-gated feature for supported camera speakers.

## Listening

1. Each camera's bottom bar shows its audio codec (for example, **AAC Audio**) and a small **Audio OFF** button when audio is available, including in **All cameras**. Cameras without an audio track show **No Audio** and no button. Audio is off by default.
2. Click **Audio OFF** to turn that camera's sound on; the button changes to **Audio ON**. Its picture switches to synchronized video and sound once media arrives. An ordinary click mutes other cameras already playing audio in this browser. **Ctrl+click** to keep them audible.
3. Click **Audio ON** to turn sound off and release that camera's session. Other cameras muted by your selection keep their synchronized video and display **Audio OFF**; clicking that button resumes sound and mutes peers unless Ctrl is held. The menu offers the same toggle for the focused camera and **Volume** for all listening cameras.
4. Returning to the grid, focusing another camera, resizing or rearranging tiles preserves listening. If the browser blocks sound, the button returns to **Audio OFF**; click it again to retry. Failure details are available in its tooltip, accessible status and the menu.

These controls affect this browser only. They do not change saved recordings or the camera's microphone settings. Closing the page, disconnecting the viewer, changing quality/configuration, losing the server connection or entering rewind playback stops all live listening sessions. After these changes, turn audio on again.

The server automatically checks each camera's main-stream RTSP description. Normal descriptions do not start an audio decoder or play the stream. If the only advertised audio is marked receive-only, the server checks whether the camera actually sends audio by decoding one audio frame. This handles cameras such as Vatilon H83 that incorrectly mark their microphone track receive-only. The check uses a temporary main-stream connection, sends no microphone audio, and ends after ten seconds if unsuccessful. Only received, decoded samples enable listening; failures remain **Checking audio** and retry. No manual override is needed. **Checking audio** appears until that check succeeds; unreachable cameras remain unknown and retry rather than falsely reporting **No Audio**. Motion-activated cameras reuse their shared availability checks and wait for activity. Discovery is repeated when the camera configuration reconnects. Demo cameras show **No Audio**.

The initial codec label comes from the camera's description. When listening, the label follows the audio actually delivered to the browser (AAC after conversion). Unrecognized source codecs display **Audio** without guessing a type. If the delivered stream has no audio, the footer changes to **No Audio**, its button disappears and the silent session is released. A built-in microphone does not guarantee exported RTSP audio; check the camera's RTSP/microphone settings if it is absent.

## Compatibility and resources

If sound becomes choppy on a slower viewing device, enable **Use lighter video while listening** in the viewer menu. This browser then requests the existing H.264 compatibility conversion (up to 720p, approximately 15 fps) instead of decoding the original main-stream video alongside the audio. The choice is saved in this browser and switching it restarts active listening sessions while retaining selected cameras, mute and volume choices. Other browsers, recording and rewind keep their existing quality. Compatibility listeners on the same camera share an on-demand encoder, separate from original-quality listeners and rewind; it stops when the last compatible listener leaves. Conversion uses additional server processing, so compare the result on your server and viewing device. It cannot fix every cause of choppy audio.

The browser needs MediaSource or ManagedMediaSource support with fragmented MP4 decoding for the source H.264 or H.265 video and, for sound, AAC-LC audio. Unsupported browsers show a message and retain JPEG viewing. No browser extension is required. FFmpeg on the server must include the `libx264` and `aac` encoders as well as decoders for the input camera streams.

Live listening always uses the main stream, even when grid viewing uses substreams. By default, the server copies supported H.264/H.265 video at its original resolution and converts the first available audio track to mono 48 kHz AAC at 64 kbit/s. Unsupported input and the lighter-video option use the existing baseline H.264 conversion, no larger than 1280 by 720, at approximately 15 fps or less. Video and audio travel together in fragmented MP4 over a same-origin WebSocket. Source keyframes and approximately one-second fragments introduce buffering delay; this is not a zero-latency intercom.

Each camera with listeners uses one additional RTSP connection and encoder, shared by up to 16 listeners on that camera. Different selected cameras use separate encoders. Account for the server's CPU and the camera/NVR's RTSP connection limits. The last listener leaving stops that encoder. Muting alone keeps it running so video stays synchronized and sound can resume immediately.

Media stays in memory: there are no live recording files, disk segments or temporary media spools. Each server listener and browser receive queue is limited to eight messages and 4 MiB; live media fragments are limited to 2 MiB. A slow listener disconnects and reconnects with fresh initialization and a current keyframe, without holding up others. The browser prunes old playback data and seeks toward the live edge if it falls behind.

Recording remains independent. Listening works with recording disabled; muting or ending a live session does not disable recording or remove audio from clips. Recording retains its existing source-video-copy behavior rather than using the live encoder.

## Availability and access

Motion-activated cameras use the same authenticated availability checks as viewing and recording. While a camera returns an idle 404, listening waits without starting FFmpeg. Listening does not wake the camera. On sleep, loss or restart, the browser discards the previous buffered media before accepting a new stream epoch, preventing replay of old sound. Interrupted streams retry while listening remains selected. See [battery camera behavior](battery-cameras.md).

The endpoint is `GET /api/cameras/{channel}/live` upgraded to WebSocket (HTTP/2 CONNECT is also mapped). It requires the same `read` access as viewing when authentication is enabled and enforces the application's same-origin policy. Revoking access closes active listeners, including those waiting for sleeping cameras. Camera credentials remain on the server; browsers receive only media and fixed status/codec metadata.

## Verification

```powershell
dotnet publish src/SecurityCameraViewer.Web -c Release -o artifacts/live-web
dotnet run --project tests/SecurityCameraViewer.Web.Tests -c Release -- artifacts/live-web/SecurityCameraViewer.Web.dll --live-only
```

The focused suite uses loopback RTSP fixtures with G.711 audio and without audio. It validates playable H.264/AAC output and non-silent decoded audio, late joins, shared encoder lifetime, slow listeners, connection changes, sleep/wake recovery, same-origin protections and read-access revocation. It also verifies that listening does not enable recording. The full web suite includes these tests. Run `dotnet run --project tests/SecurityCameraViewer.Web.Tests -- --audio-metadata-only` for SDP parsing, automatic discovery, retries and cancellation. Run `node --test tests/web-live-media.test.cjs tests/web-live-audio-controls.test.cjs` for player lifecycle, audio selection and grid/focus controls. The real-browser checks in `node tests/test_live_audio_browser.mjs artifacts/live-web/SecurityCameraViewer.Web.dll` use generated RTSP fixtures to verify simultaneous MSE audio playback, modifier clicks, ON/OFF and volume, upfront audio availability and footer layout and session cleanup.
