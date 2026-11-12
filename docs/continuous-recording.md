# Continuous recording and archive storage

Open **Motion & recording**, enable recording globally and for each camera, then choose its **Recording mode**:

| Mode | Capture behavior |
|---|---|
| Events | Motion or external triggers save the RAM pre-event buffer and post-event tail. |
| Continuous | Save consecutive segments throughout the recording schedule. No RAM pre-event buffer is needed. |
| Record whenever video is available | For motion-activated/battery cameras: save transmitted activity and keep the shared sleep/wake polling behavior. No software detection or pre-event footage. |

Existing event settings retain Event mode. Existing `recordWhenStreaming: true` settings retain available-video behavior. The API adds `mode` (`event`, `continuous`, `available-video`) and `detectDuringContinuousRecording` to camera recording settings; the legacy boolean remains accepted. Changing a battery camera to a regular camera clears available-video mode.

Continuous recording defaults to detection off. **Detect motion during continuous recording** enables the existing detector and zone webhook actions, and adds timestamped motion markers to the footage. Record actions do not create duplicate video or stop the archive during quiet scenes. External recording triggers admitted while a scheduled recording is active add markers; their requested duration does not extend that mode's schedule. Event triggers retain their existing duration and pre-event behavior. Detection zones have their own schedules; the separate recording schedule controls continuous capture.

Continuous and available-video modes share the **Recording schedule**. No rules means all day, every day. Rules use the saved recording timezone; overnight ranges belong to their starting day and end times are exclusive. Temporary arming overrides the schedule; disarming closes the current segment. Recording uses the active configuration only. A sleeping camera cannot supply footage before it wakes.

## Files and catalogue

All modes share one writer, MP4 finalizer, catalogue, protection and retention policy. New footage is stored as ordinary files:

```text
<recording-folder>/
  archive.db
  <configuration-id>/
    <stable-camera-id>/
      2026/10/09/
        20261009T020000Z_<UUID>.mp4
        20261009T020000Z_<UUID>.jpg
```

Dates and filenames use UTC; playback labels use the browser's timezone. Renaming or reordering cameras preserves identity. SQLite holds metadata, relative file paths, protection state, indexed camera/time/zone history and separate timestamped activity markers. It contains no video. Configurations sharing a folder have separate libraries and retention policies in the same catalogue. No database server or separate SQLite installation is required on Windows or Linux.

Legacy valid `events/<UUID>.json` entries import automatically without moving their video, changing recording IDs or losing protection. Explicit configuration ownership is preserved; older sidecars without a configuration ID belong to the configuration importing them. Repeated startup does not duplicate entries. Old sidecars remain alongside imported media; SQLite becomes authoritative for subsequent protection/status changes. Removing an imported recording also removes its old sidecars.

Back up the recording folder, including `archive.db`, while the server is stopped. New recordings do not have JSON sidecars from which a lost database can be reconstructed. Corrupt or unsupported newer catalogues fail startup without being replaced. Keep a backup before upgrading or rolling back; older servers cannot discover the new archive layout.

## Recording mirrors and automatic failover

In **Motion & recording → Recording storage**, keep the original **Recording folder on the server** and use **Add folder** for each mirror. Mount each drive at a stable path and create its recording folder before saving. The server account needs read and write access to each folder. Set each folder's **Maximum storage (GB)** separately. **Keep recordings for (days)** applies to all folders.

The server assigns each registered folder its own `.recording-location-id` file. Keep it with that folder. New media and the SQLite catalogue are stored in the adjacent ID-named archive directory. Existing media in the original folder stays at its current path and its catalogue entries are migrated when you register the folder. A missing ID marks the folder offline, and an unexpected ID marks it **Wrong folder**. If you intentionally replace or reformat a drive, mount it, create the recording folder, then choose **Register replacement** and save. Copying the ID file yourself copies the folder's identity.

