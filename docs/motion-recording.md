# Web motion recording design

For cameras that sleep between events, see [battery cameras](battery-cameras.md). The web server also supports optional recording whenever their RTSP video is available, without software motion detection.

The recording engine lives in `SecurityCameraViewer.Recording`, a platform-neutral .NET library. The web server hosts its recording workers. Local CPU inference uses the Microsoft ONNX Runtime NuGet package; Intel GPU inference uses an optional externally installed OpenVINO runtime.

## Responsibilities and extension points

| Type | Responsibility |
|---|---|
| `IFrameMotionDetector` | Common asynchronous analysis contract for pixel and AI detectors. |
| `IMotionDetector` | Analyse one borrowed grayscale frame and report motion, optional per-zone scores, changed area, warmup and schedule status. No recording or UI access. |
| `IMotionDetectorFactory` | Name the implementation and create an independent detector for each camera/connection. |
| `AdaptiveBackgroundDetector` | First concrete algorithm: adaptive background, illumination compensation, spatial noise rejection, warmup and debounce. |
| `MotionRegionMask` | Reusable rectangle/polygon rasterization and weekly timezone-aware inclusion/exclusion policy. |
| `IMotionFrameSource` | Deliver bounded grayscale or RGB analysis frames. |
| `IEncodedVideoSource` | Deliver initialization data and compressed keyframe fragments without disk spooling. |
| `FfmpegCameraMediaSource` | Implement both media-source contracts through private FFmpeg pipes. |
| `FragmentedMp4` | Bound and parse MP4 fragments with video and optional audio; calculate durations from the video track. |
| `VideoBuffer` | Evict compressed video by time and byte budget, entirely in managed memory. |
| `RecordingCamera` | Coordinate sources, a detector, a RAM buffer and an event sink; handle quiet tails, splitting, failures and cancellation. |
| `MotionAction` / `MotionActionRouter` | Define reusable Record/Webhook actions and resolve zone selections or camera defaults. |
| `IMotionActionCatalog` | Resolve saved action definitions for background webhook delivery. |
| `IRecordingEventStore` / `IEventWriter` | Admit triggered events, append fragments, complete interrupted or normal captures. |
| `FileRecordingStore` | Manage event files, indexing, finalization, protection and retention. |
| `IRecordingCapturePolicy` | Decide event buffering, optional detection and scheduled capture independently of storage. |
| `IRecordingCatalog` / `SqliteRecordingCatalog` | Persist recording metadata and timestamped activity markers; query indexed camera/time/zone history. |
| `RecordingFileLayout` | Resolve owned media paths beneath the archive root. |
| `RecordingService` | Own web-host startup/shutdown and recording/connection configuration. |
| `RecordingSettingsStore` / `RecordingEndpoints` | Persist configuration and expose the web API respectively. |
| `ICameraEventPublisher` | Publish camera, motion and recording transitions independently of webhook/SSE transport. |

`RecordingCamera` depends on the narrow detector, media-source and event-sink interfaces. Configuration and storage do not depend on the concrete detector. The web composition root registers the concrete adapters, following dependency inversion; additional algorithms can be added without editing the recorder or browser controls. Interfaces are split by consumer responsibility rather than a single camera/storage interface.

## Detection modes and local AI

Each detection template selects **Adaptive background subtraction**, **Adaptive background subtraction + AI**, or **AI object detection** in Motion detection template settings. Existing settings keep adaptive detection at 320 × 180, five analysis frames per second, sensitivity 60 and minimum area 1%. Analysis resolution (320/640/960 pixels wide) and frame rate (1–10) are now configurable. The `?` button beside Close in Motion & recording opens the [configuration guide](../src/SecurityCameraViewer.Web/wwwroot/motion-guide.html), including tuning for small distant subjects and the N150/four-camera starting configuration.

Hybrid mode has independent gate controls, defaulting to sensitivity 80 and minimum area 0.1%. A confirmed pixel-motion zone admits AI checks for three seconds after its last motion. Selected object presence confirms only zones with recent motion; a parked car and unrelated movement in the same zone can qualify. Hybrid mode cannot recover motion the gate misses. AI-only mode checks selected object presence, including stationary objects. Neither mode implements object identity tracking or proves object movement. The default People and all vehicles preset triggers people, bicycles, cars, motorcycles, airplanes, buses, trains, trucks and boats, with no ignored types. The selectable original preset retains person/car triggers. Both use confidence 45%, one AI check per second and CPU. Existing saved class selections are preserved until a preset is chosen and saved. Built-in models expose all 80 COCO classes, including animals. Each type can Trigger, Ignore (shown in overlays without activating actions), or have No action. Ignoring an animal never vetoes another triggering person. Objects belong to zones by bounding-box centre, after exclusions and schedules. Existing recording/action/arming policy still applies. See [model descriptions, presets and object rules](ai-models.md) for configuration and adding compatible models without source changes.

