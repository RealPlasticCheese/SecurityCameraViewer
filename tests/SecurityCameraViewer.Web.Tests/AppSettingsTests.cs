using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.SettingsMigration;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Nvr;
using SecurityCameraViewer.Web.Settings;
using SecurityCameraViewer.Web.Streaming;

internal static class AppSettingsTests
{
    public static async Task RunAsync()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "unified-settings-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var configuration = Configuration(path);
        using var store = new JsonAppSettingsStore(configuration);
        var cameras = new WebSettingsStore(store);
        var camera = new CameraSource { Channel = 1, MainStream = "rtsp://127.0.0.1/live", Enabled = false };
        var connection = new ViewerSettings
        {
            AutoConnect = false,
            Sources = [camera],
            Credentials = new Dictionary<string, CameraCredentials> { [camera.Id] = new("camera-secret", "onvif-user", "onvif-secret") }
        };
        await cameras.SaveConfigurationAsync(connection, " shared-secret ");
        var nvr = new NvrSettingsStore(store);
        nvr.Save(new(new()
        {
            Host = "127.0.0.1"
        }, "nvr-secret"));
        var streaming = new StreamingSettingsStore(store);
        streaming.Save(new(streaming.Current, "streaming-secret"));
        var integrations = new IntegrationSettings(store, configuration);
        var token = integrations.CreateToken("Administrator", ["admin"], null);

        var filters = new RecordingFilterStore(store);
        var updates = Enumerable.Range(0, 20).Select(index => (Task)filters.SaveAsync("default", new(null, "Filter " + index, new()))).ToList();
        updates.Add(Task.Run(() => nvr.Save(new(nvr.Current, "nvr-updated"))));
        updates.Add(Task.Run(() => streaming.Save(new(streaming.Current, "streaming-updated"))));
        updates.Add(Task.Run(() => integrations.CreateToken("Concurrent reader", ["read"], null)));
        updates.Add(store.UpdateAsync(settings => settings with { LiveRewind = new(true, 3600, 512) }));
        await Task.WhenAll(updates);
        Check((await filters.ListAsync("default")).Length == 20, "Concurrent section updates preserve every filter");
        Check(store.Read().Nvr.Password == "nvr-updated" && store.Read().Streaming.Password == "streaming-updated" &&
            integrations.Current.Tokens!.Length == 2 && integrations.Authenticate(token.Value) is not null,
            "Saving filters preserves all other sections and authentication");
        Check(store.Read().LiveRewind == new LiveRewindSettings(true, 3600, 512), "Concurrent settings writes preserve user-selected rewind capacity");

        var second = connection with
        {
            ConfigurationId = Guid.NewGuid().ToString("N"),
            ConfigurationName = "Other cameras"
        };
        await cameras.SaveConfigurationAsync(second, "other-shared-secret");
        using (var restarted = new JsonAppSettingsStore(configuration))
        {
            Check(restarted.Read().LiveRewind == new LiveRewindSettings(true, 3600, 512), "Rewind settings survive server restart without an artificial duration ceiling");
            var restored = new WebSettingsStore(restarted);
            Check(restored.Load(false).Password == "other-shared-secret" && restored.LoadPassword("default") == " shared-secret ",
                "Shared passwords survive restart independently for named configurations");
            Check(restored.LoadCredentials("default")[camera.Id] == connection.Credentials[camera.Id],
                "Camera and ONVIF credentials survive restart without external files");
            Check(new NvrSettingsStore(restarted).GetConnection().Password == "nvr-updated" && new StreamingSettingsStore(restarted).Account.Password == "streaming-updated",
                "NVR and streaming passwords survive restart");
            Check(new IntegrationSettings(restarted, configuration).Authenticate(token.Value) is not null,
                "API token hashes survive restart");
            using var viewer = new ViewerService(restored, configuration);
            Check(!JsonSerializer.Serialize(viewer.Snapshot()).Contains("secret") && !JsonSerializer.Serialize(viewer.Configurations()).Contains("secret"),
                "Ordinary camera APIs never serialize the private document or credentials");
        }

