# Integrations, API tokens and webhooks

The web server exposes a versioned REST API, live server-sent events (SSE), and outgoing JSON webhooks. It runs on Linux and Windows with the same deployment as the viewer: .NET 10 and FFmpeg, or a self-contained web publish plus FFmpeg. There is no database server, message broker, cloud account or Home Assistant add-on to install. Recording metadata uses embedded SQLite.

## Enable access from the hamburger menu

1. Open **Integrations & API**, then **Enable integrations & sign-in**.
2. Save the administrator token displayed once. The current browser receives a session cookie automatically. Other browsers sign in with that token using **Sign in** in the menu.
3. Create a separate token named **Home Assistant** with **Read** and **Trigger recordings**. Add **Control** if automations need to arm/disarm cameras or deliberately override zone schedules. Avoid giving automations administrator permissions.
4. Configure outgoing webhooks in the same dialog. Select event types, cameras, zones and a cooldown; use **Test** and **Refresh deliveries** to check the receiver. Test deliveries have type `system.test` and intentionally bypass subscription filters and cooldowns.

Before enabling integrations, the original trusted-LAN browser mode is unchanged. After enabling, **all** API routes, including older browser controls, recordings, snapshots and WebSockets, require authentication. Versioned API routes always require authentication, even before setup. There is no anonymous bypass through old routes. Static HTML, scripts and public sign-in status remain available.

Send `Authorization: Bearer scv_...`; never put tokens in URLs. Tokens are generated with 256 random bits and stored only as SHA-256 hashes. Expiry is optional, and revocation applies to the next request, including browser sessions. Streaming connections recheck access regularly. Administrator sessions last twelve hours or until server restart. Token authentication persists across restart. Browser administration requires an administrator token; narrower scopes are intended for API clients.

| Scope | Access |
| --- | --- |
| `read` | Camera/zone discovery, snapshots, recording metadata/media, health, SSE and API specification |
| `trigger` | Request or extend a recording |
| `control` | Temporary arming/disarming and [push-to-talk camera audio](push-to-talk.md); also required alongside `trigger` when bypassing schedules |
| `admin` | All scopes, settings changes, protecting/deleting recordings, token and webhook management |

Use HTTPS when credentials cross an untrusted network. HTTP remains available for a trusted local network. Use your normal HTTPS reverse proxy if needed; preserve Host/Origin consistently. The application does not trust arbitrary forwarded headers. Do not expose trusted-LAN setup mode to the Internet.

If all administrator credentials are lost, stop the server, then run the following with the **same settings path and integration directory** used by the service. It prints a new administrator token once and exits; restart the service normally afterward. Do not run another writer against a running server's settings directory.

```sh
dotnet SecurityCameraViewer.Web.dll --settings /srv/camera-viewer/settings.json --create-admin-token true
```

Windows uses the same command with a Windows settings path. The default integration directory is `integrations` beside the web settings file. Override it with `--integrations-directory /srv/camera-viewer/integrations`. Preserve that directory across upgrades and container replacements. Token hashes, webhook URLs and signing secrets live in the unified `settings.json`. The runtime directory holds temporary arming overrides, delivery files and trigger receipts. Preserve both the settings document and operational data. See [settings and one-time migration](settings.md). Linux-created files use owner-only permissions; on Windows restrict the directory to the service account through its ACL. Protect backups too: webhook URLs and signing secrets are sensitive. Corrupt authentication configuration fails startup instead of silently allowing anonymous access.

## REST API

Base path: `/api/v1`. The menu links to `/api/v1/openapi.json`, which documents the routes and write schemas. JSON uses camelCase; timestamps are ISO 8601 UTC. Camera IDs such as `camera-1` identify recorder channels and remain stable when names change. Replacing the recorder on the same channel intentionally keeps that identity. Zone IDs are stable 32-character UUIDs; retain IDs when editing and omit them for newly created zones. Existing zone configurations receive deterministic IDs on migration.

