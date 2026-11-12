namespace SecurityCameraViewer.Web.Browser;

internal sealed class BrowserLocator(IBrowserPlatform platform)
{
    public string? Find(string browser)
    {
        foreach (var path in Candidates(browser).Distinct(StringComparer.Ordinal))
        {
            if (platform.IsExecutable(path))
            {
                return path;
            }
        }
        return null;
    }

    public string FindExecutable(string path)
    {
        if (platform.IsExecutable(path))
        {
            return Path.GetFullPath(path);
        }
        if (Path.GetFileName(path) == path)
        {
            var separator = platform.IsWindows ? ';' : ':';
            foreach (var directory in (platform.GetEnvironmentVariable("PATH") ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim('"'), path);
                if (platform.IsExecutable(candidate))
                {
                    return candidate;
                }
            }
        }
        throw new FileNotFoundException("The --browser-path executable was not found or is not executable.");
    }

    internal IEnumerable<string> Candidates(string browser)
    {
        var names = platform.IsWindows
            ? browser switch
            {
                "edge" => new[] { "msedge.exe" },
                "chrome" => ["chrome.exe"],
                "chromium" => ["chromium.exe"],
                "brave" => ["brave.exe"],
                _ => Array.Empty<string>()
            }
            : browser switch
            {
                "edge" => new[] { "microsoft-edge", "microsoft-edge-stable" },
                "chrome" => ["google-chrome", "google-chrome-stable"],
                "chromium" => ["chromium", "chromium-browser"],
                "brave" => ["brave-browser", "brave-browser-stable"],
                _ => Array.Empty<string>()
            };
        var separator = platform.IsWindows ? ';' : ':';
        foreach (var directory in (platform.GetEnvironmentVariable("PATH") ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                yield return Path.Combine(directory.Trim('"'), name);
            }
        }
        if (!platform.IsWindows)
        {
            yield break;
        }
        var relativePath = browser switch
        {
            "edge" => Path.Combine("Microsoft", "Edge", "Application", "msedge.exe"),
            "chrome" => Path.Combine("Google", "Chrome", "Application", "chrome.exe"),
            "chromium" => Path.Combine("Chromium", "Application", "chrome.exe"),
            "brave" => Path.Combine("BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            _ => null
        };
        if (relativePath is null)
        {
            yield break;
        }
        foreach (var variable in new[] { "LOCALAPPDATA", "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
        {
            if (platform.GetEnvironmentVariable(variable) is { Length: > 0 } root)
            {
                yield return Path.Combine(root, relativePath);
            }
        }
    }
}
