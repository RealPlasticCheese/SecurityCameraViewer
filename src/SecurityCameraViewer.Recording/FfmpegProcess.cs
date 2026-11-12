using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

public sealed class VideoSourceException(string message) : IOException(message);

public interface IFfmpegProcess
{
    Task RunAsync(IEnumerable<string> arguments, string? input,
        Func<Stream, CancellationToken, Task> read, CancellationToken token, string pipeline = "other", int channel = 0);
}

/// <summary>Bounded process lifetime and diagnostics. Never publishes FFmpeg stderr (which can contain credentials).</summary>
public sealed class FfmpegProcess(string executable) : IFfmpegProcess
{
    public async Task RunAsync(IEnumerable<string> arguments, string? input, Func<Stream, CancellationToken, Task> read,
        CancellationToken token, string pipeline = "other", int channel = 0)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(arguments))
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove("FFREPORT");
        using var process = new Process { StartInfo = info };
        token.ThrowIfCancellationRequested();
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            throw new IOException("FFmpeg could not start. Check the configured executable.");
        }
        using var registration = token.Register(() => Kill(process));
        using var resources = ProcessingMetrics.TrackFfmpeg(process, pipeline, channel);
        await using var outputPump = OperatingSystem.IsWindows()
            ? new ProcessOutputStream(process.StandardOutput.BaseStream, () => Kill(process), token) : null;
        await using var errorPump = OperatingSystem.IsWindows()
            ? new ProcessOutputStream(process.StandardError.BaseStream, () => Kill(process), CancellationToken.None) : null;
        using var errorReader = errorPump is null ? null : new StreamReader(errorPump, process.StandardError.CurrentEncoding, leaveOpen: true);
        var drain = DrainAsync(errorReader ?? process.StandardError);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), token);
            }

            process.StandardInput.Close();
            await read(outputPump ?? process.StandardOutput.BaseStream, token);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0)
            {
                throw new VideoSourceException("FFmpeg could not process the video. Check RTSP credentials, stream path and codec.");
            }
        }
        finally
        {
            resources.Dispose();
            Kill(process);
            // Cancellation ends capture, but cleanup must still reap the child and observe stderr.
            await process.WaitForExitAsync(CancellationToken.None);
            await drain;
        }
    }

    public Task FileAsync(IEnumerable<string> arguments, CancellationToken token) => RunAsync(arguments, null,
        async (stream, ct) => await stream.CopyToAsync(Stream.Null, ct), token);

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer) > 0)
        {
        }
    }
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