        Check(!File.ReadAllText(path).Contains(token.Value), "The token plaintext is never saved");
        var snapshot = store.Read();
        snapshot.Configurations[0].Credentials.Clear();
        Check(store.Read().Configurations[0].Credentials.Count == 1, "Returned snapshots cannot mutate shared settings");
        var savedBytes = File.ReadAllBytes(path);
        await RejectAsync<ArgumentException>(() => store.UpdateAsync(settings => settings with { ActiveConfigurationId = "missing" }));
        Check(File.ReadAllBytes(path).SequenceEqual(savedBytes), "Validation failure leaves the original document unchanged");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await RejectAsync<OperationCanceledException>(() => store.UpdateAsync(settings => settings with { Version = 2 }, cancelled.Token));
        }
        using (var cancelledDuringUpdate = new CancellationTokenSource())
        {
            await RejectAsync<OperationCanceledException>(() => store.UpdateAsync(settings =>
            {
                cancelledDuringUpdate.Cancel();
                return settings with { Nvr = settings.Nvr with { Password = "cancelled-secret" } };
            }, cancelledDuringUpdate.Token));
            Check(File.ReadAllBytes(path).SequenceEqual(savedBytes), "Cancellation before commit leaves the persisted document unchanged");
        }

        File.Delete(path);
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            await RejectAsync<UnauthorizedAccessException>(() => store.UpdateAsync(settings => settings with { Nvr = settings.Nvr with { Password = "replacement" } }));
        }
        else
        {
            await RejectAsync<IOException>(() => store.UpdateAsync(settings => settings with { Nvr = settings.Nvr with { Password = "replacement" } }));
        }
        Check(store.Read().Nvr.Password == "nvr-updated" && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "A failed write preserves the in-memory snapshot and cleans its temporary file");
        Directory.Delete(path);
        PrivateFile.WriteBytes(path, savedBytes);

        foreach (var invalid in new[] { "{}", "{invalid", "{\"Version\":99,\"Configurations\":[],\"Integrations\":{}}", "{\"AutoConnect\":false}" })
        {
            PrivateFile.Write(path, invalid);
            await RejectAsync<InvalidDataException>(() => Task.FromResult(new JsonAppSettingsStore(configuration)));
            Check(File.ReadAllText(path) == invalid, "Invalid or legacy settings fail startup without overwriting the input");
        }

        PrivateFile.WriteBytes(path, savedBytes);
        foreach (var option in new[] { "password-file", "credentials-file", "nvr-password-file", "recording-settings", "streaming-settings", "nvr-settings", "nvr-host", "nvr-port", "nvr-username" })
        {
            await RejectAsync<ArgumentException>(() => Task.FromResult(new JsonAppSettingsStore(Configuration(path, option))));
        }

        if (!OperatingSystem.IsWindows())
        {
            Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Settings and their replacements are owner-only on Linux");
        }
        else
        {
            var security = System.IO.FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
            var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
            var allExplicit = true;
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
            {
                allExplicit &= !rule.IsInherited;
            }

            Check(security.AreAccessRulesProtected && rules.Count == 3 && allExplicit,
                "Windows settings have protected ACLs with only owner, SYSTEM and Administrator grants");
        }

        var legacyDirectory = Path.Combine(directory, "legacy-authentication");
        Directory.CreateDirectory(legacyDirectory);
        PrivateFile.Write(Path.Combine(legacyDirectory, "integrations.json"), JsonSerializer.Serialize(new IntegrationConfiguration(true)));
        var legacyConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["settings"] = Path.Combine(directory, "missing-settings.json"),
            ["integrations-directory"] = legacyDirectory
        }).Build();
        await RejectAsync<InvalidDataException>(() => Task.FromResult(new JsonAppSettingsStore(legacyConfiguration)));
        Check(!File.Exists(legacyConfiguration["settings"]), "Missing main settings never bypass legacy authentication in a custom directory");

        Backup(Path.Combine(directory, "backup"), store.Read(), camera.Id, token.Value);
        await MigrationAsync(Path.Combine(directory, "migration"));
    }

    private static void Backup(string directory, AppSettings settings, string cameraId, string adminToken)
    {
        var hook = new WebhookSubscription
        {
            Url = "https://example.test/hooks/fixture-token",
            Events = ["motion.started"],
            Secret = "webhook-secret"
        };
        var sourceConfiguration = Configuration(Path.Combine(directory, "source", "settings.json"));
        using var source = new JsonAppSettingsStore(sourceConfiguration);
        source.Update(_ => settings with
        {
            Configurations = settings.Configurations.Select(configuration => configuration with
            {
                Connection = configuration.Connection with
                {
                    Sources = [configuration.Connection.Sources.Single() with
                    {
                        UseDefaultPassword = true,
                        MainStream = "rtsp://127.0.0.1/live?token=fixture"
                    }]
                },
                Recording = new RecordingSettings
                {
                    ConfigurationId = configuration.Connection.ConfigurationId,
                    Directory = Path.Combine(directory, "recordings"),
                    Enabled = true,
                    RetentionDays = 15
                },
                Enhancements = new() { [cameraId] = new() { Enabled = true, Brightness = 10 } }
            }).ToArray(),
            Streaming = settings.Streaming with
            {
                Settings = settings.Streaming.Settings with
                {
                    Enabled = true,
                    AdvertisedHost = "192.0.2.20",
                    OnvifBaseUrl = "http://192.0.2.20:5080/"
                }
            },
            Integrations = settings.Integrations with { Webhooks = [hook] }
        });
        var snapshot = source.Read();
        var fullBackup = AppSettingsBackup.Create(snapshot, includePasswords: true);
        Check(fullBackup.SequenceEqual(File.ReadAllBytes(source.FilePath)),
            "The password-inclusive export exactly matches the complete saved settings file");

        using var restored = Restore("full", fullBackup);
        Check(new WebSettingsStore(restored).Load(false).Settings.ConfigurationId == snapshot.ActiveConfigurationId &&
            new IntegrationSettings(restored, Configuration(restored.FilePath)).Authenticate(adminToken) is not null &&
            restored.Read().Integrations.Webhooks!.Single().Secret == hook.Secret,
            "A fresh settings store loads the active collection, token hashes and webhook signing credentials from the backup alone");

        using var passwordless = Restore("passwordless", AppSettingsBackup.Create(snapshot, includePasswords: false));
        var safe = passwordless.Read();
        Check(safe.Configurations.All(configuration => configuration.Password == "" &&
                configuration.Credentials.Values.All(credentials => credentials.Password == "" && credentials.OnvifPassword is null)) &&
            safe.Nvr.Password == "" && safe.Streaming.Password == "" && !safe.Streaming.Settings.Enabled &&
            safe.Integrations.Webhooks!.Single() is { Secret: "", Enabled: false },
            "An unchecked backup clears passwords and signing secrets and starts without the services that require them");
        Check(safe.Configurations[0].Credentials[cameraId].OnvifUsername == "onvif-user" &&
            safe.Configurations[0].Connection.Sources[0] is { UseDefaultPassword: true, MainStream: "rtsp://127.0.0.1/live?token=fixture" } &&
            safe.Streaming.Settings.DeviceId == snapshot.Streaming.Settings.DeviceId &&
            safe.Integrations.Webhooks!.Single().Url == hook.Url &&
            new IntegrationSettings(passwordless, Configuration(passwordless.FilePath)).Authenticate(adminToken) is not null,
            "An unchecked backup retains credential usernames, exact URLs, stable IDs and existing API authentication");
        Check(AppSettingsBackup.Create(source.Read(), includePasswords: true).SequenceEqual(fullBackup),
            "Creating a passwordless backup leaves the live saved settings intact");

        using var unconfigured = new JsonAppSettingsStore(Configuration(Path.Combine(directory, "unconfigured.json")));
        using var defaults = Restore("defaults", AppSettingsBackup.Create(unconfigured.Read(), includePasswords: false));
        Check(defaults.Read().Configurations.Single().Connection.ConfigurationId == "default",
            "Backup works before the first settings save");

        JsonAppSettingsStore Restore(string name, byte[] backup)
        {
            var path = Path.Combine(directory, name, "settings.json");
            PrivateFile.WriteBytes(path, backup);
            return new JsonAppSettingsStore(Configuration(path));
        }
    }

    private static async Task MigrationAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "integrations"));
        var path = Path.Combine(directory, "settings.json");
        var passwordPath = Path.Combine(directory, "old-password.txt");
        var credentialsPath = Path.Combine(directory, "credentials.json");
        var nvrPasswordPath = Path.Combine(directory, "old-nvr-password.txt");
        var camera = new CameraSource { Channel = 1, MainStream = "rtsp://127.0.0.1/live", Enabled = false };
        var first = new ViewerSettings { AutoConnect = false, Sources = [camera] };
        var second = first with
        {
            ConfigurationId = Guid.NewGuid().ToString("N"),
            ConfigurationName = "Workshop"
        };
        PrivateFile.Write(path, JsonSerializer.Serialize(first));
        PrivateFile.Write(Path.ChangeExtension(path, ".configurations.json"), JsonSerializer.Serialize(new ConfigurationCatalog { ActiveId = second.ConfigurationId, Configurations = [first, second] }));
        PrivateFile.Write(passwordPath, " shared-password \r\n");
        PrivateFile.Write(credentialsPath, JsonSerializer.Serialize(new Dictionary<string, CameraCredentials> { [camera.Id] = new("camera-password", "onvif-user", "onvif-password") }));
        var library = Path.Combine(directory, "existing-library");
        PrivateFile.Write(Path.Combine(directory, "recording-settings." + second.ConfigurationId + ".json"), JsonSerializer.Serialize(new SecurityCameraViewer.Recording.RecordingSettings { ConfigurationId = second.ConfigurationId, Directory = library }));
        var filter = new SavedRecordingFilter(Guid.NewGuid().ToString("N"), "Existing filter", new());
        PrivateFile.Write(Path.ChangeExtension(path, ".recording-filters.json"), JsonSerializer.Serialize(new Dictionary<string, SavedRecordingFilter[]> { [second.ConfigurationId] = [filter] }));
        PrivateFile.Write(Path.Combine(directory, "streaming.json"), JsonSerializer.Serialize(new StreamingSettingsState { Password = "streaming-password" }));
        PrivateFile.WriteBytes(nvrPasswordPath, System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes("nvr-password\r\n")).ToArray());
        PrivateFile.Write(Path.Combine(directory, "nvr-settings.json"), JsonSerializer.Serialize(new
        {
            Host = "127.0.0.1",
            Port = 9000,
            Username = "admin",
            PasswordFile = nvrPasswordPath
        }));
        var apiToken = new ApiToken("existing-token", "Administrator", new string('a', 64), ["admin"], DateTimeOffset.UtcNow, null);
        PrivateFile.Write(Path.Combine(directory, "integrations", "integrations.json"), JsonSerializer.Serialize(new IntegrationConfiguration(true, [apiToken])));
        var original = File.ReadAllBytes(path);
        var inputs = new MigrationInputs { SharedPasswordPath = passwordPath, CredentialsPath = credentialsPath };
        var result = new LegacySettingsMigrator().Migrate(path, inputs);
        using var restored = new JsonAppSettingsStore(Configuration(path));
        var settings = restored.Read();
        Check(settings.ActiveConfigurationId == second.ConfigurationId && settings.Configurations[1].Connection.Sources[0].Id == camera.Id,
            "Migration preserves the active configuration and stable camera identities");
        Check(settings.Configurations.All(configuration => configuration.Password == " shared-password " && configuration.Credentials[camera.Id].OnvifPassword == "onvif-password"),
            "Migration imports shared, camera and ONVIF passwords, preserving spaces");
        Check(settings.Nvr.Password == "nvr-password" && settings.Streaming.Password == "streaming-password" && settings.Integrations.Tokens!.Single().Hash == apiToken.Hash && settings.Integrations.Tokens!.Single().Id == apiToken.Id,
            "Migration imports NVR passwords with BOMs, streaming credentials and token hashes");
        Check(settings.Configurations[1].Recording!.Directory == library && settings.Configurations[1].RecordingFilters.Single().Id == filter.Id,
            "Migration preserves recording library paths and saved filter identities");
        var restoreMap = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(result.BackupDirectory!, "restore-map.json")))!;
        Check(File.ReadAllBytes(restoreMap[path]).SequenceEqual(original) && File.ReadAllBytes(restoreMap[nvrPasswordPath]).SequenceEqual(File.ReadAllBytes(nvrPasswordPath)),
            "Migration creates private byte-for-byte backups and a restore map");
        Check(new LegacySettingsMigrator().Migrate(path, inputs).BackupDirectory is null, "Repeated conversion of unified settings changes no files");

        var invalidDirectory = Path.Combine(directory, "invalid");
        Directory.CreateDirectory(invalidDirectory);
        var invalidPath = Path.Combine(invalidDirectory, "settings.json");
        PrivateFile.Write(invalidPath, JsonSerializer.Serialize(first));
        PrivateFile.Write(Path.Combine(invalidDirectory, "streaming.json"), "{invalid");
        var before = File.ReadAllBytes(invalidPath);
        await RejectAsync<InvalidDataException>(() => Task.FromResult(new LegacySettingsMigrator().Migrate(invalidPath, inputs)));
        Check(File.ReadAllBytes(invalidPath).SequenceEqual(before) && !Directory.EnumerateDirectories(invalidDirectory, "settings-backup-*").Any(),
            "An invalid legacy section aborts conversion before backups or replacement");
    }

    private static IConfiguration Configuration(string path, string? removedOption = null)
    {
        var values = new Dictionary<string, string?> { ["settings"] = path };
        if (removedOption is not null)
        {
            values[removedOption] = "obsolete";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + description);
        }

        Console.WriteLine("PASS: " + description);
    }
}