`ConfigurableMotionDetectorFactory` selects the per-camera policy. `AiMotionDetector` owns camera-local masks and gate state. `IAiModelRegistry` discovers model descriptions; `IAiModelMetadataReader` supplies optional embedded labels/input size. `IAiInferenceService` schedules inference independently of recording. `IObjectModelAdapter` prepares images and decodes model output; `IModelSessionFactory` chooses portable ONNX Runtime CPU or optional OpenVINO GPU. One shared inference lane limits concurrent CPU/GPU work, CPU inference uses two threads, and each FFmpeg source keeps one waiting frame, dropping older waiting frames. The model cache is bounded to six model/device sessions regardless of registered-model count; cameras using the same model/device share it. No browser session is required. Available-video recording bypasses detection entirely.

AI consumes RGB at a minimum 640 × 360 source resolution. YOLOX Nano/Tiny adapters use 416-square, top-left padded BGR input with unnormalized float values and decode raw grid outputs. YOLO26 Nano uses 640-square, centred letterbox RGB normalized to 0–1; raw `[1,84,N]` and end-to-end `[1,N,6]` COCO outputs are supported. Nonmaximum suppression is class-aware. Model input/output contracts are validated, and missing or incompatible models report analysis errors without treating unconfirmed pixel motion as an AI event.

### Reusable detection templates

Open **Motion detection templates** in the hamburger menu to use the separate **Motion detection template settings** form. The **Edit this template** link beside a camera or inclusion-zone assignment opens the selected template directly, including the camera template when a zone inherits it. Define and name detection policy once, then choose a **Default detection template** for each camera. Inclusion zones can inherit that choice or select a different template; exclusion zones remain masks and apply to all detectors. Camera substream selection, recording modes, schedules and actions stay with cameras/zones.

Existing inline settings migrate deterministically into templates, deduplicating identical policies while preserving tuned values and assignments for disabled cameras. If a large legacy collection reaches the 64-template limit, excess distinct policies remain inline as **Existing camera settings (unassigned)** until a template is assigned or space becomes available. Migration is saved with the next settings save. **Save templates** applies shared policy changes to every saved assignment. Camera/zone assignments and recording controls apply with **Save recording settings**. Opening the template form preserves pending recording edits; saving or cancelling templates returns to the recording form without saving those edits. Cancel discards template edits. The template form requires reopening before saving if templates or the active camera configuration change elsewhere. Duplicate a template to tune one camera or zone independently. Assigned templates cannot be removed until their assignments are changed.

Install optional starters: **Existing adaptive behaviour**, **Outdoor foliage**, **Small distant subjects**, and **Shadow reduction**. These are starting points requiring testing on your footage. They do not automatically replace assignments. Export one or all templates to a version 1 JSON file; import adds fresh template IDs and preserves existing assignments. Files contain detection policy only, without credentials, camera identities, zones or recording paths. Up to 64 templates per camera configuration and 8 MB per imported file are supported. Referenced AI models must be registered on the receiving server.

Zones using the same template share a detector. Distinct zone templates share one camera decoder, using the highest required resolution/frame rate and colour when needed. Each worker downsamples and limits its own slower analysis rate. AI inference still uses the bounded shared model service. Camera `changedPercent` is the largest monitored-area score among template workers; individual zone scores and optional diagnostics identify each policy. Overlay observations remain bounded to 128 per camera, with confirmed detections prioritized. More distinct templates on a camera increase analysis work and memory.

### Optional adaptive filters

Defaults preserve the existing detector: no learned noise, smoothing or shadow filtering, region persistence 1 (off), average brightness compensation, three-frame confirmation, four-pixel candidate display and diagnostics off. Adaptive and hybrid templates can configure:

