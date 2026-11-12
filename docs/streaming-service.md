# RTSP and ONVIF camera service

The existing ASP.NET Core server can share the cameras in its **active configuration** with Android viewers, NVRs and other clients. Each enabled camera has its own main stream and, when configured, substream. The service does not combine the camera grid into a single video.

Configuration is in the existing hamburger menu: **RTSP & ONVIF service**. The feature starts disabled. Windows and Linux server hosts are supported.

## Quick setup

1. Configure and test your cameras through **Cameras & configurations**. Select the collection you want to share. Disabled cameras are excluded.
2. Install MediaMTX using one of the commands below, then start your web server normally.
3. Open **RTSP & ONVIF service** in the hamburger menu.
4. Enter the server's LAN IP/hostname, such as `192.168.1.10`, and the **ONVIF server base address**, such as `http://192.168.1.10:5080/`. This is the existing web server address. Use an address reachable from your phone, including the web port and trailing `/`.
5. Set a client username and password (at least eight characters). MediaMTX's Digest login supports ASCII letters, digits and `! $ ( ) * + . ; < = > [ ] ^ _ - { } @ # &`; spaces and other symbols are rejected before saving. These are new service credentials; clients do not need your upstream camera credentials or a browser API token. Upstream camera passwords retain their existing character support.
6. Leave RTSP on **8554**, ONVIF enabled, and discovery set to **Individual cameras**. Enable the consolidated service and save.
7. Wait for **running** in the status line. The address list provides each camera's ONVIF device address and main/substream RTSP addresses. Click an address field to select it for copying.

Opening the page at `localhost` pre-fills a localhost address, which is not reachable from your phone. Replace it with the server's LAN IP. Discovery and manual client access both use the explicitly configured addresses; they do not trust an incoming HTTP Host header to construct stream URLs.

### Windows

