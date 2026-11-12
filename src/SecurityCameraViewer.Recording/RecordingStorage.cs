namespace SecurityCameraViewer.Recording;

public static class RecordingStorage
{
    /// <summary>Select the containing mount, including a separate Linux recording disk.</summary>
    public static long? FreeBytes(string directory)
    {
        static string WithSeparator(string value) => Path.EndsInDirectorySeparator(value) ? value : value + Path.DirectorySeparatorChar;
        var path = WithSeparator(Path.GetFullPath(directory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var drive = DriveInfo.GetDrives().Where(d => path.StartsWith(WithSeparator(d.Name), comparison))
            .OrderByDescending(d => d.Name.Length).FirstOrDefault();
        return drive is { IsReady: true } ? drive.AvailableFreeSpace : null;
    }
}