- **Learn normal background variation**: bounded per-pixel noise statistics learned during warmup and from background pixels. Noise allowance is 1-6 standard deviations (default 3). Foreground does not continually raise the learned noise floor.
- **Light smoothing**: a mask-aware 3-by-3 average reduces small compression artifacts without leaking excluded timestamps across mask boundaries. Small subjects can also be smoothed away.
- **Region persistence**: 1-10 nearby consecutive observations, with 1 disabling the filter. Matches the largest 128 spatial components with bounded history, resets on frame gaps and mask changes, and is not object identity tracking. Region persistence precedes the separately configurable 1-10-frame area confirmation. Both delays depend on analysis rate.
- **Lighting compensation**: existing mean shift, or robust brightness/contrast estimation from stable background pixels. Large brightness shifts retain exposure-recovery suppression.
- **Colour shadow suppression**: filters moderate darkening with similar normalized colour, requiring colour frames at the selected adaptive resolution. Minimum brightness ratio 0.3-0.95 and colour tolerance 0.01-0.3 tune this heuristic. It can also reject similar-coloured dark objects; grayscale replacement sources explicitly report missing colour rather than silently disabling the option.
- **Minimum candidate display size**: 4-10,000 analysis pixels. Changes only emitted overlay observations; motion decisions, recording, zone actions and hybrid gating use the unchanged qualifying mask.
- **Diagnostics**: reports monitored moving-area percentage, threshold, confirmation and region progress, brightness shift/gain, shadow count, and qualification reason. Camera status retains diagnostics even when no box survives the display filter. Component moving area remains a different denominator/quantity from the total zone score.

Test background rejection and real moving subjects at the intended distance before relying on more aggressive filtering. Hybrid uses the same adaptive options, so a filter that suppresses legitimate motion can also prevent AI checks.

## Model installation and execution devices

The included ONNX Runtime supports CPU inference on Windows/Linux x64. Models are external deployment assets, not downloaded at server startup or bundled with the application. The default folder is `models` beside the server application; override with `--ai-model-directory /absolute/path`. `/api/recording/detection` reports methods, installed model files, classes, GPU devices and the configured model folder; it requires administrator access when authentication is enabled.

Install official Apache-2.0 YOLOX assets with Python's standard library:

```sh
python3 tools/install-ai-models.py --directory /opt/security-camera-viewer/models --model yolox-nano
python3 tools/install-ai-models.py --directory /opt/security-camera-viewer/models --model yolox-tiny
```

