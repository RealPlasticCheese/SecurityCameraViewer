using System.Diagnostics;

namespace SecurityCameraViewer.Web.Browser;

internal interface IBrowserPlatform
{
    bool IsWindows { get; }
    bool IsLinux { get; }
    string? GetEnvironmentVariable(string name);
    bool IsExecutable(string path);
    void Start(ProcessStartInfo startInfo);
}

internal sealed class BrowserPlatform : IBrowserPlatform
{
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsLinux => OperatingSystem.IsLinux();

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public bool IsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        if (OperatingSystem.IsLinux())
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        return true;
    }

    public void Start(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The operating system did not start a browser process.");
    }
}
