# Camera push-to-talk

Focus a camera to use its video overlay. **Hold to talk** appears only after the server verifies a compatible ONVIF RTSP audio backchannel and negotiates its TCP transport. A microphone, incoming audio track, or `200 OK` response alone does not enable the button. There is no camera-brand allowlist.

## Using the speaker

1. Use **HTTPS with a browser-trusted certificate**, or access the server through localhost. Ordinary HTTP on a LAN address does not allow browser microphone capture. Listening and JPEG viewing remain available without microphone support.
2. Connect the cameras and focus one with fresh live video. Capability discovery is automatic while focused; sleeping or unavailable cameras are not probed until video resumes. Results are transient, shared across browsers and rechecked about every 15 seconds while focused. They reset on connection/configuration changes.
3. Hold **Hold to talk** with the primary mouse/touch pointer, or focus it and hold **Space** or **Enter**. Grant microphone permission when asked. If the permission prompt interrupted the press, release and hold again after granting access.
4. Speak while the overlay says **Talking… · release to stop**. Release to stop microphone capture and close the speaker connection. There is no toggle-to-latch mode.

Only one browser can use a given camera speaker at a time. Other browsers can keep viewing and listening; a competing talk request is rejected. A browser can talk to only one camera. It does not need to turn Audio ON first. During a press, that browser's live listening audio is temporarily suppressed to reduce feedback; its previous mute setting is restored afterward. Microphone audio is never played through local speakers.

Losing pointer capture, keyboard focus, window focus, switching cameras, returning to the grid, hiding/leaving the page, disconnecting the viewer, changing configuration/quality, microphone loss, or a connection error stops capture. Both the browser and server limit a press to **30 seconds**; release and hold again for another press. The server also stops idle microphone senders and revoked credentials. Talkback never automatically restarts microphone capture after an error.

## Protocol support

This implementation supports explicit ONVIF audio speaker tracks advertised by a DESCRIBE request with `Require: www.onvif.org/ver20/backchannel`. The track must have `a=sendonly` (directly or inherited from the SDP session), a usable control URI, and mono **G.711 PCMU (μ-law) or PCMA (A-law) at 8 kHz**. Static and valid dynamic RTP payload types are supported. SETUP must negotiate **unicast RTP/AVP/TCP interleaving**. Discovery sets up and tears down a session without PLAY or audio; an actual press revalidates the track, sets up the transport and starts PLAY before sending any audio.

Unsupported codecs, stereo/rate combinations, UDP-only transports, SRTP/RTSPS, and vendor-specific talk APIs are not implemented. Unsupported devices retain their existing viewing/listening behavior without a Talk button. RTSP control URLs and Content-Base must stay on the configured camera authority; redirects, different hosts/ports and device-advertised embedded credentials are rejected.

The browser uses AudioWorklet to downsample microphone audio to 8 kHz mono, sending 20 ms signed 16-bit PCM frames over a same-origin WebSocket. The server compands these into G.711 RTP, with sequence numbers, an 8 kHz clock, RTCP sender reports and authenticated session keepalives. Media stays in memory: no outgoing audio recording, disk spool or temporary audio file is created. Recording configuration and workers remain independent. A camera's own microphone may naturally hear its speaker; this feature does not remove that sound from recordings.

Buffering is deliberately small: the worklet retains at most two outstanding frames, browser network backlog is bounded, and the server processes one fixed 320-byte frame at a time with paced RTP writes. Backpressure or stale delivery stops the press instead of replaying queued speech. Packet delivery and camera speaker buffering still introduce some delay; release cannot retract packets already sent.

## Permissions and endpoints

With integration authentication enabled, capability reads require **read** and sending audio requires **control** (or **admin**). Read access alone cannot activate a camera speaker. The capability response includes `canTalk` so controls can explain missing permission. The existing browser management page still requires an administrator session.

- `GET /api/cameras/{channel}/talkback`: transient capability/status metadata, without RTSP credentials or control addresses.
- `GET /api/cameras/{channel}/talk`: WebSocket upgrade; HTTP/2 CONNECT is also mapped. It requires a recent verified capability and an exclusive speaker lease.

The server sends a fixed PCM-format ready message. Only exact 320-byte binary audio frames are accepted; close the socket to stop. Sessions continuously recheck control access, including while waiting for the first frame. Existing same-origin protection applies. Camera credentials remain server-side and are never placed in browser messages, outgoing audio files or process arguments.

See the [ONVIF streaming specification, section 5.3](https://www.onvif.org/specs/stream/ONVIF-Streaming-Spec.pdf) for backchannel negotiation.

## Verification

```powershell
dotnet publish src/SecurityCameraViewer.Web -c Release -o artifacts/talkback-web
dotnet run --project tests/SecurityCameraViewer.Web.Tests -c Release -- artifacts/talkback-web/SecurityCameraViewer.Web.dll --talkback-only
node tests/test_talkback_browser.mjs
```

Loopback tests cover Digest-qop authentication across methods, advertised capabilities, unsupported/malicious controls, transport negotiation, static/dynamic G.711 payloads, RTP clocks, RTCP and independently decoded waveform accuracy. Web tests cover speaker exclusivity, idle/maximum-duration limits, malformed frames, connection changes, permission separation and revocation. The browser lifecycle tests execute the production controller/worklet with synthetic streams, including late permission results, keyboard/pointer release and backpressure. They do not open a real microphone or contact real cameras.

For a real-browser AudioWorklet smoke test, run `tests/rtsp_fixture.py` with `RTSP_FIXTURE_PORT=18554` and `RTSP_FIXTURE_TALKBACK_CHANNELS=1`, then start the published web server with `--demo true`. Copy `tests/talkback-browser-harness.html` and `tests/talkback-browser-harness.js` into that test publish's `wwwroot`, visit `/talkback-browser-harness.html` on localhost and run its synthetic test. It checks PCM delivery and release cleanup using an oscillator-generated stream, not a microphone. These test pages are not part of the application's publish; do not deploy them.