The downloads are the [official YOLOX ONNX releases](https://github.com/Megvii-BaseDetection/YOLOX/tree/main/demo/ONNXRuntime); filenames must be `yolox_nano.onnx` / `yolox_tiny.onnx`. For YOLO26 Nano, use a fixed batch-one, 640-square float32 COCO export named `yolo26n.onnx`; [Ultralytics export instructions](https://docs.ultralytics.com/modes/export/) and AGPL-3.0/Enterprise licensing apply. Do not install untrusted model files. Choose **Refresh models** after installing/replacing files; it reloads descriptions and clears sessions without changing saved camera policy. Place a compatible model's `*.model.json` description beside its ONNX file to register additional choices; see [the description format](ai-models.md).

With Ultralytics installed separately, a compatible export is:

```sh
yolo export model=yolo26n.pt format=onnx imgsz=640 batch=1 dynamic=False simplify=False nms=False half=False
```

Copy the resulting `yolo26n.onnx` into the server's configured model folder.

**CPU** uses the included runtime. **GPU** explicitly requires an available OpenVINO Intel GPU and reports errors if it cannot execute. **Automatic** tries the available GPU, then falls back to CPU; status reports the actual device and fallback. AI errors keep the video buffer running but suppress new automatic triggers. Existing events retain normal quiet-tail behavior.

For Intel GPU acceleration, install the [OpenVINO runtime](https://docs.openvino.ai/2026/get-started/install-openvino.html) and Intel compute driver for the Linux distribution. The service user needs GPU render-device access; containers need the device passed through. Configure the runtime library environment from `setupvars.sh` for the actual server service process, including dependent libraries. `SCV_OPENVINO_LIBRARY` can specify an absolute path to `libopenvino_c.so` or `openvino_c.dll`. Windows hosts can use OpenVINO's Windows runtime/environment similarly. Restart after runtime/driver changes. GPU availability is discovered from the runtime, not inferred from the host CPU name. This branch must be benchmarked on the target N150/Linux host before choosing final rates for four cameras.

## Plugging in another detector

### Live detection observations

`MotionSample.Detections` exposes a common `DetectionObservation` contract: normalized bounds, kind, associated zones, qualification flag, trigger/ignore disposition, capture/expiry timestamps and a collection of `DetectionEvidence`. Each evidence source identifies its method, optional model, capture time and typed text/numeric `DetectionAttribute` values with units. Ignored objects retain their class/confidence evidence, have `confirmed: false`, and use dotted gray overlays with an Ignored label. Detection, persistence, camera playback and metadata presentation remain separate. New detector/model adapters can reuse the API and renderer by returning this contract, including additional named attributes without browser model-specific branches.

Adaptive detection extracts four-connected components from its existing spatially filtered pixels after warmup/illumination checks, retaining the 64 largest components of at least four filtered pixels. Reused scratch arrays bound working memory. Small changes below the motion threshold/debounce remain diagnostic candidates; extraction does not alter the detector's motion decision. Component moving-area percentages use the full active monitored mask as denominator. AI returns up to 64 selected, included objects; cached results retain their original capture time. Hybrid objects retain qualifying gate evidence and AI evidence, while pixel boxes remain candidates. The total is bounded to 128 observations per camera. Expiry is 1.5 seconds or three adaptive-frame intervals, whichever is longer; AI expiry is two seconds or two inference intervals, whichever is longer.

`GET /api/detections` and `/api/v1/detections` provide current observations, relative age/remaining lifetime, configuration ID and safe method/model labels. Read/admin scope applies, with existing authentication enforcement. Model installation paths and private configuration are absent. Snapshots coordinate with configuration transitions, suppress expired observations, and do not initiate decoding or inference. Live observations are omitted from the administrative recording snapshot and are not persisted in recording events.

The viewer's **Show detection overlays** preference defaults off and is saved in that browser. **Show detection metadata text**, directly beneath it, independently hides or shows labels and detail panels while leaving detection boxes visible; it defaults on and is also saved in that browser. A single shared poll runs at most once per 500 ms, only while enabled, visible and connected; in-flight requests are bounded, canceled on invalidation and guarded against stale camera/configuration results. A reusable per-camera renderer maps normalized geometry through the same picture/zoom projection as JPEG and live video. It renders labels and hover/tap/keyboard details as text, clears on unavailable video and releases timers/observers on disposal. Overlays add no inference requests and are not burned into recordings.

Native video, browser software decoding and focused live sound automatically select detection samples for the displayed footage. MP4 presentation timestamps are mapped to the server clock using fragment production timestamps taken before transport buffering. Recent production anchors recalibrate each fragment rather than retaining a lifetime minimum offset: a paced source or a pause can change the relationship between media time and elapsed wall time. Three consistent production intervals estimate actual source pacing; neighboring anchors reject isolated delayed flushing. Fragment calibrations are retained through the existing live buffer window so later clock changes do not shift older buffered pictures forward. Native video uses compositor frame callbacks where available; the animation-frame fallback uses the current playback position. Software decoding reports each displayed frame's presentation timestamp. Detection samples include empty results, so an object entering or leaving the view does not get a box from a later frame. Expiry follows the displayed footage, and reconnects clear old timing. No user delay adjustment or additional decoding is required. Separate camera main/substream input delays, low analysis rates and different fields of view can still affect spatial alignment; this mechanism corrects application playback buffering rather than claiming a camera-provided capture clock.

Adaptive motion boxes describe changed background regions rather than tracked people or vehicles. An object's vacated position can remain foreground while the background model relearns it; waves, shadows and moving foliage can also produce boxes. Detection details show capture times, while the recording status and history samples distinguish a stalled analysis stream from an overlay timing problem.

The detection response also includes `history` per camera, with an epoch `id` and sampled `frames` (`capturedAt`, `publishedAt`, `detections`). `?since=<generatedAt from the previous response>` requests new publications, including inference completed late for an earlier frame. The inclusive cursor boundary is deduplicated by the browser. The existing `detections` array continues to contain only unexpired current observations. History retains metadata, including observations whose wall-clock expiry has passed, so buffered video can still display its corresponding detections. Images are never retained by detection history.

Server resource budgets are configurable through `DetectionOverlay:Seconds` (default 30 seconds) and `DetectionOverlay:MaximumSamplesPerCamera` (default 600 samples). Environment variables use `DetectionOverlay__Seconds` and `DetectionOverlay__MaximumSamplesPerCamera`. The browser receives these budgets and prunes its metadata history accordingly. When footage is older than retained history, boxes remain hidden until matching samples are available. These budgets do not alter detection decisions, recording or video decoding. Server JPEG previews use the current detector observations because they do not have an MP4 playback buffer.

Implement `IMotionDetector` and `IMotionDetectorFactory`, then replace this registration in the web `Program.cs`:

```csharp
builder.Services.AddSingleton<IMotionDetectorFactory, MyDetectorFactory>();
```

`Create(CameraRecordingSettings, TimeZoneInfo)` returns `IFrameMotionDetector` and must return a fresh stateful detector for each call. Existing grayscale detectors can continue implementing `IMotionDetector`; its default asynchronous adapter invokes `Analyse`, which receives row-major grayscale pixels, dimensions, a monotonic time in seconds and the UTC capture/receipt time. The pixel buffer is borrowed; copy it only if retaining it is essential, and keep any queued work bounded. A grayscale detector should return quickly and never perform file or network I/O on this callback. Expensive inference should have its own bounded worker. Custom factories should update their return type to the common frame contract.

AI detectors implement the separate `IAsyncMotionDetector` contract, accepting an `AnalysisFrame` with RGB pixels and capture time, plus cancellation, without requiring an unsupported synchronous method. `RecordingCamera` disposes asynchronous detectors on mode/connection transitions. `IMotionFrameSource` retains its original grayscale contract and has a settings-aware overload; older replacement sources remain supported through its default adapter. Add a model definition and its adapter for new model families rather than editing recording/storage logic. `MotionSample.Ai` provides inference status independently of zone motion transitions.

Preserve configured zone/schedule semantics when replacing the algorithm. `MotionRegionMask.Update(width, height, capturedAt)` provides the reusable policy; a change in `Revision` signals that learned image state should be reset. `Pixels` is a mask for the entire normalized source image. The built-in implementation resets and warms up when the active zone set changes. A detector that supports different tuning should explicitly map the supplied sensitivity/minimum-area controls or extend the configuration/UI contract.

`ZonePixels` supplies each currently active inclusion zone's mask after exclusions. Return `ZoneMotion` values through the optional `MotionSample.Zones` property to support named-zone automation. Keep the supplied zone IDs unchanged. Aggregate-only detectors remain compatible and emit full-frame motion events. The built-in detector applies its debounce and minimum area threshold independently per zone. See [integrations](integrations.md) for temporary arming, external triggers, API permissions and event delivery.

## Capture and event lifetime

Each enabled camera opens one main-stream recording session. Event mode also opens an analysis session (substream by default); Continuous mode does so only when optional detection is enabled. Available-video mode bypasses analysis. The recording session copies video and automatically includes any audio tracks advertised by RTSP, encoding audio as AAC at 64 kbit/s per track for MP4 compatibility. Streams without audio remain supported. Analysis and the JPEG camera grid request video only. Optional [focused-camera live audio](live-audio.md) uses a separate on-demand main-stream session, shared by listeners on that camera. Browser activity cannot stop recording. See [continuous recording and archive storage](continuous-recording.md).

FFmpeg writes `empty_moov+frag_keyframe+default_base_moof` MP4 to stdout. The reader identifies the video track by its media handler and track ID, and uses its timescale and sample durations even when audio is the first track. The initialization header and complete `moof`/`mdat` groups retain all audio and video data together, preserving synchronization through buffering, event cuts and restart recovery. The ring's byte budget includes audio. MP4 box size is bounded before allocation; oversized or excessively long fragments cause a visible recovery error, not unbounded memory growth.

Motion confirmation in a zone with an enabled Record action starts an event at the next complete fragment. The initial buffer selection includes keyframe/arrival allowance so it can retain the requested period preceding the trigger when available. The actual trigger time is stored separately from the clip start. A clip can start earlier than requested and end later because stream copy cuts on keyframes. Startup, memory eviction and reconnect can reduce the available lead-in.

Event mode creates files only on a recording trigger. Continuous and available-video modes create consecutive segments while their recording schedule allows capture. All modes use the same event writer, MP4 finalizer, protection and retention. The writer saves initialization and complete fragments without an unbounded write queue. Event mode retains pre-event overlap when splitting long activity; scheduled modes rotate at fragment boundaries without duplicating footage or restarting RTSP. A serial finalizer copies compressed video and AAC audio into an MP4 with normalized timestamps and fast-start metadata, creates a thumbnail, then removes the capture file. Video is not re-encoded.

The embedded SQLite catalogue indexes completed recordings; only active/finalizing metadata is kept in capture memory. Database writes occur on lifecycle transitions, activity markers and explicit user changes, never on arriving video fragments or status polls. A restart recovers complete fragments of interrupted captures and reconstructs their duration. Failed finalization keeps captured data downloadable. No MP4 joins across reconnects or codec changes are attempted. Legacy JSON metadata imports idempotently while its footage stays at its original path.

## Browsing and saved playback filters

Open **View recordings** to combine **Camera**, **Date (local time)** and **Triggered zones** filters. Expand the zone picker to check individual zones or select all zones under a camera. A recording matches if any selected zone appears in its initial or additional trigger metadata; Camera and Date also apply. With no checked zones, all recordings are eligible, including recordings without zone metadata. Filtering happens on the server before paging, so **Load older events** continues through matching recordings.

The picker includes configured inclusion zones and historical zones found anywhere in the current recording library. Stable zone IDs preserve matches after renaming; historical names are shown when a zone has been removed. Camera groups keep zones on different cameras distinct. The controls wrap for phone screens and the zone list scrolls with touch-sized checkbox rows.

**Clear all filters** resets Camera, Date, zone selections and the loaded preset selection. **Saved filters** loads a preset immediately. Choose **Save filter…** to name a new preset or select an existing one to replace. Presets store all three filter values, including a specific local calendar date. They are shared between browsers and persist across server restarts in `settings.recording-filters.json` beside the server's settings file (the filename follows a custom `--settings` path). Each camera configuration has its own preset list. Saving presets does not restart recording workers.

The browser uses `POST /api/recordings/search` with channel, inclusive `since`, exclusive `before`, page limit and camera/zone selections. `GET /api/recordings/zones` supplies historical zone groups; `GET` and `POST /api/recordings/filters` list and save presets. Search requires read access; saving presets requires administrator access when authentication is enabled. Invalid or duplicate preset names are rejected, and replacement requires the existing preset ID. A configuration supports up to 100 presets and each preset supports up to 1024 zone selections.

## Retention and failure behaviour

New files use `<configuration-id>/<camera-id>/<UTC-year>/<month>/<day>/<UTC-start>_<UUID>` beneath the selected recording folder; `archive.db` stores metadata and markers. Imported files keep their legacy `events` paths. Only catalogue-owned files are candidates for deletion; unrelated files are preserved. Age cleanup and quota admission delete oldest unprotected completed/failed recordings in the active configuration. Active/finalizing recordings reserve twice their capture size for remuxing; protected clips remain counted. A 64 MiB free-space reserve prevents writing into a nearly full filesystem. Actual I/O errors stop the affected capture and are reported separately from live-view status.

Recording workers restart their independent sessions after failures (2, 5, 10, 20, then 30 seconds). A stopped analysis stream or absent keyframes triggers recovery. No browser client is needed to consume frames. Shutdown cancels and awaits both FFmpeg processes and marks active events interrupted; startup finishes them. Changes to recording/connection settings deliberately restart these workers. Recording defaults off, so merely upgrading the viewer does not begin disk writes.

OS paging is outside the application buffer policy. Set the ring limit for available RAM and camera bitrate; the limit does not include codec decoder memory or a bounded fragment currently being parsed/written.

## Zone schedule semantics

Zone coordinates range from 0 to 1 across the full picture, allowing the main-stream preview and scaled analysis stream to share geometry. There are at most 32 named zones per camera, 64 vertices per polygon, and 16 weekly rules per zone. No rules means 24 hours every day. A rule has Sunday-based weekday numbers (Sunday=0), an all-day flag, and optional `HH:mm` start/end times.

Rules form a union, start is inclusive, end exclusive, and equal endpoints are rejected (choose all-day explicitly). Overnight rules select the starting weekday; Saturday morning may therefore be covered by a Friday night rule. Rules use an explicitly saved timezone; DST repeated local hours match twice and skipped hours do not occur. Inclusions form a union, exclusions subtract from it. If no inclusion was configured, default to the whole frame. If inclusions exist but none are currently active, monitor no pixels. A schedule transition never truncates an already-open event's quiet tail.

## Reusable zone actions

Open **Motion & recording settings → Reusable actions**. The built-in **Record** action is present by default. Add a named **Record** or **Webhook** action, then select it under **Camera default actions** or a zone's **Custom actions**. All Record actions use the shared recording policy and overlap into one clip. Action order has no sequencing meaning.

New and existing zones inherit camera defaults unless explicitly overridden. A custom selection with all checkboxes cleared means no actions. Without inclusion zones, the monitored full frame uses camera defaults. Exclusion zones never execute actions. Both the overall **Enable motion detection & actions** setting and the camera's enable setting must be on, including for webhook-only monitoring. RAM buffering and the main stream remain active even without a Record action.

Zone names remain editable and new zones use `Zone 1`, `Zone 2`, etc. Existing names are preserved. Stable IDs maintain identity, so renaming leaves action assignments intact. Default webhook payloads and recording details display the names at the time of the event.

Webhook actions POST to an HTTP(S) URL once when confirmed motion starts in each zone. They have an optional JSON body (up to 16,384 characters) and a 0–3600 second cooldown, defaulting to 30 seconds. A blank body sends the standard motion event with camera and zone names; a custom body is static JSON without template substitution. Continuous motion sends once; cooldown expiry does not itself send another request. Cooldowns are independent per action, camera and zone.

Webhook-only zones create no clip. Entering another zone invokes its webhook even while recording is active. Only motion in zones with Record selected extends the quiet tail. Pausing an action disables new activations and holds pending deliveries. Remove all references before deleting an action; deletion cancels its queued deliveries. Settings changes restart workers as before. Explicit API recording triggers retain their existing behavior regardless of zone actions.

The JSON contract adds `actions` to recording settings, `defaultActionIds` to cameras (default `["record"]`), and nullable `actionIds` to zones. Omitted/null zone `actionIds` inherits; `[]` opts out. The reserved `record` definition cannot be deleted or changed to a webhook. Up to 64 actions are supported; unknown/duplicate references and invalid configuration are rejected before applying settings. Old settings need no manual migration.

Webhook actions use the existing bounded, durable dispatcher, without network or queue-file I/O in the capture loop. See [integration delivery details](integrations.md#zone-webhook-actions) for retries, headers and the relationship to event subscriptions.

## Verification

`SecurityCameraViewer.Recording.Tests` runs deterministic detector/mask/weekly-boundary checks and generated-video tests, then a four-camera authenticated loopback RTSP fixture through the actual web host. It verifies no idle recording directory/files, actual motion triggers, RAM lead-in, quiet tails, independent live-view controls, playable MP4s, range requests, restart recovery, protection, retention scope and invalid API input. `SecurityCameraViewer.Web.Tests` continues covering the existing playback/reconnect/browser-stream lifecycle. Use a real-camera soak test to establish suitable sensitivity, keyframe intervals, connection capacity, CPU and storage settings on the deployment machine.

Audio checks use an audio-first AAC MP4 with a different video timescale to verify track selection, buffered cuts, synchronized decoded audio and restart recovery. The web recording fixture mixes G.711 audio cameras with video-only cameras, verifying AAC conversion and optional audio mapping through the real RTSP capture pipeline. The battery lifecycle fixture verifies audio capture through wake, recording and stream end.

Action tests cover legacy defaults, settings round-trips, naming, no-action/webhook-only zones, overlapping recordings, independent activations, quiet tails and disarming. Integration tests POST to a loopback receiver and verify JSON bodies, cooldowns, retries, restart recovery and paused actions. Focused commands: `dotnet run --project tests/SecurityCameraViewer.Recording.Tests -c Release -- --unit-only` and `dotnet run --project tests/SecurityCameraViewer.Integration.Tests -c Release -- --actions-only`.

AI checks cover hybrid gating and zone correlation, stationary-object presence, rate limits, schedules/exclusions, model preprocessing/output formats, shared inference concurrency, explicit GPU failure and Automatic fallback/backoff. The RTSP fixture verifies RGB analysis, a missing model's visible error, and continued RAM buffering without unconfirmed events. An optional real-model smoke test accepts installed models and a 640 × 360 RGB24 image containing people: `dotnet run --project tests/SecurityCameraViewer.Recording.Tests -c Release -- --ai-smoke /path/to/models /path/to/image.rgb`. Add `--openvino` to exercise the native adapter on CPU; this does not benchmark an Intel GPU.
