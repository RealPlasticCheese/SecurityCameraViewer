using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SecurityCameraViewer.Core;

public sealed record StreamMediaInfo(string? Codec, int? Width, int? Height, string? FrameRate, string? Bitrate, string? Error);

/// <summary>Optional ffprobe inspection. Only allowlisted media fields leave the private subprocess.</summary>
public static class MediaInspector
{
    public static async Task<StreamMediaInfo> InspectAsync(ViewerSettings settings, string password, int channel,
        string ffmpeg, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(ffmpeg);
        var executable = string.IsNullOrEmpty(directory) ? "ffprobe" : Path.Combine(directory, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in new[] { "-v", "error", "-f", "concat", "-safe", "0", "-protocol_whitelist", "pipe,tcp,udp,rtp", "-i", "pipe:0",
            "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height,r_frame_rate,bit_rate", "-of", "json" })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove("FFREPORT");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var output = ReadOutputAsync(process.StandardOutput, deadline.Token);
            try
            {
                await process.StandardInput.WriteAsync(FfmpegRtspInput.Create(settings, password, channel).AsMemory(), deadline.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(deadline.Token);
                await Task.WhenAll(errors, output);
                if (process.ExitCode != 0)
                {
                    return new(null, null, null, null, null, "ffprobe could not inspect this stream.");
                }

                return Parse(await output);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }

                await process.WaitForExitAsync(CancellationToken.None);
                try
                {
                    await Task.WhenAll(errors, output);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidDataException or JsonException or InvalidOperationException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(null, null, null, null, null, "Media properties unavailable. Check ffprobe installation, stream reachability and camera session limits.");
        }
    }

    internal static StreamMediaInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array ||
            streams.EnumerateArray().FirstOrDefault() is not { ValueKind: JsonValueKind.Object } stream)
        {
            return new(null, null, null, null, null, "No video stream properties returned.");
        }

        string? Text(string key) => stream.TryGetProperty(key, out var value) ? value.ToString() : null;
        int? Number(string key) => stream.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var number) ? number : null;
        return new(Text("codec_name"), Number("width"), Number("height"), Text("r_frame_rate"), Text("bit_rate"), null);
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + count > 65536)
            {
                throw new InvalidDataException("Media inspection exceeded its output limit.");
            }

            result.Append(buffer, 0, count);
        }

        return result.ToString();
    }
}
