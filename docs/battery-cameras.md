# Motion-activated / battery cameras (web server)

The web server can wait for cameras that publish RTSP only during activity. This is local-only: no Eufy account, cloud API, bridge, or subscription. Windows and Linux are supported web-server hosts.

## Setup and viewing

1. Enable NAS/RTSP in the camera's app and copy its exact stream address and separate RTSP credentials.
2. In **Cameras & configurations**, select **Motion-activated / battery camera**.
3. Set **Poll interval** between **0.1 and 3600 seconds**, with up to one decimal place (default **1 second**), then save and activate. For example, **0.1** = 100 ms, **0.5** = 500 ms, and **1.6** = 1600 ms.
4. Trigger physical motion and verify that video appears. A sleeping camera can fail a one-time video test even with correct settings.

Idle checks use authenticated RTSP DESCRIBE requests to the main stream. Viewing, optional live audio and recording share these checks; additional browsers do not increase polling. An idle 404 does not launch FFmpeg. The interval is the wait between completed checks; an unresponsive check can take up to three additional seconds. Shorter intervals reduce discovery delay but increase requests. Consumers do not poll while receiving video.

Tiles show **Waiting for activity** and retain the last image with its date and time by default. Set **Video timeout (seconds)** above **Portable configuration** in **Cameras & configurations** to replace that image with the waiting screen after the configured period without fresh video (for example, **300** seconds). **0** keeps it indefinitely. Fresh video restores the picture automatically. Before the first decoded image, the stream is explicitly unverified: a 404 alone cannot distinguish sleep from a wrong path. Rejected credentials and unreachable services remain errors. Viewing abandons a stalled session after eight seconds without frames. Continuous-camera recovery is unchanged.

**Check now** interrupts the polling wait. It checks availability, not camera wakefulness. No verified local-only wake command is implemented for eufyCam 2 Pro / HomeBase 2, so no Wake button is offered. Trigger physical motion instead. Check now requires administrator access when authentication is enabled.

To hear available microphone audio, focus the camera and choose **Listen to focused camera**. [Live audio](live-audio.md) waits while the camera sleeps and restarts with fresh synchronized video and sound when activity resumes. It does not wake the camera or replay buffered sound from its previous active period. Multiple listeners share one live encoder, separate from the JPEG viewer and recording sessions.

## Optional recording

In **Motion & recording settings**, enable recording globally and for the camera, then select **Record whenever video is available**. This option defaults off and requires motion-activated camera mode.

- Capture starts at the first complete fragment without software-motion warmup. Metadata identifies the trigger as `stream` / “Camera video became available”, not confirmed motion.
- Set its weekly **Recording schedule** using the configured recording timezone. No rules means all day, every day. Overnight rules finish the following day; end times are exclusive. Temporary arming/disarming applies.
- Motion zones, their schedules, and motion actions are not used in this mode. Leave the option off to retain software motion detection and its actions.
- A stream ending finishes the clip normally. Silent stalls have a 35-second recording watchdog, allowing the existing maximum 30-second keyframe interval. Shutdown, settings changes, and storage failures can mark a clip interrupted.
- Maximum clip length, retention, storage limits and protection apply. Long activity splits at fragment boundaries without gaps. This mode has no RAM pre-event buffer: pre/post-motion settings do not apply. External triggers admitted during active capture add timestamped markers without changing its schedule.
- Recording continues with browsers closed or live viewing disconnected. Active viewing and recording currently use separate RTSP media sessions; idle checks are shared. Available-video recording eliminates the additional motion-analysis stream.
- Recordings include audio when the RTSP stream provides it, encoded as AAC for saved MP4 playback. This behavior is shared with continuous-camera recording. The JPEG grid is silent; the focused-camera Listen control enables optional live sound independently of recording.

There is no footage from before the camera transmits. Polling and keyframe timing can miss the beginning of short events. Viewing from another app may also produce recordable activity. Switching the camera to Continuous clears its available-video recording option. Existing cameras default to Continuous.

All modes use the same SQLite catalogue and camera/date file layout. See [continuous recording and archive storage](continuous-recording.md) for playback, migration, backup and retention details. The camera connection type and recording mode are separate settings.

## Verification

```powershell
dotnet publish src/SecurityCameraViewer.Web -c Release -o artifacts/battery-web
dotnet run --project tests/SecurityCameraViewer.Web.Tests -c Release -- --battery-only
python tests/test_battery_cameras.py artifacts/battery-web/SecurityCameraViewer.Web.dll
```

Tests use a loopback H.264 fixture with authenticated 404 responses while sleeping and stream closure on sleep. They cover shared checks, cancellation, Check now, unverified URLs, authentication errors, schedules/arming, clip splitting, storage failures, playable finalized files, retained snapshots and restart persistence. Earlier physical testing confirmed 1080p H.264 at 15 fps through eufyCam 2 Pro / HomeBase 2; the new lifecycle's timing still needs verification on the actual installation.
