using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SecurityCameraViewer.Recording;

internal static class AudioRecordingTests
{
    public static async Task RunAsync(string root)
    {
        var ffmpeg = new FfmpegProcess("ffmpeg");
        var source = Path.Combine(root, "audio-first.mp4");
        await ffmpeg.FileAsync([
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000",
            "-t", "8", "-map", "1:a:0", "-map", "0:v:0", "-c:v", "libx264",
            "-preset", "ultrafast", "-g", "10", "-bf", "0", "-c:a", "aac", "-b:a", "64k", source
        ], CancellationToken.None);

        byte[]? initialization = null;
        var fragments = new List<VideoFragment>();
        await ffmpeg.RunAsync([
            "-i", source, "-map", "0", "-c", "copy", "-movflags",
            "+empty_moov+frag_keyframe+default_base_moof", "-f", "mp4", "pipe:1"
        ], null, (stream, token) => FragmentedMp4.ReadAsync(stream, bytes =>
        {
            initialization = bytes;
            return Task.CompletedTask;
        }, fragment =>
        {
            fragments.Add(fragment);
            return Task.CompletedTask;
        }, token), CancellationToken.None);

        Check(initialization is not null && fragments.Count >= 8 &&
            Math.Abs(fragments.Sum(fragment => fragment.DurationSeconds) - 8) < .2,
            "Audio-first MP4 tracks use the video clock and track ID for fragment durations");

        var settings = new RecordingSettings
        {
            Directory = Path.Combine(root, "audio-library"),
            RetentionDays = 365
        };
        var store = new FileRecordingStore(settings, ffmpeg);
        var buffered = fragments.TakeLast(4).ToArray();
        await using (var writer = await store.BeginAsync(1, "Audio camera", initialization!,
            buffered.Sum(fragment => fragment.DurationSeconds), CancellationToken.None))
        {
            foreach (var fragment in buffered)
            {
                await writer.AppendAsync(fragment, CancellationToken.None);
            }

            await writer.CompleteAsync(false, CancellationToken.None);
        }

        await store.MaintainAsync(CancellationToken.None);
        var recording = store.List(null, null).Single();
        Check(recording.Status == "ready" && Math.Abs(recording.DurationSeconds - 4) < .2,
            "Buffered audio and video finalize with video-based event duration");
        await VerifyClipAsync(store.MediaPath(recording.Id)!, ffmpeg, recording.DurationSeconds);

        string interruptedId;
        await using (var writer = await store.BeginAsync(1, "Interrupted audio camera", initialization!,
            0, CancellationToken.None))
        {
            interruptedId = writer.Id ?? throw new InvalidOperationException("Missing recording ID.");
            foreach (var fragment in buffered.Take(2))
            {
                await writer.AppendAsync(fragment, CancellationToken.None);
            }

            await writer.CompleteAsync(true, CancellationToken.None);
        }

        var restarted = new FileRecordingStore(settings, ffmpeg);
        await restarted.MaintainAsync(CancellationToken.None);
        var recovered = restarted.Find(interruptedId)!;
        Check(recovered.Status == "ready" && recovered.Interrupted &&
            Math.Abs(recovered.DurationSeconds - 2) < .2,
            "Restart recovery counts video duration once while preserving audio fragments");
        await VerifyClipAsync(restarted.MediaPath(recovered.Id)!, ffmpeg, recovered.DurationSeconds);
    }

    public static async Task VerifyClipAsync(string path, FfmpegProcess ffmpeg, double durationSeconds,
        bool expectAudio = true)
    {
        var start = new ProcessStartInfo("ffprobe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-count_packets", "-show_entries",
            "stream=codec_type,codec_name,start_time,duration,nb_read_packets", "-of", "json", path
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var probe = Process.Start(start)!;
        using var registration = deadline.Token.Register(() =>
        {
            if (!probe.HasExited)
            {
                probe.Kill();
            }
        });
        var output = probe.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = probe.StandardError.ReadToEndAsync(deadline.Token);
        await probe.WaitForExitAsync(deadline.Token);
        await errors;
        using var document = JsonDocument.Parse(await output);
        Check(probe.ExitCode == 0, "Recorded MP4 can be inspected");

        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "video");
        Check(video.GetProperty("codec_name").GetString() == "h264" &&
            Math.Abs(Number(video, "duration") - durationSeconds) < .2,
            "Saved video codec and duration are preserved");
        var audioStreams = streams.Where(stream => stream.GetProperty("codec_type").GetString() == "audio").ToArray();
        Check(expectAudio ? audioStreams.Length > 0 : audioStreams.Length == 0,
            expectAudio ? "RTSP audio is retained in the MP4" : "Streams without audio still record video successfully");
        foreach (var audio in audioStreams)
        {
            Check(audio.GetProperty("codec_name").GetString() == "aac" && Number(audio, "nb_read_packets") > 0,
                "Saved audio is AAC with actual packets");
            Check(Math.Abs(Number(audio, "start_time") - Number(video, "start_time")) < .2 &&
                Math.Abs(Number(audio, "duration") - Number(video, "duration")) < .3 &&
                Number(video, "start_time") < .2,
                "Cut clips retain synchronized audio and video on a timeline starting near zero");
        }

        await ffmpeg.FileAsync(["-xerror", "-i", path, "-f", "null", "-"], deadline.Token);
        if (expectAudio)
        {
            var audible = false;
            await ffmpeg.RunAsync(["-i", path, "-map", "0:a:0", "-f", "s16le", "pipe:1"], null,
                async (stream, token) =>
                {
                    var samples = new byte[4096];
                    int count;
                    while ((count = await stream.ReadAsync(samples, token)) > 0)
                    {
                        audible |= samples.AsSpan(0, count).ContainsAnyExcept((byte)0);
                    }
                }, deadline.Token);
            Check(audible, "Recorded audio decodes to non-silent samples");
        }
    }

    private static double Number(JsonElement stream, string property) =>
        double.Parse(stream.GetProperty(property).GetString()!, CultureInfo.InvariantCulture);

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAIL: " + message);
        }

        Console.WriteLine("PASS: " + message);
    }
}
