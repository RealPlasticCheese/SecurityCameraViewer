using System.Globalization;

namespace SecurityCameraViewer.Recording;

/// <summary>Immutable buffered media with a wall-clock interval and a trim offset from its first keyframe.</summary>
public sealed record RecordingClip(DateTimeOffset StartedAt, DateTimeOffset EndedAt, double TrimSeconds,
    byte[] Initialization, byte[][] Fragments, double ClockRate = 1)
{
    public double DurationSeconds => (EndedAt - StartedAt).TotalSeconds;
}

public sealed record RecordingImport(RecordingEvent Recording, string VideoPath);

public interface IRecordingClipRenderer
{
    Task RenderAsync(RecordingClip clip, string destination, CancellationToken token);
}

/// <summary>Trims decoded frames so selected boundaries need not coincide with camera keyframes.</summary>
public sealed class FfmpegRecordingClipRenderer(IFfmpegProcess ffmpeg) : IRecordingClipRenderer
{
    public async Task RenderAsync(RecordingClip clip, string destination, CancellationToken token)
    {
        var input = destination + ".input.mp4";
        try
        {
            await using (var stream = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(clip.Initialization, token);
                foreach (var fragment in clip.Fragments)
                {
                    await stream.WriteAsync(fragment, token);
                }
            }

            string[] timing = clip.ClockRate == 1 ? ["-i", input, "-ss", clip.TrimSeconds.ToString("R", CultureInfo.InvariantCulture)]
                : ["-ss", clip.TrimSeconds.ToString("R", CultureInfo.InvariantCulture), "-i", input,
                   "-vf", $"setpts=(PTS-STARTPTS)*{clip.ClockRate.ToString("R", CultureInfo.InvariantCulture)}", "-af", AudioTempo(clip.ClockRate)];
            await ffmpeg.RunAsync(timing.Concat([
                "-t", clip.DurationSeconds.ToString("R", CultureInfo.InvariantCulture),
                "-map", "0:v:0", "-map", "0:a?", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                "-pix_fmt", "yuv420p", "-threads", "1", "-c:a", "aac", "-movflags", "+faststart", destination]),
                null, (stream, ct) => stream.CopyToAsync(Stream.Null, ct), token, "rewind-export");

            try
            {
                await ffmpeg.RunAsync(["-i", destination, "-frames:v", "1", "-vf", "scale=320:-2",
                    "-threads", "1", destination + ".jpg"], null,
                    (stream, ct) => stream.CopyToAsync(Stream.Null, ct), token, "rewind-export");
            }
            catch (IOException)
            {
                File.Delete(destination + ".jpg");
                // A missing thumbnail must not prevent saving playable footage.
            }
        }
        finally
        {
            File.Delete(input);
        }
    }

    private static string AudioTempo(double clockRate)
    {
        if (!double.IsFinite(clockRate) || clockRate <= 0 || !double.IsFinite(1 / clockRate))
        {
            throw new ArgumentException("Recording clock pace must be positive and finite.");
        }
        var tempo = 1 / clockRate;
        var filters = new List<string>();
        // Cascade the documented atempo range so slow/fast sources need no product ceiling.
        while (tempo < 0.5)
        {
            filters.Add("atempo=0.5");
            tempo *= 2;
        }
        while (tempo > 2)
        {
            filters.Add("atempo=2");
            tempo /= 2;
        }
        filters.Add("atempo=" + tempo.ToString("R", CultureInfo.InvariantCulture));
        return string.Join(',', filters);
    }
}
