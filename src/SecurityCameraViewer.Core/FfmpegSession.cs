using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SecurityCameraViewer.Core;

/// <summary>
/// One RTSP-to-JPEG attempt. FFmpeg owns the decoder; this worker never accesses UI objects.
/// Only the newest complete frame is retained, so slow rendering cannot build up a queue.
/// </summary>
public sealed class FfmpegSession : IAsyncDisposable
{
    private readonly Lock _frameLock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private byte[] _latest = [];
    private readonly string _executable;
    private long _frameTimestamp;
    private bool _hasFrame;
    private string? _failure;
    private Task? _disposeTask;

    public FfmpegSession(ViewerSettings settings, string password, int channel,
        string executable = "ffmpeg")
    {
        _executable = executable;
        _worker = Task.Run(() => RunAsync(settings, password, channel));
    }

    public bool IsCompleted => _worker.IsCompleted;
    public string Failure => Volatile.Read(ref _failure) ?? "Camera connection ended";

    // The consumer copies synchronously while the worker's other buffer receives video.
    public bool ConsumeFrame(Action<byte[]> copy, out long timestamp)
    {
        lock (_frameLock)
        {
            timestamp = _frameTimestamp;
            if (!_hasFrame)
            {
                return false;
            }

            copy(_latest);
            _hasFrame = false;
            return true;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable = "ffmpeg")
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
        // The authenticated URL travels only over stdin, never command arguments or a temporary file.
        // -vsync 0 prevents manufacturing duplicate frames when the stream stalls.
        string[] arguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "2",
            "-f", "concat", "-safe", "0", "-protocol_whitelist", "pipe,tcp,udp,rtp",
            "-analyzeduration", "1000000", "-probesize", "1000000", "-i", "pipe:0",
            "-map", "0:v:0", "-an", "-sn", "-dn", "-filter_threads", "1"];
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        string[] output = ["-vf", "select='isnan(prev_selected_t)+gte(t-prev_selected_t,0.099)',scale=w='min(1280,iw)':h='min(720,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2",
                "-vsync", "0", "-threads", "1", "-c:v", "mjpeg", "-q:v", "5", "-pix_fmt", "yuvj420p", "-f", "image2pipe", "pipe:1"];
        foreach (var argument in output)
        {
            info.ArgumentList.Add(argument);
        }
        // An inherited FFREPORT could write credential-bearing FFmpeg diagnostics to disk.
        info.Environment.Remove("FFREPORT");
        return info;
    }

    internal static string CreateInput(ViewerSettings settings, string password, int channel)
        => FfmpegRtspInput.Create(settings, password, channel);

    private async Task RunAsync(ViewerSettings settings, string password, int channel)
    {
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            using var process = new Process { StartInfo = CreateStartInfo(_executable) };
            if (!process.Start())
            {
                _failure = "FFmpeg could not start";
                return;
            }
            using var registration = _stop.Token.Register(() => Kill(process));
            using var resources = ProcessingMetrics.TrackFfmpeg(process, "preview", channel);
            await using var outputPump = OperatingSystem.IsWindows()
                ? new ProcessOutputStream(process.StandardOutput.BaseStream, () => Kill(process), _stop.Token) : null;
            await using var errorPump = OperatingSystem.IsWindows()
                ? new ProcessOutputStream(process.StandardError.BaseStream, () => Kill(process), CancellationToken.None) : null;
            using var errorReader = errorPump is null ? null : new StreamReader(errorPump, process.StandardError.CurrentEncoding, leaveOpen: true);
            var errors = ReadErrorsAsync(errorReader ?? process.StandardError);
            try
            {
                await process.StandardInput.WriteAsync(CreateInput(settings, password, channel).AsMemory(), _stop.Token);
                process.StandardInput.Close();
                Stream output = outputPump ?? process.StandardOutput.BaseStream;
                await JpegFrames.ReadAsync(output, frame =>
                {
                    lock (_frameLock)
                    {
                        _latest = frame;
                        _frameTimestamp = Stopwatch.GetTimestamp();
                        _hasFrame = true;
                    }
                }, _stop.Token);
            }
            finally
            {
                resources.Dispose();
                Kill(process);
                await process.WaitForExitAsync();
                await errors;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Win32Exception)
        {
            _failure = "FFmpeg could not start. Install FFmpeg and add it to PATH.";
        }
        catch (Exception ex)
        {
            _failure ??= $"Playback stopped ({ex.GetType().Name})";
        }
    }

    private async Task ReadErrorsAsync(StreamReader reader)
    {
        // Drain stderr without retaining or displaying raw URLs, passwords, or unlimited log output.
        var buffer = new char[2048];
        var tail = "";
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            var text = tail + new string(buffer, 0, count);
            if (text.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                _failure = "RTSP login rejected. Check the recorder username and password";
            }
            else if (text.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase))
            {
                _failure = "RTSP viewing permission denied";
            }
            else if (text.Contains("404 Not Found", StringComparison.OrdinalIgnoreCase))
            {
                _failure = "Camera stream path not found";
            }
            else if (text.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
            {
                _failure = "Recorder refused the RTSP connection";
            }

            tail = text.Length > 100 ? text[^100..] : text;
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

    public ValueTask DisposeAsync() => new(_disposeTask ??= StopAsync());

    private async Task StopAsync()
    {
        await _stop.CancelAsync();
        await _worker;
        _stop.Dispose();
        lock (_frameLock)
        {
            _hasFrame = false;
            _latest = [];
        }
    }
}