| Method and path | Purpose |
| --- | --- |
| `GET /cameras` or `/cameras/{id}` | Names, live state, recording state, buffer coverage and arming state |
| `GET /cameras/{id}/zones` | Rectangle/polygon geometry, named zones, weekly schedules and active state |
| `GET /cameras/{id}/snapshot` | Latest live JPEG; returns 404 until live viewing supplies a frame |
| `GET`, `PUT /cameras/{id}/arming` | Read/set an expiring override |
| `POST /recordings/trigger` | Start/extend recording, preserving the RAM pre-event buffer |
| `GET /recording-requests/{id}` | Trigger receipt and any related recordings |
| `GET /recordings?cameraId=camera-1&before=...&limit=50` | Newest-first recordings; maximum 100 per page |
| `GET /recordings/timeline?cameraId=...&since=...&before=...` | Overlapping coverage (up to two days), maximum 2,000 segments with a truncation flag |
| `GET /recordings/{id}/markers` | Timestamped activity markers for seeking, maximum 8,192 per request |
| `GET /recordings/{id}` | Readiness, trigger sources, duration and media links |
| `GET /recordings/{id}/video[?download=true]` | Authenticated MP4 with byte-range support |
| `GET /recordings/{id}/thumbnail` | Authenticated JPEG thumbnail |
| `PUT /recordings/{id}/protection` | `{ "protected": true }` |
| `DELETE /recordings/{id}` | Delete a completed, unprotected recording |
| `GET`, `PUT /settings/recording` | Read/replace full recording and zone settings |
| `GET /health` | Camera errors, failed recordings, storage capacity, reduced pre-roll and webhook backlog |
| `GET /events` | Live SSE feed |

To edit zones through the API, retrieve `/settings/recording`, update the relevant camera's zones and PUT the full document back. Applying recording settings restarts capture workers. Shapes use normalized coordinates; exclusion zones win over inclusions. Weekly schedules use the recording settings timezone. The zone response distinguishes the weekly schedule from its effective active state under an arming override.

Arming payloads use `mode`: `schedule`, `armed`, or `disarmed`. Temporary modes require `expiresAt` up to seven days ahead. `armed` ignores weekly time ranges but retains shapes and exclusions; `disarmed` suppresses new automatic motion triggers. Event mode keeps buffering in RAM and allows its existing tail to finish. Continuous and available-video modes have no pre-event buffer; disarming closes capture at a fragment boundary, and arming resumes it. Expiry automatically resumes schedules, including across a server restart. Neither mode enables disabled recording or a disabled camera.

Example external trigger:

```http
POST /api/v1/recordings/trigger
Authorization: Bearer <trigger-token>
Idempotency-Key: front-door-open-20261002T090000Z
Content-Type: application/json

{"cameraId":"camera-1","source":"home-assistant","reason":"Front door opened","durationSeconds":20,"overrideSchedule":false}
```

An optional `zoneIds` array tags the cause for filtering; it does not redefine detection geometry. In Event mode, a trigger requires a connected, enabled recording camera with a filled buffer. Duration is 1–3600 seconds from admission at a keyframe; motion can extend the result and the configured maximum clip length still splits long events. Concurrent triggers extend the existing recording and appear in `additionalTriggers`. An explicit schedule override requires both `trigger` and `control` scopes. Off-schedule, disarmed, disabled or unavailable cameras return 409 unless the permitted override applies. Insufficient scope returns 403; missing/expired/revoked credentials return 401.

In continuous and available-video modes, external triggers require active, scheduled capture and add timestamped markers to existing footage. Duration and schedule overrides do not restart or extend capture; use temporary arming to change its schedule. Markers persist separately from the bounded `additionalTriggers` summary. See [continuous recording and archive storage](continuous-recording.md) for modes, migration and playback.

Retries must reuse the same `Idempotency-Key` and body. Keys are scoped to the issuing token and retained at least 24 hours; changing the body with the same key returns 409. Receipts persist before admission to avoid duplicate recordings after restart. A crash between receipt persistence and capture can leave an accepted receipt without footage; query its related recordings or consume recording lifecycle events. `202 Accepted` confirms admission, not successful finalization. Failed/rejected requests need a new key for a deliberate new attempt.

## Events and reliable delivery

### Zone webhook actions

Define a named **Webhook** action under **Motion & recording settings → Reusable actions**, then select it on the zone alongside **Record** if footage is wanted. Actions work in trusted-LAN mode without enabling API sign-in. Once sign-in is enabled, configuration is administrator-only. URLs and optional JSON bodies are stored in the camera configuration's recording section in `settings.json`; protect this file and backups because URLs can contain secrets.

