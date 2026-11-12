using System.Text.Json;
using SecurityCameraViewer.Nvr;

namespace SecurityCameraViewer.Web.Nvr;

public sealed record NvrSavedExport(string Id, NvrRecordingRequest Recording, DateTimeOffset Created, long SizeBytes);

public sealed class NvrExportStore(WebSettingsStore viewer, IConfiguration configuration)
{
    private readonly string _directory = Path.GetFullPath(configuration["nvr-export-directory"] ?? Path.Combine(Path.GetDirectoryName(viewer.FilePath)!, "nvr-exports"));
    private readonly object _sync = new();

    public IReadOnlyList<NvrSavedExport> List()
    {
        lock (_sync)
        {
            if (!Directory.Exists(_directory))
            {
                return [];
            }

            var result = new List<NvrSavedExport>();
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                try
                {
                    var saved = JsonSerializer.Deserialize<NvrSavedExport>(File.ReadAllText(path));
                    if (saved is not null && IsId(saved.Id) && Path.GetFileNameWithoutExtension(path) == saved.Id && File.Exists(VideoPath(saved.Id)))
                    {
                        result.Add(saved);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    // Ignore incomplete indexes; file paths are always derived from validated identifiers.
                }
            }

            return result.OrderByDescending(e => e.Created).ToArray();
        }
    }

    public string TemporaryPath(string id, string extension)
    {
        ValidateId(id);
        if (extension is not ("mkv" or "mp4"))
        {
            throw new ArgumentException("Unsupported export format.");
        }

        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return Path.Combine(_directory, $"{id}.partial.{extension}");
    }

    public NvrSavedExport Commit(string id, NvrRecordingRequest recording)
    {
        lock (_sync)
        {
            var temporary = TemporaryPath(id, "mp4");
            var length = new FileInfo(temporary).Length;
            if (length == 0)
            {
                throw new NvrException("The exported recording is empty.");
            }

            var saved = new NvrSavedExport(id, recording, DateTimeOffset.UtcNow, length);
            File.Move(temporary, VideoPath(id));
            try
            {
                File.WriteAllText(IndexPath(id) + ".tmp", JsonSerializer.Serialize(saved));
                File.Move(IndexPath(id) + ".tmp", IndexPath(id));
            }
            catch
            {
                File.Delete(VideoPath(id));
                throw;
            }

            return saved;
        }
    }

    public string? FindVideo(string id) => IsId(id) && List().Any(e => e.Id == id) ? VideoPath(id) : null;

    public bool Delete(string id)
    {
        lock (_sync)
        {
            if (!IsId(id) || !File.Exists(IndexPath(id)))
            {
                return false;
            }

            File.Delete(IndexPath(id));
            File.Delete(VideoPath(id));
            return true;
        }
    }

    public void Cleanup(string id)
    {
        ValidateId(id);
        foreach (var path in new[] { Path.Combine(_directory, $"{id}.partial.mkv"), Path.Combine(_directory, $"{id}.partial.mp4"), IndexPath(id) + ".tmp" })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void CleanupAbandonedTransfers()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*.partial.*"))
            {
                var name = Path.GetFileName(path);
                if (name.Length > 32 && IsId(name[..32]))
                {
                    Cleanup(name[..32]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string VideoPath(string id) => Path.Combine(_directory, id + ".mp4");
    private string IndexPath(string id) => Path.Combine(_directory, id + ".json");
    private static bool IsId(string id) => Guid.TryParseExact(id, "N", out _);
    private static void ValidateId(string id)
    {
        if (!IsId(id))
        {
            throw new ArgumentException("Invalid export identifier.");
        }
    }
}
