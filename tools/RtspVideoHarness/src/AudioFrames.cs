using System.Text.Json;

namespace RtspVideoHarness;

/// <summary>Distributes 48 kHz stereo PCM samples across video frames without rounding drift.</summary>
public sealed class AudioFrameClock(int framesPerSecond)
{
    private long remainder;

    public int NextByteCount()
    {
        remainder += 48000;
        var samples = remainder / framesPerSecond;
        remainder %= framesPerSecond;
        return checked((int)samples * 4);
    }
}

public static class AudioFrames
{
    public static async Task<byte[]> ReadAsync(Stream? source, int bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frame = new byte[bytes];
        if (source != null)
        {
            var offset = 0;
            while (offset < bytes)
            {
                var read = await source.ReadAsync(frame.AsMemory(offset), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }
        }

        // Missing or shorter source audio is zero-filled; longer audio is trimmed to video duration.
        return frame;
    }

    public static async Task<bool> HasAudioAsync(VideoFile file, HarnessOptions options, CancellationToken cancellationToken)
    {
        var executable = options.FfprobeExecutable ?? Path.Combine(Path.GetDirectoryName(options.FfmpegExecutable) ?? "",
            OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        await using var probe = new ManagedProcess(executable,
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_type", "-of", "json", file.FullPath],
            "FFprobe", binaryOutput: true);
        using var reader = new StreamReader(probe.StandardOutput);
        var json = await reader.ReadToEndAsync(cancellationToken).WaitAsync(options.ProcessTimeout, cancellationToken);
        await probe.EnsureSuccessAsync(cancellationToken).WaitAsync(options.ProcessTimeout, cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("streams").GetArrayLength() > 0;
    }
}
