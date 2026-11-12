using System.ComponentModel;
using System.Diagnostics;

namespace SecurityCameraViewer.Web.Browser;

internal sealed class BrowserLauncher(IBrowserPlatform platform, ILogger<BrowserLauncher> logger)
{
    public bool Launch(BrowserLaunchOptions options, Uri address)
    {
        try
        {
            if (platform.IsLinux && string.IsNullOrWhiteSpace(platform.GetEnvironmentVariable("DISPLAY")) &&
                string.IsNullOrWhiteSpace(platform.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            {
                throw new InvalidOperationException("A graphical desktop session is required. Connect from a desktop computer instead.");
            }
            if (options.Browser == "default")
            {
                OpenDefault(address);
                return true;
            }
            var locator = new BrowserLocator(platform);
            if (options.BrowserPath is not null)
            {
                var executable = locator.FindExecutable(options.BrowserPath);
                OpenApp(executable, address);
                return true;
            }
            var browsers = options.Browser == "auto"
                ? platform.IsWindows ? new[] { "edge", "chrome", "chromium", "brave" } : ["chrome", "chromium", "edge", "brave"]
                : [options.Browser];
            Exception? lastFailure = null;
            foreach (var browser in browsers)
            {
                if (locator.Find(browser) is not { } executable)
                {
                    continue;
                }
                try
                {
                    OpenApp(executable, address);
                    return true;
                }
                catch (Exception exception) when (IsLaunchFailure(exception))
                {
                    if (options.Browser != "auto")
                    {
                        throw;
                    }
                    lastFailure = exception;
                    logger.LogWarning(exception, "Could not launch {Browser}; trying another browser.", browser);
                }
            }
            if (options.Browser != "auto")
            {
                throw new FileNotFoundException($"The selected browser '{options.Browser}' was not found. Install it or use --browser-path.");
            }
            logger.LogWarning(lastFailure, "No app-mode browser could be launched. Opening the default browser in a regular window.");
            OpenDefault(address);
            return true;
        }
        catch (Exception exception) when (IsLaunchFailure(exception))
        {
            logger.LogWarning(exception, "Could not open the viewer. Browse to {Address} manually.", address);
            return false;
        }
    }

    private void OpenApp(string executable, Uri address)
    {
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        startInfo.ArgumentList.Add("--app=" + address.AbsoluteUri);
        platform.Start(startInfo);
        logger.LogInformation("Opened viewer app window at {Address}.", address);
    }

    private void OpenDefault(Uri address)
    {
        platform.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
        logger.LogInformation("Opened viewer in the default browser at {Address}.", address);
    }

    private static bool IsLaunchFailure(Exception exception) =>
        exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException;
}