Actions share the persistent delivery queue and retry policy below. **Integrations & API → Refresh deliveries** shows the saved action name and retry/dismiss controls after integrations are enabled. Action requests include `X-SCV-Action`, `X-SCV-Event`, `X-SCV-Delivery` and `X-SCV-Timestamp`. They have no signing secret or `X-SCV-Signature`; use existing signed event subscriptions when HMAC verification is required. A blank body sends the standard event with `actionIds`, camera details, and the triggering zone's stable ID and name. A custom body is static JSON; deduplicate retries using the delivery header.

Cooldowns apply to new activations per action/camera/zone; retries are independent. Recent persisted history restores cooldowns on restart. Only 100 completed/cancelled deliveries are retained, so cooldowns whose history has expired are not restored. Pausing holds pending deliveries; deletion cancels them. Editing a URL applies to pending deliveries, while their already-queued JSON bodies remain unchanged.

Existing event subscriptions remain independent and still receive matching motion events from zones with no selected actions. If both an action and a subscription target the same receiver, both can send a request. Disarming and weekly schedules suppress automatic actions; explicit recording API triggers retain their existing behavior.

### Event subscriptions

Subscriptions support `motion.started`, `motion.ended`, `recording.started`, `recording.ready`, `recording.failed`, `camera.offline`, `camera.online`, `storage.warning`, and `storage.recovered`. Motion events report confirmed **zone transitions**, not every frame. With no inclusion zones the synthetic ID `full-frame` represents the monitored picture. An exclusion is never reported as a motion zone. Alternative detectors can return per-zone scores through `MotionSample.Zones`; older aggregate implementations continue to produce full-frame events.

`recording.ready` is emitted only after the MP4 has been finalized and made available. A ready event may also mark an interrupted but recoverable recording. `motion.started` may precede a recording ID; correlate later events by camera, zone and time. Camera online/offline refers to the background recording stream; planned configuration restarts do not report an outage. Storage warnings cover maintenance errors, low free disk and nearing the configured quota. Recovery is a state transition, not a repeated heartbeat.

```json
{
  "schemaVersion": 1,
  "id": "unique-event-id",
  "type": "motion.started",
  "timestamp": "2026-10-02T09:00:00Z",
  "cameraId": "camera-1",
  "channel": 1,
  "cameraName": "Driveway",
  "zones": [{"id": "stable-zone-id", "name": "Gate"}],
  "recordingId": null,
  "data": {"changedPercent": 4.2}
}
```

Empty camera/zone filters match all. When both are specified, both must match. Storage events carry no camera/zone information, so filtered subscriptions do not receive them. Recording events inherit the zones known from their triggers. Cooldown is per subscription, event type, camera and zone set; it does not merge start/end events. Retries ignore cooldowns. Set cooldown to zero if every transition is needed.

Each subscribed event is placed in a local delivery file before the first HTTP attempt. Up to four destinations deliver concurrently, with one in-flight delivery per subscription. The dispatcher accepts any 2xx response, has a five-second timeout, does not follow redirects, and retries connection errors, 408, 425, 429 and 5xx responses. Backoff doubles with jitter, honors `Retry-After` up to one hour, and stops after ten attempts. Other failures become failed deliveries immediately. The menu lets you retry or dismiss them, pause subscriptions, rotate secrets and inspect results. Deleting a subscription cancels its queued deliveries; editing its URL or secret applies to pending deliveries too.

The queue holds up to 2,000 pending/failed deliveries and approximately 32 MiB of serialized payloads, plus the current event. It retains 100 completed/cancelled entries. A full queue or persistence error appears in health/menu notices. Capture publication uses a bounded 1,024-event memory channel so webhook I/O cannot stall capture; overflow is counted. Graceful shutdown drains admission after stopping capture. A hard crash can lose events still in this small memory channel. Persisted deliveries survive restart and provide **at-least-once** delivery: consumers should deduplicate by event ID or stable delivery ID. This is not an exactly-once transaction spanning recording and webhooks.

