using System.ComponentModel;
using System.Diagnostics;

namespace SecurityCameraViewer.Web.Streaming;

public interface IMediaRelayProcess : IAsyncDisposable
{
    bool Running { get; }
    Task StartAsync(string executable, string configurationPath, CancellationToken token);
    Task StopAsync();
}

/// <summary>Owns only the child process. Both output pipes are drained without logging upstream credentials.</summary>
public sealed class MediaMtxProcess : IMediaRelayProcess
{
    private Process? _process;
    private Task _output = Task.CompletedTask;
    private Task _errors = Task.CompletedTask;
    public bool Running => _process is { HasExited: false };

    public Task StartAsync(string executable, string configurationPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_process is not null)
        {
            throw new InvalidOperationException("Stop the relay before starting another process.");
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "mediamtx.exe" : "mediamtx");
        var info = new ProcessStartInfo(executable == "mediamtx" && File.Exists(bundled) ? bundled : executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Prevent inherited MediaMTX environment overrides from changing authentication or listeners.
        foreach (var name in info.Environment.Keys.Where(k => k.StartsWith("MTX_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            info.Environment.Remove(name);
        }

        info.ArgumentList.Add(configurationPath);
        var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            _process = process;
            _output = DrainAsync(process.StandardOutput);
            _errors = DrainAsync(process.StandardError);
        }
        catch (Win32Exception)
        {
            process.Dispose();
            throw new IOException("MediaMTX could not start. Install it or set its executable path in RTSP & ONVIF service.");
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            await process.WaitForExitAsync();
            await Task.WhenAll(_output, _errors);
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer) > 0)
        {
        }
    }
}
