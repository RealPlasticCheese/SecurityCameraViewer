# Server settings and migration

The maintained web server uses one private, versioned `settings.json`. A singleton `IAppSettingsStore`, implemented by `JsonAppSettingsStore`, owns loading, validation, snapshots and atomic updates. Feature accessors contain feature-specific operations and share that store; they do not write separate configuration files.

## Location and contents

- Windows: `%LOCALAPPDATA%\SecurityCameraViewer.Web\settings.json`.
- Linux: `${XDG_DATA_HOME:-$HOME/.local/share}/SecurityCameraViewer.Web/settings.json`, for the account running the server.
- Either host: use `--settings /absolute/path/settings.json` to select a different document.

The version 1 document contains `ActiveConfigurationId`, `Configurations`, `Streaming`, `Nvr`, `Integrations`, and `LiveRewind`. Each camera configuration contains its `Connection`, shared `Password`, per-camera `Credentials` (including ONVIF credentials), `Recording`, `RecordingFilters`, and `Enhancements`. `Enhancements` stores the browser picture sliders and enabled preference under each stable camera source ID. Existing documents default to Balanced values with enhancements off. Renaming or reordering cameras preserves their values; removing a camera removes its saved enhancement entry. The existing Enhance controls edit these shared preferences; undo history belongs only to the current open panel. Streaming client and NVR passwords are stored in their respective sections. API tokens remain hashes; webhook signing secrets retain their existing values. `LiveRewind` holds the optional rolling buffer's duration and memory budget; older documents default to buffering disabled.

Enter passwords in the browser and save. They survive server restarts. An omitted password retains the saved value; an explicit empty password clears it. Changing the NVR identity without supplying a replacement password clears the old recorder's credential. Ordinary state and configuration responses omit passwords; the existing explicit, administrator-controlled camera password reveal remains available. Diagnostic exports remain redacted unless explicitly requested otherwise.

The file contains plaintext passwords required by the camera protocols. Linux files, temporary files and converter backups use mode 0600. Windows files have protected ACLs granting access to the creating account, SYSTEM and Administrators. Run migration as the server account, and protect backups accordingly. The document must remain outside the published `wwwroot`.

Recordings, saved NVR exports, logs, webhook delivery queues, trigger receipts, temporary arming overrides and the generated streaming runtime document remain operational data. The integrations runtime directory defaults to `integrations` beside the settings file; an existing `--integrations-directory` override still selects that runtime directory. NVR export storage retains its `--nvr-export-directory` override.

Edits through the browser update only the intended section, under one shared lock, and publish the new in-memory snapshot after a durable atomic file replacement. To edit the file manually, stop the server first. Invalid, unsupported or legacy documents stop startup without overwriting anything or disabling authentication. The server does not reread manual edits while running. The standalone administrator-token recovery command also requires the server to be stopped.

## One-time migration

### TL;DR: Linux service

From a checkout containing this update, with the .NET 10 SDK installed, run the following as the Linux account that runs the server. Do not run the whole block with `sudo`; the service script requests it when needed. This assumes the default settings location and service options. For a custom setup, check `bash web-service.sh show`, adjust `viewer_settings`, and repeat your existing installer options such as `--user` and `--port`.

```bash
viewer_settings="${XDG_DATA_HOME:-$HOME/.local/share}/SecurityCameraViewer.Web/settings.json"
bash web-service.sh stop &&
dotnet run --project tools/SecurityCameraViewer.SettingsMigration -c Release -- "$viewer_settings" &&
bash build-web.sh &&
bash web-service.sh install --settings "$viewer_settings" &&
bash web-service.sh status
```

