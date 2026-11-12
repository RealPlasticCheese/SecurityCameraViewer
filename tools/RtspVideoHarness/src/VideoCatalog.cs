namespace RtspVideoHarness;

public sealed record VideoFile(string FullPath, string RelativePath, DateTime CreatedUtc, DateTime ModifiedUtc);

public static class VideoCatalog
{
    public static IReadOnlyList<VideoFile> Discover(HarnessOptions options)
    {
        if (!Directory.Exists(options.Folder))
        {
            throw new DirectoryNotFoundException($"Video folder does not exist: {options.Folder}");
        }

        var extensions = options.Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(options.Folder, "*", options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(path => extensions.Contains("*") || extensions.Contains(Path.GetExtension(path)))
            .Select(path => new FileInfo(path))
            .Select(file => new VideoFile(file.FullName, Path.GetRelativePath(options.Folder, file.FullName), file.CreationTimeUtc, file.LastWriteTimeUtc));
        var ordered = Sort(files, options.Order, options.Descending);
        if (ordered.Count == 0)
        {
            throw new InvalidOperationException($"No matching video files in {options.Folder}. Use --recursive or --extensions if needed.");
        }

        return ordered;
    }

    public static IReadOnlyList<VideoFile> Sort(IEnumerable<VideoFile> files, FileOrder order, bool descending)
    {
        var byName = files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal);
        IEnumerable<VideoFile> ordered = order switch
        {
            FileOrder.Name => byName,
            FileOrder.Created => byName.OrderBy(file => file.CreatedUtc),
            FileOrder.Modified => byName.OrderBy(file => file.ModifiedUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        };
        return (descending ? ordered.Reverse() : ordered).ToArray();
    }
}
