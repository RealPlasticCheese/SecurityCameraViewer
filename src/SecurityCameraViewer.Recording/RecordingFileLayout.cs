using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SecurityCameraViewer.Recording;

/// <summary>Maps owned recordings to safe paths under the selected archive root.</summary>
public sealed class RecordingFileLayout(string directory, string? archiveFolder = null)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public string CreateBasePath(RecordingEvent recording)
    {
        var filename = recording.StartedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "_" + recording.Id;
        return (archiveFolder is not null
            ? Path.Combine(archiveFolder, filename)
            : Path.Combine(FolderName(recording.ConfigurationId), FolderName(recording.CameraId),
                recording.StartedAt.UtcDateTime.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), filename)).Replace('\\', '/');
    }

    public string PathFor(RecordingEvent recording, string suffix)
    {
        var relative = recording.FileBasePath;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Split(['/', '\\']).Any(part => part is ".." or "."))
        {
            throw new IOException("The recording catalogue contains an invalid media path.");
        }

        // Catalogue paths remain portable when a recording folder is moved between Windows and Linux.
        relative = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(DirectoryPath, relative + suffix));
        var root = Path.TrimEndingDirectorySeparator(DirectoryPath) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new IOException("The recording media path is outside its archive.");
        }

        return path;
    }

    internal void PruneRecordingDateFolders(RecordingEvent recording)
    {
        if (recording.FileBasePath.Replace('\\', '/') != new RecordingFileLayout(DirectoryPath).CreateBasePath(recording))
        {
            return;
        }

        var day = Path.GetDirectoryName(PathFor(recording, ".mp4"))!;
        var month = Path.GetDirectoryName(day)!;
        var year = Path.GetDirectoryName(month)!;
        var camera = Path.GetDirectoryName(year)!;
        var configuration = Path.GetDirectoryName(camera)!;
        if (IsOrdinaryDirectory(configuration) && IsOrdinaryDirectory(camera) &&
            IsOrdinaryDirectory(year) && IsOrdinaryDirectory(month) && IsOrdinaryDirectory(day))
        {
            RemoveIfEmpty(day);
            RemoveIfEmpty(month);
            RemoveIfEmpty(year);
        }
    }

    internal void PruneExistingDateFolders(string configurationId, CancellationToken token)
    {
        var configuration = Path.Combine(DirectoryPath, FolderName(configurationId));
        foreach (var camera in ChildDirectories(configuration))
        {
            PruneDates(camera, 0);
        }

        void PruneDates(string parent, int depth)
        {
            foreach (var child in ChildDirectories(parent))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(child);
                if (name.Length != (depth == 0 ? 4 : 2) || !name.All(char.IsAsciiDigit) ||
                    !int.TryParse(name, out var number) || number < 1 ||
                    (depth == 1 && number > 12) || (depth == 2 && number > 31))
                {
                    continue;
                }

                if (depth < 2)
                {
                    PruneDates(child, depth + 1);
                }
                RemoveIfEmpty(child);
            }
        }
    }

    private static IEnumerable<string> ChildDirectories(string path)
    {
        try
        {
            return IsOrdinaryDirectory(path)
                ? Directory.GetDirectories(path, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint })
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsOrdinaryDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RemoveIfEmpty(string path)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A nonempty or inaccessible directory can be left in place.
        }
    }

    private static string FolderName(string value) => value.Length is > 0 and <= 100 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') &&
        !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? value : "id-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
