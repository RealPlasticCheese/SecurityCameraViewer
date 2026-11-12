namespace SecurityCameraViewer.Recording;

public sealed record RecordingLocation(string Directory, double MaximumStorageGigabytes, string? Id = null)
{
    public bool RegisterReplacement { get; init; }
    internal const string IdentityFile = ".recording-location-id";
    public static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal string ArchiveDirectory => Id is null ? Directory : Path.Combine(Directory, "archive-" + Id);

    public static string Normalize(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    public static bool ValidId(string? id) => Guid.TryParseExact(id, "N", out _);

    public static bool ValidQuota(double gigabytes) => gigabytes * 1_000_000_000 is >= 1 and < (double)long.MaxValue;

    public bool HasIdentity() => File.Exists(Path.Combine(Directory, IdentityFile));

    public RecordingLocation Register(string? migrateConfiguration = null)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            throw new ArgumentException($"Mount the drive and create the recording folder before adding {Directory}.");
        }

        var marker = Path.Combine(Directory, IdentityFile);
        var id = ReadIdentity();
        if (id is null)
        {
            id = Guid.NewGuid().ToString("N");
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(id);
        }
        else if (!ValidId(id))
        {
            throw new ArgumentException($"The recording folder identity in {Directory} is invalid.");
        }

        var location = this with { Id = id };
        if (location.ReadIdentity() != id)
        {
            throw new RecordingLocationException("offline", "The recording folder disconnected during registration.");
        }
        System.IO.Directory.CreateDirectory(location.ArchiveDirectory);
        location.EnsureAvailable();
        if (migrateConfiguration is not null)
        {
            new SqliteRecordingCatalog(location.ArchiveDirectory, migrateConfiguration, createDirectory: false)
                .Migrate(location.Directory);
        }
        return location;
    }

    internal void EnsureAvailable()
    {
        if (Id is null)
        {
            return;
        }
        var actual = ReadIdentity();
        if (!ValidId(Id) || actual is not null && actual != Id)
        {
            throw new RecordingLocationException("wrong-folder", "A different recording folder is mounted here.");
        }
        if (actual is null || !System.IO.Directory.Exists(ArchiveDirectory))
        {
            throw new RecordingLocationException("offline", "The recording folder is offline.");
        }
    }

    private string? ReadIdentity()
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(Directory, IdentityFile));
            return stream.Length == 32
                ? new StreamReader(stream).ReadToEnd()
                : "";
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}

internal sealed class RecordingLocationException(string status, string message, long requiredBytes = 0) : IOException(message)
{
    public string Status { get; } = status;
    public long RequiredBytes { get; } = requiredBytes;
}