From the repository directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build-web.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File install-mediamtx.ps1
.\launch-web.cmd
```

The installer downloads MediaMTX **1.21.1**, verifies its SHA-256 release checksum, and places `mediamtx.exe` beside the published web server in `artifacts/web`. The server starts and supervises it when the service is enabled. You do not need to run MediaMTX separately or maintain its own camera configuration.

For Windows ARM64, pass `-Architecture arm64`. To select a different release or publish folder, use `-Version 1.21.1 -Destination C:\path\to\published-server`. Versions other than 1.21.1 require compatibility testing. FFmpeg and FFprobe must be installed (FFprobe is normally included with FFmpeg).

### Linux

From the repository directory, as the normal service user:

```bash
bash build-web.sh
bash install-mediamtx.sh
bash run-web.sh
```

The installer uses `curl`, `sha256sum`, `tar` and `install`, detects x86-64/ARM64/ARMv7, verifies the release checksum and installs an executable in `artifacts/web`. An optional first argument selects the version and a second selects the destination:

```bash
bash install-mediamtx.sh 1.21.1 /path/to/published-server
```

For the existing systemd service, install the binary beside its published DLL, then run `bash web-service.sh restart`. The same service manages both the ASP.NET application and MediaMTX; a separate systemd unit is unnecessary. The service account needs permission to execute MediaMTX and write the server data directory. Upstream camera passwords are saved in the unified settings document.

Alternatively, install MediaMTX yourself and enter its absolute executable path in **Advanced relay settings**. `mediamtx` also searches beside the running DLL and then on PATH. No dependency is downloaded automatically during server startup.

## Client setup

### ONVIF discovery

Connect your phone/client to the same LAN as the server and scan for ONVIF devices. Discovery uses IPv4 multicast at `239.255.255.250:3702`. Select the camera and enter the username/password from **Client login**. Main and substream profiles appear where supported by the client.

Choose **Individual cameras** for apps that expect a device to represent one camera. Choose **One recorder with all cameras** for clients that handle multiple video sources/profile groups. **Both** advertises both forms and can produce duplicate camera entries. The recorder endpoint always exists at:

```text
http://192.168.1.10:5080/onvif/device_service
```

The individual camera endpoint contains its stable ID:

```text
http://192.168.1.10:5080/onvif/cameras/<camera-id>/device_service
```

tinyCam Monitor, Onvier and other Android ONVIF apps are target clients, but their actual Android builds must be checked on a device. This repository's automated tests exercise ONVIF wire protocols, schemas and its own ONVIF client; they do not claim those Android apps have been tested. Menu labels and support for custom service paths vary between app versions. Use discovered entries or the full camera service URL when supported. If an app only accepts host/port and always uses `/onvif/device_service`, use the recorder endpoint and select a profile, or add the camera as RTSP.

### Direct RTSP

Copy a camera's RTSP address from the menu and enter the client login in the application's credential fields:

```text
rtsp://192.168.1.10:8554/cameras/<camera-id>/main
rtsp://192.168.1.10:8554/cameras/<camera-id>/sub
```

These addresses contain no passwords. Select **RTSP over TCP** in the client for the simplest firewall setup. UDP transport is also enabled by default and can be disabled in advanced settings. Audio is relayed with video when present, in its original codec. Clients must support the source video/audio codecs; the relay does not transcode them. For H.265 cameras and a client that cannot decode H.265, configure an H.264 substream upstream and select that profile.

## Firewall and addressing

Allow the following ports from your trusted LAN:

| Purpose | Default | Notes |
|---|---|---|
| Web and ONVIF | TCP 5080 | Existing ASP.NET server listener; use your configured web/HTTPS port |
| RTSP | TCP 8554 | Required for RTSP negotiation and TCP video |
| ONVIF discovery | UDP 3702 | Multicast discovery; not needed when using manual service URLs |
| RTP / RTCP | UDP 8000 / 8001 | UDP requires an even RTP port and the next port for RTCP |
| Relay control API | TCP 9997 | Bound to loopback, authenticated; do not expose externally |

For example, with UFW and a LAN of `192.168.1.0/24`:

```bash
sudo ufw allow from 192.168.1.0/24 to any port 5080 proto tcp
sudo ufw allow from 192.168.1.0/24 to any port 8554 proto tcp
sudo ufw allow from 192.168.1.0/24 to any port 3702 proto udp
# Only for RTP over UDP:
sudo ufw allow from 192.168.1.0/24 to any port 8000:8001 proto udp
```

The installer does not modify firewall rules. Guest Wi-Fi, client isolation, VLAN boundaries and VPNs often block multicast discovery. Use manual URLs and TCP RTSP across routed networks. Manual IPv6 addresses are supported, but discovery is currently IPv4 only. An HTTPS reverse proxy must forward `/onvif/` as well as the normal web application; configure the HTTPS origin as the ONVIF base address. RTSP remains a separate listener. ONVIF SOAP and RTSP on their default ports are unencrypted; Digest authentication does not encrypt the video. Use a trusted LAN/VPN for remote access.

## Runtime behavior and persistence

- MediaMTX reads compressed streams directly from upstream cameras. All consumers of one relay path share its upstream connection; main and substream are separate paths. Web viewing, recording video, motion analysis, live audio and external readers use the local relay while it is ready. Upstream setup probes, battery-camera availability checks and push-to-talk still use the camera directly.
- Sources start when requested and close ten seconds after the last reader leaves. Metadata inspection may briefly open non-battery feeds. FFprobe discovers actual codec, dimensions, frame rate and audio metadata. If probing is unavailable, saved ONVIF metadata is used, followed by documented fallback values: H.264, 1920×1080 main / 640×360 sub, 15 fps, 4096 / 512 kbps. Check the descriptor source in status and use **Profile metadata overrides** when needed. Overrides describe the original stream; they do not re-encode it.
- Motion-activated cameras are excluded from automatic media probing. Use saved camera metadata or an override. An external RTSP request or snapshot may open their feed; its availability still depends on the camera's wake behavior.
- Closing browsers or choosing **Disconnect** does not stop RTSP or ONVIF. Disable the service in its menu or stop the web server to stop the consolidated service.
- Changing camera configuration or client credentials restarts relay sessions. Routing transitions restart active viewing and recording workers; open clips may be marked interrupted. When MediaMTX is unavailable, internal workers recover using direct camera inputs. The web UI and recordings remain independently usable.
- Relay failures retry with a bounded delay. The menu shows startup/health failures. ONVIF discovery failures are reported separately and do not prevent manual ONVIF URLs or RTSP playback.
- Settings and the client password are saved atomically in the `Streaming` section of the private unified `settings.json`. Use `--settings /path/settings.json` to select the document. Client plaintext is required for HTTP/RTSP Digest and WS-Security digest interoperability; keep that directory accessible only to the service user and administrators. On Linux new private files use mode 0600. Windows files use protected ACLs for the server account, SYSTEM and Administrators.
- A private `streaming-runtime.json` in the same directory contains upstream camera credentials while MediaMTX runs. It is passed as a filename, never as credential-bearing process arguments, and is removed after the relay stops. Child stdout/stderr are drained without forwarding potentially sensitive camera URLs to browser responses or application logs. A forced host termination can leave the runtime file behind; preserve its restrictive permissions and remove it when the service is stopped.
- Camera and client passwords entered through the browser persist in their respective sections of the unified settings document. See [one-time migration](settings.md) for existing installations.

## Implemented ONVIF surface

Both SOAP 1.1 and SOAP 1.2 are supported, with WS-Addressing response correlation. Authentication accepts HTTP Basic, HTTP MD5 Digest (`qop=auth` or legacy without qop), WS-Security UsernameToken PasswordDigest and PasswordText. Timestamp limits and nonce replay protection apply to digest tokens; device UTC time is available before authentication so clients can calculate clock offset.

| Service | Implemented behavior |
|---|---|
| Device | Device information, UTC clock, capabilities, services with embedded capabilities, fixed scopes, discovery mode, hostname, network interfaces/protocols and read-only user enumeration |
| Discovery | WS-Discovery 2005 and 2009 Probe/Resolve, device-type and scope matching, stable recorder/camera UUIDs, Hello/Bye announcements |
| Media | Profiles, profile lookup, video sources, source/encoder configurations and compatible configurations, configuration options, audio source/encoder queries, stream and snapshot URIs |
| Media2 | Profiles including H.265, configuration-type selection, source/encoder configurations/options, audio configurations, stream and snapshot URIs |
| DeviceIO | Video/audio source token enumeration and read-only service capabilities |
| Events | Standard motion and video-signal topics, event properties, expiring PullPoint subscriptions, bounded pulls, topic filters, renewal, unsubscribe and synchronization of observed state |
| Snapshots | Authenticated JPEG snapshots from the shared relay, independent of browser viewing |

Media1 excludes H.265 encoder profiles because its schema cannot describe that codec. Media2 exposes them; direct RTSP URLs also work. Software motion events aggregate active zones, so one zone ending does not clear motion while another remains active. Events come from the existing recording workers: enable and configure **Motion & recording** to receive motion/online/offline events. The service does not subscribe to or proxy proprietary upstream camera analytics. Event queues are bounded to 128 notifications per subscriber; slow subscribers keep recent state, and expired subscriptions must be recreated. Maximum 32 subscriptions, with pulls up to 30 seconds and renewals up to one hour.

This is a documented ONVIF implementation subset, not an ONVIF-conformant/certified Profile S/T/G product. It does not advertise conformance scopes. PTZ, imaging adjustment, speaker backchannel through the relay, encoder/profile mutations, remote reboot/firmware management, multicast RTP and ONVIF recording search/replay are not advertised. Unsupported actions return ONVIF faults rather than successful placeholder responses. Web recordings and browser push-to-talk remain available through their existing interfaces.

## Troubleshooting

| Symptom | Action |
|---|---|
| Service remains starting/retrying | Install MediaMTX 1.21.1; verify its path/executable permission and distinct ports. Stop any independently running MediaMTX that uses the same ports. |
| Discovery unavailable | Allow UDP 3702/multicast; check LAN interfaces and another ONVIF discovery listener. Use manual device URLs. |
| Camera appears but video fails | Use the client service login, TCP RTSP and a supported source codec. Verify upstream camera credentials and main/substream addresses in camera setup. |
| Only some profiles appear | Media1 hides H.265 profiles. Use a Media2-capable client or a direct RTSP URL. A substream is available only when configured upstream. |
| Client selects the wrong camera | Use individual discovery/device URLs. For a host/port-only app, select the correct recorder profile or use RTSP. |
| Snapshot returns 503 | Relay is unavailable, upstream video is asleep/offline, FFmpeg is missing, or no frame arrived before the timeout. |
| Descriptor resolution is incorrect | Install FFprobe, refresh media metadata, wait for inspection, then reopen settings. Set an override for a sleeping camera or unusual metadata. |
| WS-Security login fails | Correct the client login and host UTC time. The unauthenticated clock operation helps clients compensate for skew. |
| Administration returns 401/403 | Sign into the browser with an administrator API token when Integrations authentication is enabled. Client RTSP/ONVIF credentials cannot administer the server. |

## Validation

The tests follow the repository's executable C# test convention and need no additional test-framework package:

```bash
dotnet run --project tests/SecurityCameraViewer.Streaming.Tests
```

For actual RTSP/HTTP/FFmpeg integration, install MediaMTX, FFmpeg/FFprobe and Python, build the server, then run:

```bash
dotnet build src/SecurityCameraViewer.Web
dotnet run --project tests/SecurityCameraViewer.Streaming.Tests -- --integration --mediamtx /absolute/path/to/mediamtx
```

On Windows, omit `--mediamtx` to use `artifacts/web/mediamtx.exe`. On Linux supply the installed `artifacts/web/mediamtx` path. The suite starts localhost-only fixtures on temporary ports and does not contact saved real cameras. Optional `--server /path/to/SecurityCameraViewer.Web.dll` selects a published server.

Additional official-XSD validation uses an ignored cache downloaded from ONVIF, W3C and OASIS:

```bash
python tests/fetch_onvif_schemas.py
dotnet run --project tests/SecurityCameraViewer.Streaming.Tests -- --schemas
node --test tests/web-streaming.test.cjs
```

The schema suite uses cached official definitions without network access at test time. ONVIF development schemas contain extensibility wildcards, so .NET's unique-particle-attribution compilation check is disabled; instance validation remains enabled. These checks supplement protocol/integration tests and do not constitute ONVIF conformance testing. Physical Android devices and Linux runtime/multicast behavior need deployment testing on their respective hosts.

Implementation references: [MediaMTX camera sources](https://mediamtx.org/docs/publish/rtsp-cameras-and-servers), [MediaMTX configuration](https://mediamtx.org/docs/references/configuration-file), [ONVIF specifications](https://www.onvif.org/profiles-specifications-new/), [ONVIF reference WSDL/schema repository](https://github.com/onvif/specs).
