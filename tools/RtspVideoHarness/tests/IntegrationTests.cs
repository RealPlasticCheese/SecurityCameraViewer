using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RtspVideoHarness.Tests;

internal static class IntegrationTests
{
    public static async Task RunAsync(string[] args)
    {
        string? Argument(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        var mediaMtx = Path.GetFullPath(Argument("--mediamtx") ?? Path.Combine("tools", "RtspVideoHarness", "artifacts", OperatingSystem.IsWindows() ? "mediamtx.exe" : "mediamtx"));
        if (!File.Exists(mediaMtx))
        {
            throw new FileNotFoundException("Build the harness or pass --mediamtx PATH for integration tests.", mediaMtx);
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new HarnessOptions
        {
            Folder = directory,
            MediaMtxExecutable = mediaMtx,
            FfmpegExecutable = Argument("--ffmpeg") ?? "ffmpeg",
            FfprobeExecutable = Argument("--ffprobe"),
            Format = new(64, 48, 10),
            Port = FreePort(),
            BlankPeriod = TimeSpan.FromMilliseconds(400)
        };
        var before = ChildIds();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var red = Path.Combine(directory, "01 red's video.mp4");
            var blue = Path.Combine(directory, "02 blue.mp4");
            await RunAsync(options.FfmpegExecutable, ["-v", "error", "-f", "lavfi", "-i", "color=c=red:s=96x54:r=12",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "1.6", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", red], deadline.Token);
            await RunAsync(options.FfmpegExecutable, ["-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=64x48:r=10",
                "-t", "1.2", "-c:v", "libx264", "-pix_fmt", "yuv420p", blue], deadline.Token);
            var files = VideoCatalog.Discover(options);
            Check.That(await AudioFrames.HasAudioAsync(files[0], options, deadline.Token) &&
                !await AudioFrames.HasAudioAsync(files[1], options, deadline.Token), "Real probe distinguishes clips with and without audio");
            await ContinuousAsync(options, files, directory, deadline.Token);
            await OfflineAsync(options with
            {
                Port = FreePort(),
                OfflineGaps = true,
                BlankPeriod = TimeSpan.FromMilliseconds(700)
            }, files, deadline.Token);
            await FailurePathsAsync(options with
            {
                Port = FreePort()
            }, files, directory, deadline.Token);
            await YouTubeIntegrationTests.RunAsync(options, directory, files, deadline.Token);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Check.That(!ChildIds().Except(before).Any(), "All harness-owned FFmpeg, FFprobe and MediaMTX children are cleaned up");
    }

    private static async Task ContinuousAsync(HarnessOptions options, IReadOnlyList<VideoFile> files, string directory, CancellationToken token)
    {
        await using var server = await MediaMtxServer.StartAsync(options, token);
        await using var playback = new FfmpegPlayback(options);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var player = new PlaylistPlayer(playback).RunAsync(files, options, stop.Token);
        try
        {
            var description = await UntilAsync(options.StreamUri, status => status == 200, player, token);
            Check.That(description.Contains("H264", StringComparison.OrdinalIgnoreCase) && description.Contains("MPEG4-GENERIC", StringComparison.OrdinalIgnoreCase),
                "Real RTSP session advertises H.264 video and AAC audio");

            var secondOptions = options with
            {
                Port = FreePort(),
                AudioEnabled = false
            };
            await using (var secondServer = await MediaMtxServer.StartAsync(secondOptions, token))
            await using (var secondPlayback = new FfmpegPlayback(secondOptions))
            {
                using var secondStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                var secondPlayer = new PlaylistPlayer(secondPlayback).RunAsync([files[1]], secondOptions, secondStop.Token);
                try
                {
                    var secondDescription = await UntilAsync(secondOptions.StreamUri, status => status == 200, secondPlayer, token);
                    Check.That(!secondDescription.Contains("m=audio", StringComparison.Ordinal) && (await DescribeAsync(options.StreamUri, token)).Status == 200,
                        "Two instances serve different configurable ports concurrently, including video-only mode");
                }
                finally
                {
                    secondStop.Cancel();
                    await ExpectStoppedAsync(secondPlayer);
                }
            }

            Check.That((await DescribeAsync(options.StreamUri, token)).Status == 200, "Stopping another instance leaves the first stream available");
            var recording = Path.Combine(directory, "capture.mkv");
            await RunAsync(options.FfmpegExecutable, ["-v", "error", "-rtsp_transport", "tcp", "-i", options.StreamUri.AbsoluteUri,
                "-t", "5", "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", recording], token);
            var pixels = await RunAsync(options.FfmpegExecutable, ["-v", "error", "-i", recording, "-an", "-vf", "scale=1:1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"], token);
            var sequence = new List<string>();
            for (var index = 0; index + 2 < pixels.Length; index += 3)
            {
                var color = pixels[index] > 100 && pixels[index + 2] < 70 ? "red" :
                    pixels[index + 2] > 100 && pixels[index] < 70 ? "blue" :
                    pixels[index] < 20 && pixels[index + 1] < 20 && pixels[index + 2] < 20 ? "black" : "other";
                if (sequence.Count == 0 || sequence[^1] != color)
                {
                    sequence.Add(color);
                }
            }

            var joined = string.Join(',', sequence);
            Check.That(joined.Contains("red,black,blue", StringComparison.Ordinal) && joined.Contains("blue,black,red", StringComparison.Ordinal),
                $"One uninterrupted reader receives ordered clips, black gaps and looping: {joined}");
            var pcm = await RunAsync(options.FfmpegExecutable, ["-v", "error", "-i", recording, "-vn", "-ac", "2", "-ar", "48000", "-f", "s16le", "pipe:1"], token);
            var longestSilence = 0;
            var silence = 0;
            var hasTone = false;
            for (var index = 0; index + 1 < pcm.Length; index += 2)
            {
                var sample = (short)(pcm[index] | pcm[index + 1] << 8);
                hasTone |= Math.Abs((int)sample) > 500;
                silence = Math.Abs((int)sample) < 5 ? silence + 1 : 0;
                longestSilence = Math.Max(longestSilence, silence);
            }

            Check.That(hasTone && longestSilence > 48000, "Real AAC contains source tone and sustained silence for gaps/video without audio");
        }
        finally
        {
            stop.Cancel();
            await ExpectStoppedAsync(player);
        }
    }

    private static async Task OfflineAsync(HarnessOptions options, IReadOnlyList<VideoFile> files, CancellationToken token)
    {
        await using var server = await MediaMtxServer.StartAsync(options, token);
        await using var playback = new FfmpegPlayback(options);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var player = new PlaylistPlayer(playback).RunAsync(files, options, stop.Token);
        try
        {
            await UntilAsync(options.StreamUri, status => status == 200, player, token);
            await UntilAsync(options.StreamUri, status => status == 404, player, token);
            Check.That(!server.Completion.IsCompleted, "Offline gap removes the stream while the RTSP listener remains running");
            var description = await UntilAsync(options.StreamUri, status => status == 200, player, token);
            Check.That(description.Contains("m=audio", StringComparison.Ordinal), "Offline gap republishes audio/video to the same RTSP URL");
            var frame = await RunAsync(options.FfmpegExecutable, ["-v", "error", "-rtsp_transport", "tcp", "-i", options.StreamUri.AbsoluteUri,
                "-an", "-frames:v", "1", "-vf", "scale=1:1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"], token);
            Check.That(frame.Length == 3, "A reconnecting RTSP reader decodes video after camera wakeup");
        }
        finally
        {
            stop.Cancel();
            await ExpectStoppedAsync(player);
        }
    }

    private static async Task FailurePathsAsync(HarnessOptions options, IReadOnlyList<VideoFile> files, string directory, CancellationToken token)
    {
        using var occupied = new TcpListener(IPAddress.Loopback, options.Port);
        occupied.Start();
        await Check.ThrowsAsync<IOException>(async () =>
        {
            await using var failed = await MediaMtxServer.StartAsync(options, token);
        }, "Occupied RTSP port fails explicitly instead of connecting to someone else's listener");
        occupied.Stop();
        await using var server = await MediaMtxServer.StartAsync(options, token);
        var badFile = Path.Combine(directory, "corrupt.mp4");
        await File.WriteAllTextAsync(badFile, "not a video", token);
        await using var playback = new FfmpegPlayback(options);
        await Check.ThrowsAsync<IOException>(() => playback.PlayFileAsync(new(badFile, "corrupt", DateTime.UnixEpoch, DateTime.UnixEpoch), token),
            "Real corrupt input reports FFprobe diagnostics");
        await using var missing = new FfmpegPlayback(options with
        {
            FfmpegExecutable = "missing-ffmpeg-" + Guid.NewGuid(),
            AudioEnabled = false
        });
        await Check.ThrowsAsync<IOException>(() => missing.PlayFileAsync(files[0], token), "Missing FFmpeg fails without leaking the server");
        await using var missingProbe = new FfmpegPlayback(options with
        {
            FfprobeExecutable = "missing-ffprobe-" + Guid.NewGuid()
        });
        await Check.ThrowsAsync<IOException>(() => missingProbe.PlayFileAsync(files[0], token), "Missing FFprobe reports a dependency failure");
        var audioOnly = Path.Combine(directory, "audio-only.m4a");
        await RunAsync(options.FfmpegExecutable, ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "0.1", "-c:a", "aac", audioOnly], token);
        await Check.ThrowsAsync<IOException>(() => playback.PlayFileAsync(new(audioOnly, "audio-only", DateTime.UnixEpoch, DateTime.UnixEpoch), token),
            "Files containing only audio cannot be mistaken for playable camera video");
        await using var once = new FfmpegPlayback(options with
        {
            AudioEnabled = false
        });
        await new PlaylistPlayer(once).RunAsync([files[1]], options with
        {
            Once = true,
            AudioEnabled = false
        }, token);
        await once.DisconnectAsync(token);
        Check.That((await DescribeAsync(options.StreamUri, token)).Status == 404, "One-pass completion flushes and disconnects its publisher cleanly");
        await using var audioOnce = new FfmpegPlayback(options);
        await new PlaylistPlayer(audioOnce).RunAsync([files[0]], options with
        {
            Once = true
        }, token);
        await audioOnce.DisconnectAsync(token);
        Check.That((await DescribeAsync(options.StreamUri, token)).Status == 404, "One-pass audio/video completion drains both encoder inputs");
    }

    private static async Task<byte[]> RunAsync(string executable, string[] arguments, CancellationToken token)
    {
        await using var process = new ManagedProcess(executable, arguments, "Integration FFmpeg", binaryOutput: true);
        using var output = new MemoryStream();
        await process.StandardOutput.CopyToAsync(output, token);
        await process.EnsureSuccessAsync(token);
        return output.ToArray();
    }

    private static async Task<string> UntilAsync(Uri uri, Func<int, bool> ready, Task player, CancellationToken token)
    {
        while (true)
        {
            if (player.IsCompleted)
            {
                await player;
                throw new InvalidOperationException("Playlist stopped before the expected RTSP state.");
            }

            var response = await DescribeAsync(uri, token);
            if (ready(response.Status))
            {
                return response.Text;
            }

            await Task.Delay(40, token);
        }
    }

    private static async Task<(int Status, string Text)> DescribeAsync(Uri uri, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port, token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"DESCRIBE {uri.AbsoluteUri} RTSP/1.0\r\nCSeq: 1\r\nAccept: application/sdp\r\n\r\n"), token);
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var statusLine = await reader.ReadLineAsync(token) ?? throw new IOException("No RTSP response.");
        var length = 0;
        while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(line[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var body = new char[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await reader.ReadAsync(body.AsMemory(offset), token);
            if (read == 0)
            {
                throw new IOException("Truncated RTSP response.");
            }

            offset += read;
        }

        return (int.Parse(statusLine.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture), new string(body));
    }

    private static async Task ExpectStoppedAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static HashSet<int> ChildIds()
    {
        var ids = new HashSet<int>();
        foreach (var name in new[] { "ffmpeg", "ffprobe", "mediamtx" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    ids.Add(process.Id);
                }
            }
        }

        return ids;
    }
}
