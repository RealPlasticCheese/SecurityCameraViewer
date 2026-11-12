using System.Text.Json;

namespace SecurityCameraViewer.Web.Settings;

public sealed class JsonAppSettingsStore : IAppSettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current;

    public string FilePath { get; }

    public JsonAppSettingsStore(IConfiguration configuration)
    {
        foreach (var option in new[] { "password-file", "credentials-file", "nvr-password-file", "recording-settings", "streaming-settings", "nvr-settings", "nvr-host", "nvr-port", "nvr-username" })
        {
            if (configuration[option] is not null)
            {
                throw new ArgumentException($"The --{option} option has been removed. Use the unified settings file; see docs/settings.md for one-time migration.");
            }
        }

        FilePath = Path.GetFullPath(configuration["settings"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecurityCameraViewer.Web", "settings.json"));
        if (!File.Exists(FilePath) && new[]
            {
                Path.ChangeExtension(FilePath, ".configurations.json"),
                Path.ChangeExtension(FilePath, ".recording-filters.json"),
                Path.Combine(Path.GetFullPath(configuration["integrations-directory"] ?? Path.Combine(Path.GetDirectoryName(FilePath)!, "integrations")), "integrations.json"),
                Path.Combine(Path.GetDirectoryName(FilePath)!, "streaming.json"),
                Path.Combine(Path.GetDirectoryName(FilePath)!, "nvr-settings.json"),
                Path.Combine(Path.GetDirectoryName(FilePath)!, "recording-settings.json")
            }.Any(File.Exists))
        {
            throw new InvalidDataException("Legacy settings were found. Run the one-time converter described in docs/settings.md before starting the server.");
        }

        _current = File.Exists(FilePath) ? Load(FilePath) : new();
    }

    public AppSettings Read()
    {
        _gate.Wait();
        try
        {
            return Copy(_current);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Update(Func<AppSettings, AppSettings> update)
    {
        _gate.Wait();
        try
        {
            Save(update);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Save(update, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Save(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var next = update(Copy(_current));
        AppSettingsValidation.Validate(next);
        var serialized = JsonSerializer.Serialize(next, JsonOptions);
        var snapshot = JsonSerializer.Deserialize<AppSettings>(serialized)!;
        cancellationToken.ThrowIfCancellationRequested();
        PrivateFile.Write(FilePath, serialized);
        _current = snapshot;
    }

    private static AppSettings Load(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("Version", out var version) ||
                version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1 ||
                !document.RootElement.TryGetProperty("ActiveConfigurationId", out _) ||
                !document.RootElement.TryGetProperty("Configurations", out _) ||
                !document.RootElement.TryGetProperty("Integrations", out _))
            {
                throw new InvalidDataException("Unsupported or legacy settings format. Run the one-time converter described in docs/settings.md.");
            }

            var settings = document.Deserialize<AppSettings>(JsonOptions) ?? throw new InvalidDataException("Empty settings document.");
            AppSettingsValidation.Validate(settings);
            PrivateFile.RestrictAccess(path);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            // Never include JSON parser diagnostics: they can quote saved credentials.
            throw new InvalidDataException("Settings are invalid. Restore a backup or correct settings.json before starting the server.");
        }
    }

    private static AppSettings Copy(AppSettings settings) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;

    public void Dispose() => _gate.Dispose();
}