Each mirror copies completed clips, thumbnails, and catalogue entries in the background, including timestamped motion markers and protection. Copies work in all recording modes. Smaller drives may hold a shorter history. The oldest unprotected clips are pruned on each drive separately, and clips that have expired from the active archive during an outage may be unavailable for backfill. The recording library browses the active drive. Retention and deletion affect the drive that owns the local copy.

If the active folder becomes unavailable, the server automatically promotes a usable mirror in configured order and starts new segments as the cameras retry. The active choice persists across server restarts. The original drive rejoins as a mirror when it returns. Use **Make active** to switch back once it has caught up. If every folder is unavailable, the server retries until it can record again. A failure can lose the in-progress segment and any completed clips still awaiting a copy. The storage section shows the active drive, mirror status, copy lag when known, and the last failover with the footage available on the promoted drive at that point.

The server bounds capture and health waits with `RecordingStorage:IoTimeoutSeconds` (15 seconds by default). For environment-based configuration use `RecordingStorage__IoTimeoutSeconds`. The operating system may take longer to return a blocked write. The server can promote another drive while that write remains outstanding and waits for the old call to return before reusing its destination.

## Segments, playback and recovery

**Maximum clip length** controls segments in all modes (default 300 seconds). Cuts follow complete keyframe fragments, so duration can exceed the target by a fragment. Continuous/available-video rotation keeps RTSP connected and neither duplicates nor drops the boundary fragment. Reconnects start a new segment and can create genuine coverage gaps.

During capture, fragmented MP4 is written for interruption recovery. After closing, the shared finalizer remuxes compressed video and AAC audio to regular MP4 with normalized timestamps and fast-start metadata. It does not re-encode video, but reads and rewrites the clip and temporarily needs both files. Thumbnails decode a frame. Interrupted captures recover complete fragments at startup; failed finalization retains original data for download. MP4 cannot make an unsupported camera video codec playable in every browser.

In **View recordings**, select a camera and date to see all recorded coverage, including clips that overlap midnight. Green segments are playable; pending/failed segments are shown separately. Empty areas indicate no recorded footage. The timeline shows up to 2,000 segments and reports truncation; the paged list remains available. Zone filters apply to the list; coverage shows all footage for the selected camera/date.

Select a segment or **Play** in the list. Activity marker buttons seek within that recording. **Continue through consecutive segments** advances through ready continuous/available-video segments in the selected day's timeline, stopping at gaps greater than two seconds, pending segments or event recordings. It does not prefetch or merge files, so transitions can briefly buffer. Cross-date playback requires selecting the next date. Each marker request returns up to 8,192 entries; the compatibility `additionalTriggers` summary remains bounded to 128 while SQLite retains the full marker history.

Read-authorized endpoints are `GET /api/recordings/timeline?channel=...&since=...&before=...` (maximum range two days) and `GET /api/recordings/{id}/markers`. The versioned API exposes `/api/v1/recordings/timeline?cameraId=...&since=...&before=...` using a stable camera ID, and `/api/v1/recordings/{id}/markers`. Timeline results contain `recordings` and `truncated`. Existing recording IDs, media URLs, range requests and protection/deletion APIs remain supported.

## Retention and capacity

Set **Keep recordings** to **0** to disable age-based expiry. The storage quota still applies. The active configuration's oldest completed, unprotected recordings are removed first; protected footage counts toward the quota. Active/finalizing clips reserve twice their current size so MP4 finalization has room. A free-disk reserve also applies. If protected/active footage, insufficient disk space or a storage error prevents capture, recording reports an error and retries; live viewing remains independent.

For video alone, storage in decimal GB/day is approximately **average Mbps × 10.8**. Four cameras averaging 4 Mbps use about 172.8 GB/day or 1.21 TB/week, plus audio and working space. The default 100 GB quota would retain less than a day at that rate. Measure actual bitrate and finalization throughput on the recording disk before selecting retention and quotas.
