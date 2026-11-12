using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace RtspVideoHarness.Tests;

internal static class YouTubeIntegrationTests
{
    public static async Task RunAsync(HarnessOptions options, string directory, IReadOnlyList<VideoFile> files, CancellationToken token)
    {
        var audio = Path.Combine(directory, "youtube-audio.m4a");
        await RunAsync(options.FfmpegExecutable, ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=900:sample_rate=48000", "-t", "1.6", "-c:a", "aac", audio], token);
        var playlist = Path.Combine(directory, "generated-live.m3u8");
        await RunAsync(options.FfmpegExecutable, ["-v", "error", "-f", "lavfi", "-i", "color=c=green:s=64x48:r=10",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "10", "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-g", "10", "-sc_threshold", "0", "-c:a", "aac", "-f", "hls", "-hls_time", "1", "-hls_list_size", "0",
            "-hls_flags", "omit_endlist", "-hls_segment_filename", Path.Combine(directory, "live%02d.ts"), playlist], token);
        var segments = Directory.GetFiles(directory, "live*.ts").Order(StringComparer.Ordinal).ToArray();
        var media = segments.ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        var routes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["video.mp4"] = await File.ReadAllBytesAsync(files[0].FullPath, token),
            ["audio.m4a"] = await File.ReadAllBytesAsync(audio, token)
        };
        foreach (var segment in media)
        {
            routes.Add(segment.Key, segment.Value);
        }
        await using var http = new MediaHttpFixture(routes, segments.Select(path => Path.GetFileName(path)!).ToArray());
        var headers = new Dictionary<string, string> { ["User-Agent"] = "RtspHarness-YouTube-Test" };
        var split = JsonSerializer.Serialize(new
        {
            title = "Remote split video/audio",
            http_headers = headers,
            requested_formats = new[]
            {
                new { url = new Uri(http.BaseUri, "video.mp4?token=one&value=two").AbsoluteUri, vcodec = "h264", acodec = "none" },
                new { url = new Uri(http.BaseUri, "audio.m4a").AbsoluteUri, vcodec = "none", acodec = "aac" }
            }
        });
        await RoundTripAsync(options, split, false, token);
        Check.That(http.Requests >= 2, "Remote video/audio decoders retain yt-dlp HTTP headers on separate inputs");
        var live = JsonSerializer.Serialize(new
        {
            title = "Active live HLS",
            is_live = true,
            protocol = "m3u8_native",
            url = new Uri(http.BaseUri, "live.m3u8").AbsoluteUri,
            vcodec = "h264",
            acodec = "aac",
            http_headers = headers
        });
        await RoundTripAsync(options, live, true, token);
        await using var omittedCodecHttp = new MediaHttpFixture(routes, segments.Select(path => Path.GetFileName(path)!).ToArray());
        var omittedCodecLive = JsonSerializer.Serialize(new
        {
            title = "Live HLS with unknown audio codec",
            is_live = true,
            http_headers = headers,
            requested_formats = new object[]
            {
                new { url = new Uri(omittedCodecHttp.BaseUri, "live.m3u8").AbsoluteUri, vcodec = "h264", acodec = "none", protocol = "m3u8_native" },
                new { url = new Uri(omittedCodecHttp.BaseUri, "live.m3u8").AbsoluteUri, vcodec = "none", protocol = "m3u8_native", resolution = "audio only" }
            }
        });
        await RoundTripAsync(options, omittedCodecLive, true, token);
    }

    private static async Task RoundTripAsync(HarnessOptions options, string json, bool live, CancellationToken token)
    {
        using var lease = new TcpListener(IPAddress.Loopback, 0);
        lease.Start();
        options = options with
        {
            Port = ((IPEndPoint)lease.LocalEndpoint).Port,
            BlankPeriod = TimeSpan.Zero
        };
        lease.Stop();
        await using var server = await MediaMtxServer.StartAsync(options, token);
        var metadata = new YouTubeTests.MetadataReader(json);
        await using var playback = new FfmpegPlayback(options, new YouTubeSourceResolver(metadata));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var file = new VideoFile("https://www.youtube.com/watch?v=fixture", "YouTube fixture", DateTime.UnixEpoch, DateTime.UnixEpoch);
        var playing = new PlaylistPlayer(playback).RunAsync([file], options, stop.Token);
        try
        {
            while (await DescribeStatusAsync(options.StreamUri, token) != 200)
            {
                if (playing.IsCompleted)
                {
                    await playing;
                    throw new IOException("Remote playback stopped before RTSP became ready.");
                }
                await Task.Delay(40, token);
            }
            var video = RunAsync(options.FfmpegExecutable, ["-v", "error", "-rtsp_transport", "tcp", "-i", options.StreamUri.AbsoluteUri,
                "-map", "0:v:0", "-an", "-frames:v", "3", "-vf", "scale=1:1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"], token);
            var audio = RunAsync(options.FfmpegExecutable, ["-v", "error", "-rtsp_transport", "tcp", "-i", options.StreamUri.AbsoluteUri,
                "-map", "0:a:0", "-vn", "-frames:a", "3", "-f", "s16le", "pipe:1"], token);
            await Task.WhenAll(video, audio);
            Check.That(video.Result.Length == 9 && audio.Result.Any(value => value != 0), live
                ? "An unfinished, advancing livestream produces real RTSP video and audio before it ends"
                : "Split HTTP video/audio media resolved from YouTube metadata produces real RTSP playback");
            Check.That(!playing.IsCompleted && (live ? metadata.Calls == 1 : metadata.Calls >= 1), live
                ? "Live playback stays active rather than downloading to EOF or prematurely replaying"
                : "Remote playback uses the same persistent RTSP publisher while looping");
        }
        finally
        {
            stop.Cancel();
            try
            {
                await playing;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task<byte[]> RunAsync(string executable, string[] arguments, CancellationToken token)
    {
        await using var process = new ManagedProcess(executable, arguments, "YouTube integration FFmpeg", binaryOutput: true);
        using var output = new MemoryStream();
        await process.StandardOutput.CopyToAsync(output, token);
        await process.EnsureSuccessAsync(token);
        return output.ToArray();
    }

    private static async Task<int> DescribeStatusAsync(Uri uri, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port, token);
        var stream = client.GetStream();
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"DESCRIBE {uri.AbsoluteUri} RTSP/1.0\r\nCSeq: 1\r\nAccept: application/sdp\r\n\r\n"), token);
        using var reader = new StreamReader(stream);
        var line = await reader.ReadLineAsync(token) ?? throw new IOException("No RTSP response.");
        return int.Parse(line.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
    }
}
