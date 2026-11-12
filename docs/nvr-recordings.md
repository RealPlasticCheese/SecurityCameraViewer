# RaySharp NVR recordings

Open **NVR recordings (RaySharp)** in the camera viewer's menu. This separate page accesses recordings on the recorder's hard drive using its proprietary TCP protocol. It does not require RTSP, ONVIF, a browser plugin, .NET Framework, or the vendor's native DLLs.

The maintained ASP.NET Core server supports Windows and Linux. This implementation uses .NET 10 networking and the server's existing FFmpeg dependency. The supplied RsSDK V1.1.17 wrapper documents recording search, playback, and download, but its Windows, Android, and Apple binaries cannot be used directly on an ordinary Linux server. The core vendor networking and playback implementations are not included as source. We implement the documented recording messages in managed C#, with an attributed MIT implementation of the login wire permutation.

## Connection

Expand **Recorder connection**, enter the host, proprietary TCP port (usually **9000**), username, and password, then save. **Connect / refresh information** displays the recorder name, firmware, channel count, and the account's playback/export permissions. Connection settings are independent of the live camera configurations.

The NVR password is saved in the private `Nvr` section of the unified `settings.json` and restored after restart. Leave the password field blank to retain it. Passwords are omitted from ordinary API responses and diagnostics. Changing the recorder identity without entering a replacement password clears the previous credential. Use `--settings /absolute/path/settings.json` to choose the complete server settings document. See [file permissions and one-time migration](settings.md) for existing installations.

## Browse, play, and export

1. Connect, choose a channel and a date, and select **Search recordings**. Channel numbers start at 1 in the UI; the adapter handles the vendor's internal numbering.
2. Select a recording segment. The clip form initially selects up to one minute; adjust the start and end as needed, up to **one hour** per request.
3. Select **Retrieve and save MP4**. Transfers run one at a time, with up to four waiting requests. You may cancel a queued or active transfer. Navigating away does not cancel it.
4. Once saved, select **Play** for browser playback and seeking, or **Download MP4** to save it on your computer or phone. Saved clips remain available after a server restart.

Transfers show a progress bar, an estimated percentage of the selected recording duration, and the amount of video received. The estimate uses video timestamps, including gaps; keyframe alignment and missing footage can make it jump at the end. Retrieval reaches 100% only when the recorder finishes sending video. **Preparing MP4** then shows an indeterminate bar until the playable file is saved. Failed or cancelled transfers retain their last retrieval progress.

Dates and times are the **recorder's wall clock**, independent of the browser/server timezone. Recorder keyframe alignment may shift the actual start slightly. Video timestamps and gaps are preserved through an intermediate Matroska container rather than assuming a fixed frame rate. Each transfer is bounded to 20 minutes and 1 GiB of captured video. Completed MP4s and their indexes live in `nvr-exports` beside the web settings file, or in the directory selected by `--nvr-export-directory`. They are not mixed with the server's motion recordings.

**Delete message** removes a failed or cancelled transfer from the transfers list. **Delete server copy** removes the downloaded clip and its index. It does not delete footage from the recorder. Failed/cancelled transfers remove their partial files; shutting down interrupts transfers. Only completed exports are served to browsers, with HTTP range support.

## Supported features and limits

- Recorder identification, channel count, account playback/export permissions, and read-only disk capacity/free space/automatic-overwrite information.
- Recording segment search, including start/end, recording type, and reported file size, for a range of at most one day.
- Recorded H.264 video retrieval, server-side MP4 preparation, browser playback/seeking, downloads, cancellation, and saved-clip management.
- **Video only:** recorded audio, H.265, encrypted video frames, reverse playback, live proprietary preview, PTZ, alarm subscriptions, and device configuration changes are not implemented. Other SDK features are not assumed to work simply because their entry points exist.

With Integrations authentication enabled, a `read` token can view recorder information, search recordings, and play/download saved clips. An `admin` token is required for connection settings, creating/cancelling transfers, deleting transfer messages, and deleting server copies. Browser sessions use the existing sign-in page in the camera viewer's menu. The native recorder protocol uses obfuscation rather than TLS, so credentials and footage should travel over your trusted network or VPN.

## API and architecture

The `/api/nvr` endpoints provide `GET/PUT settings`, `GET device`, `POST search`, `GET/POST exports`, `GET/DELETE exports/{id}`, `DELETE exports/{id}/message` for failed/cancelled transfer messages, `POST exports/{id}/cancel`, and `GET exports/{id}/video` (append `?download=true` for an attachment). Search/export bodies contain `channel`, `begin`, and `end`; date values are ISO wall-clock strings such as `2026-10-07T12:00:00`, without `Z` or an offset.

`GET /api/nvr/storage` returns read-only disk capacities and free space in bytes, plus the automatic-overwrite setting. It uses the legacy SDK's KiB counters; disk-health diagnostics are not inferred from these values.

`SecurityCameraViewer.Nvr` defines the reusable `INvrClient`, recorder models, elementary video frames, and timestamp-aware H.264 packaging. Vendor message framing, authentication, dates, channel numbering, packet assembly, and frame headers stay inside the `Raysharp` adapter. The web project separately owns credential/settings storage, export persistence, transfer scheduling, FFmpeg conversion through `INvrMediaConverter`, HTTP delivery, and presentation. Future recorder providers can implement `INvrClient` without copying the RaySharp protocol details into the UI.

## Verification

Portable protocol and service regressions:

```text
dotnet run --project tests/SecurityCameraViewer.Nvr.Tests
```

The tests cover independent cipher vectors, fragmented TCP replies and header extensions, legacy recording parsing/frame padding, invalid inputs, credential persistence, saved exports after restart, identifier traversal, authorization, and cancellation cleanup. An optional path to a generated H.264 picture also exercises the timestamp-aware container writer.

On the supplied NVR041-720P running **V5.2.0-20160411**, native-port authentication and recording searches succeeded; a 30-second channel-2 export produced **750 H.264 video packets at 1280×720**. Browser playback and HTTP partial-content seeking were verified. The Linux x64 publish succeeds without vendor binaries; execution against the NVR was tested on Windows, since the development PC has no Linux runtime or WSL installed. Compatibility with other firmware, longer exports, H.265, and audio remains unverified.

Protocol references: [RsSDK wrapper and headers](https://github.com/jonathanrobichaud4/raysharp-dvr-api-docs-and-sdk), [pyswann 0.0.29](https://pypi.org/project/pyswann/0.0.29/). See [third-party notices](../THIRD-PARTY-NOTICES.md) for the adapted MIT source.
