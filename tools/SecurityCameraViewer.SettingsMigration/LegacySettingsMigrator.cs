using System.Text.Json;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Nvr;
using SecurityCameraViewer.Web.Settings;

namespace SecurityCameraViewer.SettingsMigration;

/// <summary>Optional paths used only by the one-time conversion tool.</summary>
public sealed record MigrationInputs
{
    public string? SharedPasswordPath { get; init; }
    public string? CredentialsPath { get; init; }
    public string? RecordingSettingsPath { get; init; }
    public string? StreamingSettingsPath { get; init; }
    public string? NvrSettingsPath { get; init; }
    public string? IntegrationDirectory { get; init; }
}

public sealed record MigrationResult(string? BackupDirectory, int SourceCount);

/// <summary>Imports legacy documents once. The application never uses these readers.</summary>
public sealed class LegacySettingsMigrator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public MigrationResult Migrate(string settingsPath, MigrationInputs inputs)
    {
        settingsPath = Path.GetFullPath(settingsPath);
        var directory = Path.GetDirectoryName(settingsPath)!;
        var sources = new Dictionary<string, byte[]>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        string? Read(string path, bool required = false)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                if (required)
                {
                    throw new FileNotFoundException("An explicitly selected migration input is missing.");
                }

                return null;
            }

            var bytes = File.ReadAllBytes(path);
            sources[path] = bytes;
            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        T? ReadJson<T>(string path, bool required = false) where T : class
        {
            var json = Read(path, required);
            if (json is null)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidDataException($"Empty legacy settings: {Path.GetFullPath(path)}.");
            }
            catch (JsonException)
            {
                throw new InvalidDataException($"The legacy JSON document is invalid: {Path.GetFullPath(path)}. Correct it before retrying.");
            }
        }

        var original = Read(settingsPath);
        if (original is not null)
        {
            using var document = JsonDocument.Parse(original);
            if (document.RootElement.TryGetProperty("ActiveConfigurationId", out _))
            {
                var current = JsonSerializer.Deserialize<AppSettings>(original, JsonOptions) ?? throw new InvalidDataException();
                AppSettingsValidation.Validate(current);
                return new(null, 0);
            }
        }

        var fallback = original is null ? new ViewerSettings() : JsonSerializer.Deserialize<ViewerSettings>(original, JsonOptions) ?? throw new InvalidDataException();
        var catalog = ReadJson<ConfigurationCatalog>(Path.ChangeExtension(settingsPath, ".configurations.json")) ?? new()
        {
            ActiveId = fallback.ConfigurationId,
            Configurations = [fallback]
        };
        if (catalog.Validate() is { } error)
        {
            throw new ArgumentException(error);
        }

        var passwordPath = inputs.SharedPasswordPath;
        if (passwordPath is null)
        {
            passwordPath = new[]
            {
                Path.Combine(Environment.CurrentDirectory, "artifacts", "web", "password.txt"),
                Path.Combine(directory, "password.txt"),
                Path.Combine(Environment.CurrentDirectory, "password.txt")
            }.FirstOrDefault(File.Exists);
        }

        var password = passwordPath is null ? "" : Read(passwordPath, required: true)!.TrimEnd('\r', '\n');
        var credentials = ReadJson<Dictionary<string, CameraCredentials>>(inputs.CredentialsPath ?? Path.Combine(directory, "camera-credentials.json"), inputs.CredentialsPath is not null) ?? [];
        var recordingPath = Path.GetFullPath(inputs.RecordingSettingsPath ?? Path.Combine(directory, "recording-settings.json"));
        var filters = ReadJson<Dictionary<string, SavedRecordingFilter[]>>(Path.ChangeExtension(settingsPath, ".recording-filters.json")) ?? [];
        var configurations = catalog.Configurations.Select(connection =>
        {
            var id = connection.ConfigurationId;
            var path = id == "default" ? recordingPath : Path.ChangeExtension(recordingPath, $".{id}.json");
            var recording = ReadJson<RecordingSettings>(path, inputs.RecordingSettingsPath is not null && id == "default") ?? new()
            {
                ConfigurationId = id,
                Directory = Path.Combine(Path.GetDirectoryName(recordingPath)!, id == "default" ? "recordings" : "recordings-" + id)
            };
            return new CameraConfigurationSettings
            {
                Connection = connection,
                Password = password,
                Credentials = connection.WithSources().Sources.Where(camera => credentials.ContainsKey(camera.Id))
                    .ToDictionary(camera => camera.Id, camera => credentials[camera.Id]),
                Recording = recording with { ConfigurationId = id },
                RecordingFilters = filters.GetValueOrDefault(id) ?? []
            };
        }).ToArray();

        var nvrPath = inputs.NvrSettingsPath ?? Path.Combine(directory, "nvr-settings.json");
        var nvrJson = Read(nvrPath, inputs.NvrSettingsPath is not null);
        var nvr = new NvrSettingsState();
        if (nvrJson is not null)
        {
            using var document = JsonDocument.Parse(nvrJson);
            var legacyPasswordPath = document.RootElement.EnumerateObject()
                .FirstOrDefault(property => property.Name.Equals("PasswordFile", StringComparison.OrdinalIgnoreCase)).Value;
            var nvrPasswordPath = legacyPasswordPath.ValueKind == JsonValueKind.String ? legacyPasswordPath.GetString() : null;
            nvr = new()
            {
                Settings = JsonSerializer.Deserialize<NvrSettings>(nvrJson, JsonOptions) ?? throw new InvalidDataException(),
                Password = string.IsNullOrEmpty(nvrPasswordPath) ? "" : Read(nvrPasswordPath, required: true)!.TrimEnd('\r', '\n')
            };
        }

        var migrated = new AppSettings
        {
            ActiveConfigurationId = catalog.ActiveId,
            Configurations = configurations,
            Streaming = ReadJson<StreamingSettingsState>(inputs.StreamingSettingsPath ?? Path.Combine(directory, "streaming.json"), inputs.StreamingSettingsPath is not null) ?? new(),
            Nvr = nvr,
            Integrations = ReadJson<IntegrationConfiguration>(Path.Combine(inputs.IntegrationDirectory ?? Path.Combine(directory, "integrations"), "integrations.json"), inputs.IntegrationDirectory is not null) ?? new()
        };
        AppSettingsValidation.Validate(migrated);

        // All inputs are validated before creating backups or replacing the main settings file.
        Directory.CreateDirectory(directory);
        var backupDirectory = Path.Combine(directory, "settings-backup-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(backupDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var backupPaths = new Dictionary<string, string>();
        foreach (var (path, bytes) in sources)
        {
            var backupPath = Path.Combine(backupDirectory, backupPaths.Count.ToString("000") + "-" + Path.GetFileName(path));
            PrivateFile.WriteBytes(backupPath, bytes);
            backupPaths[path] = backupPath;
        }

        PrivateFile.Write(Path.Combine(backupDirectory, "restore-map.json"), JsonSerializer.Serialize(backupPaths, JsonOptions));
        foreach (var (path, bytes) in sources)
        {
            if (!File.ReadAllBytes(path).SequenceEqual(bytes))
            {
                throw new IOException("Legacy inputs changed during migration. Stop all writers before conversion.");
            }
        }

        PrivateFile.Write(settingsPath, JsonSerializer.Serialize(migrated, JsonOptions));
        return new(backupDirectory, sources.Count);
    }
}