Open the viewer and check your cameras and recordings. Existing password files are imported automatically; re-enter and save any passwords that were only held in memory. Keep the backup directory printed by the converter. For custom legacy file locations or rollback, use the [detailed procedure below](#detailed-procedure).

If you run the server in a terminal, stop it with Ctrl+C instead of the service command, then use `bash run-web.sh --settings "$viewer_settings"` after conversion and building instead of installing the service.

### Detailed procedure

Use the separate converter once during the upgrade. The server does not contain legacy readers. No production settings are converted during builds or tests.

1. Stop the existing server (`bash web-service.sh stop` for the Linux service). Keep the previous application version available for rollback.
2. Run the converter as the account that runs the server, from this checkout:

   ```bash
   dotnet run --project tools/SecurityCameraViewer.SettingsMigration -c Release -- /absolute/path/settings.json
   ```

   On Windows, substitute the absolute Windows settings path. The first argument selects the existing main settings document, which is replaced only after all inputs validate.
3. Inspect the conversion summary. It reports file counts and the backup directory, never password values. A second conversion of an already unified document is a no-op.
4. Start the new server with the same `--settings` path. Verify named configurations, live playback, recording rules and library paths, saved recording filters, streaming/ONVIF clients, NVR access and integration authentication.
5. Enter any passwords that existed only in the old process's memory once in the browser and save. Those passwords were never present in legacy files and cannot be recovered after shutdown.

The converter reads the old main settings and `.configurations.json` catalog, per-configuration recording files, `.recording-filters.json`, `streaming.json`, `nvr-settings.json`, and `integrations/integrations.json`. It preserves camera/configuration/filter IDs, the active selection, recording paths, client device IDs, token hashes and webhook secrets. Shared password input uses the old published `artifacts/web/password.txt`, then a file beside the settings document, then the checkout's `password.txt`. Only trailing CR/LF characters are trimmed; spaces are preserved. NVR password paths in old NVR settings are imported, including BOM-marked UTF-16 files.

For custom legacy locations, provide an optional migration input manifest as the second positional argument:

```json
{
  "SharedPasswordPath": "/private/old-recorder-password.txt",
  "CredentialsPath": "/private/old-camera-credentials.json",
  "RecordingSettingsPath": "/private/old-recording-settings.json",
  "StreamingSettingsPath": "/private/old-streaming.json",
  "NvrSettingsPath": "/private/old-nvr-settings.json",
  "IntegrationDirectory": "/private/old-integrations"
}
```

Include only fields you need, and use absolute paths. An explicitly selected missing file or an invalid legacy section aborts conversion. Named recording files are derived from the recording base path as in the old server. Keep an existing integration runtime directory override when restarting so pending operational data remains available.

```bash
dotnet run --project tools/SecurityCameraViewer.SettingsMigration -c Release -- /absolute/path/settings.json /absolute/path/migration-inputs.json
```

Before replacing the main document, the converter creates a unique `settings-backup-...` directory beside it, containing byte-for-byte private backups of every imported input and a `restore-map.json` mapping original absolute paths to backup paths. Other legacy files are left in place. After verification, they can be removed from the active directory; retain the backup until you no longer need rollback. Missing optional files use defaults, while existing invalid files are never silently discarded. The converter does not move recordings or delete password inputs.

For rollback, stop the new server, restore each original file listed in `restore-map.json`, and launch the previous application version. Protect the backup and restore map as private server data.

The web server and service installer no longer support `--password-file`, `--credentials-file`, or `--nvr-password-file`. The separate `--recording-settings`, `--streaming-settings`, and `--nvr-settings` locations have also been removed. Previous `--nvr-host`, `--nvr-port`, and `--nvr-username` overrides are replaced by the saved `Nvr` section; record those connection values before stopping the old server if they were never saved. Remove these arguments from custom launch commands and reinstall an existing generated systemd unit using `bash web-service.sh install`. The converter's input manifest is only for this one-time operation.

## Validation

Run the settings and migration regression checks with:

```bash
dotnet run --project tests/SecurityCameraViewer.Web.Tests -c Release -- --settings-only
```

These use isolated fixture settings, never the installed server's files. They cover restart persistence, independent configuration credentials, concurrent updates, invalid documents, cancelled/failed writes, API redaction, removed options, migration preservation, private backups and repeat conversion.