Headers are `X-SCV-Event`, `X-SCV-Delivery`, `X-SCV-Timestamp` (Unix seconds), and `X-SCV-Signature`. The signature is `sha256=` followed by lowercase hex HMAC-SHA256 using the signing secret's UTF-8 bytes over `timestamp + "." + raw JSON body`. Verify in constant time, reject stale timestamps (for example beyond five minutes), and deduplicate delivery IDs. Every retry receives a fresh timestamp/signature and retains its delivery/event IDs. Home Assistant webhook triggers can consume the JSON without verifying these signature headers; use a long random webhook URL secret and local-only access.

SSE sends the same event envelope. It starts with a `connected` event asking clients to refresh state, then named events and 15-second keepalives. It has no historical replay: `Last-Event-ID` does not recover missed events. Slow readers are disconnected rather than consuming unlimited memory. Use `/health`, `/cameras`, and `/recordings` to resynchronize after reconnect. Durable automation delivery should use webhooks.

**Disk activity:** video pre-roll remains entirely in RAM. Queue files, retries/results, trigger receipts and explicit configuration changes create small metadata writes. Idle polling does not rewrite the queue, token store or arming state. There is no continuous video spool.

## Home Assistant examples

Use a webhook automation for incoming viewer events. Replace the ID with a long random value, then set the viewer's destination to `http://homeassistant.local:8123/api/webhook/<that-value>`. Example using the [Home Assistant webhook trigger](https://www.home-assistant.io/docs/automation/trigger/#webhook-trigger):

```yaml
automation:
  - alias: Camera motion notification
    triggers:
      - trigger: webhook
        webhook_id: replace-with-long-random-secret
        allowed_methods: [POST]
        local_only: true
    conditions:
      - condition: template
        value_template: "{{ trigger.json.type == 'motion.started' }}"
    actions:
      - action: persistent_notification.create
        data:
          title: "{{ trigger.json.cameraName }}"
          message: "Motion in {{ trigger.json.zones | map(attribute='name') | join(', ') }}"
```

For triggers from Home Assistant, use its [RESTful Command integration](https://www.home-assistant.io/integrations/rest_command/). Store the complete `Bearer scv_...` header in `secrets.yaml` under `camera_viewer_authorization`. Restart/reload REST commands after editing configuration. Use the Linux NUC's address reachable from Home Assistant; `localhost` inside a container points to that container.

```yaml
rest_command:
  camera_record_event:
    url: "http://camera-nuc:5080/api/v1/recordings/trigger"
    method: POST
    headers:
      authorization: !secret camera_viewer_authorization
      Idempotency-Key: "{{ event_key }}"
    content_type: application/json
    payload: >-
      {{ dict(cameraId=camera_id, source='home-assistant', reason=reason,
              durationSeconds=20, overrideSchedule=false) | tojson }}

automation:
  - alias: Record when front door opens
    triggers:
      - trigger: state
        entity_id: binary_sensor.front_door
        to: "on"
    actions:
      - action: rest_command.camera_record_event
        data:
          camera_id: camera-1
          reason: Front door opened
          event_key: "door-{{ trigger.to_state.context.id }}"
```

These examples use standard HTTP and JSON; Node-RED, scripts, other automation platforms and custom motion detectors can use the same API. Notification media links require authenticated fetching; never append an API token to a URL to make it public.

## Implementation and verification

`ICameraEventPublisher` decouples capture/storage from transport. `IntegrationEventHub` provides bounded fan-out, `WebhookDispatcher` owns persistence/retry/delivery, `IntegrationSettings` owns configuration/token hashes, `IntegrationAuth` owns access checks/sessions, and `RecordingTriggers` owns request deduplication. The recorder continues to depend on `IMotionDetectorFactory`, `IMotionFrameSource`, `IEncodedVideoSource` and `IRecordingEventStore`; detector replacements need no webhook or web-host dependency.

Run `dotnet run --project tests/SecurityCameraViewer.Integration.Tests -c Release -- artifacts/web/SecurityCameraViewer.Web.dll` after publishing. It uses temporary test directories, a local HTTP receiver and the authenticated RTSP fixture to check signatures/retries/recovery, scopes and legacy-route protection, trigger deduplication, external still-scene recording, token persistence/revocation and arming expiry. Existing recording and web regression suites cover buffering, schedules, stream failures, protection, retention and multiple viewers.
