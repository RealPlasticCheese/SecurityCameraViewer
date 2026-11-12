using System.Text.Json;

namespace RtspVideoHarness.Tests;

internal static class YouTubeTests
{
    public static async Task RunAsync()
    {
        const string page = "https://www.youtube.com/watch?v=sample&list=list&t=3";
        foreach (var url in new[] { page, "https://youtu.be/sample", "https://m.youtube.com/shorts/sample", "https://www.youtube.com/@channel/live",
            "https://www.youtube.com/live/sample", "http://youtube.com/embed/sample", "https://www.youtube-nocookie.com/embed/sample" })
        {
            Check.That(HarnessOptions.Parse([url, "--port", "8555"]).YouTubeUrl?.AbsoluteUri == url,
                "YouTube URL is accepted as the positional input: " + url);
        }
        var explicitOptions = HarnessOptions.Parse(["--youtube", page, "--yt-dlp", "custom yt-dlp", "--youtube-format", "bv*[height<=720]+ba/b", "--no-audio"]);
        Check.That(explicitOptions.YouTubeUrl?.AbsoluteUri == page && explicitOptions.YtDlpExecutable == "custom yt-dlp" &&
            explicitOptions.YouTubeFormat == "bv*[height<=720]+ba/b" && !explicitOptions.AudioEnabled, "Explicit YouTube, extractor and format options parse together");
        Check.That(HarnessOptions.Parse([]).YouTubeUrl == null && HarnessOptions.Parse(["--folder", "samples"]).YouTubeUrl == null,
            "Folder defaults and explicit folders retain their original behavior");
        Check.That(HarnessOptions.Parse(["--", "https://youtu.be/sample"]).YouTubeUrl != null, "Option terminator also accepts a YouTube input");
        foreach (var args in new string[][]
        {
            ["--youtube"], ["--yt-dlp"], ["--youtube-format"], ["--youtube-format", ""], ["--youtube", "not a url"],
            ["https://example.com/video"], ["https://youtube.com.evil.example/watch?v=x"], ["https://notyoutube.com/watch?v=x"],
            ["ftp://youtube.com/watch?v=x"], ["https://user:pass@youtube.com/watch?v=x"], ["https://youtube.com:9999/watch?v=x"],
            ["folder", page], [page, "folder"], ["--folder", "folder", "--youtube", page], ["--youtube", page, "--youtube", page]
        })
        {
            await Check.ThrowsAsync<ArgumentException>(() => HarnessOptions.Parse(args), "Invalid or conflicting YouTube input is rejected: " + string.Join(' ', args));
        }

        var mergedJson = JsonSerializer.Serialize(new
        {
            title = "A video",
            url = "https://media.example/video?token=a&b=c",
            vcodec = "h264",
            acodec = "aac",
            http_headers = new Dictionary<string, string> { ["User-Agent"] = "test-agent", ["Referer"] = page }
        });
        var merged = YouTubeSourceResolver.Parse(mergedJson, true);
        Check.That(merged.Video.Location == merged.Audio?.Location && !merged.IsLive && merged.Name == "A video", "Muxed video/audio metadata uses the same media input");
        var splitJson = JsonSerializer.Serialize(new
        {
            title = "Live camera",
            is_live = true,
            http_headers = new Dictionary<string, string> { ["User-Agent"] = "root-agent", ["Referer"] = page },
            requested_formats = new[]
            {
                new { url = "https://media.example/video.m3u8", vcodec = "h264", acodec = "none", protocol = "m3u8_native", http_headers = new Dictionary<string, string> { ["user-agent"] = "video-agent" } },
                new { url = "https://media.example/audio.m3u8", vcodec = "none", acodec = "aac", protocol = "m3u8", http_headers = new Dictionary<string, string>() }
            }
        });
        var split = YouTubeSourceResolver.Parse(splitJson, true);
        Check.That(split.IsLive && split.Video.LiveHls && split.Audio!.LiveHls && split.Video.Location != split.Audio.Location,
            "Active live metadata retains separate video/audio HLS URLs");
        Check.That(split.Video.Headers!["User-Agent"] == "video-agent" && split.Video.Headers["Referer"] == page && split.Audio!.Headers!["User-Agent"] == "root-agent",
            "Per-format HTTP headers override inherited headers case-insensitively");
        var unknownAudio = YouTubeSourceResolver.Parse(splitJson.Replace("\"acodec\":\"aac\"", "\"acodec\":null", StringComparison.Ordinal), true);
        Check.That(unknownAudio.Audio?.Location == split.Audio!.Location, "Unknown live audio codec retains the audio track instead of substituting silence");
        var omittedAudioJson = splitJson.Replace("\"acodec\":\"aac\",", "", StringComparison.Ordinal);
        var omittedAudio = YouTubeSourceResolver.Parse(omittedAudioJson, true);
        Check.That(omittedAudio.Audio?.Location == split.Audio!.Location && omittedAudio.Audio.LiveHls && omittedAudio.Video.Location == split.Video.Location,
            "Omitted live audio codec retains the separate HLS audio track, as returned for Venice Beach");
        Check.That(omittedAudio.Audio!.Headers!["User-Agent"] == "root-agent" && omittedAudio.Audio.Headers["Referer"] == page,
            "Live audio with an omitted codec retains its required HTTP headers");
        Check.That(YouTubeSourceResolver.Parse(omittedAudioJson, false).Audio == null,
            "Video-only mode suppresses live audio even when the codec is omitted");
        var omittedMuxedAudio = YouTubeSourceResolver.Parse(mergedJson.Replace("\"acodec\":\"aac\",", "", StringComparison.Ordinal), true);
        Check.That(omittedMuxedAudio.Audio?.Location == merged.Video.Location,
            "An omitted audio codec on a muxed input is handed to FFmpeg for decoding");
        var unknownVideo = YouTubeSourceResolver.Parse(mergedJson.Replace("\"vcodec\":\"h264\"", "\"vcodec\":null", StringComparison.Ordinal), true);
        Check.That(unknownVideo.Video.Location == merged.Video.Location, "Unknown video codec is handed to FFmpeg for decoding");
        var omittedVideo = YouTubeSourceResolver.Parse(mergedJson.Replace("\"vcodec\":\"h264\",", "", StringComparison.Ordinal), true);
        Check.That(omittedVideo.Video.Location == merged.Video.Location && omittedVideo.Audio?.Location == merged.Audio!.Location,
            "An omitted video codec is treated consistently with an unknown video codec");
        var omittedBoth = YouTubeSourceResolver.Parse("""{"url":"https://media.example/video"}""", true);
        Check.That(omittedBoth.Video.Location == omittedBoth.Audio?.Location,
            "Both omitted codecs leave track detection to FFmpeg rather than inventing a silent input");
        var repeatedHeaders = YouTubeSourceResolver.Parse("""{"url":"https://media.example/video","vcodec":"h264","http_headers":{"User-Agent":"one","user-agent":"two"}}""", false);
        Check.That(repeatedHeaders.Video.Headers!["User-Agent"] == "two", "HTTP header names are merged case-insensitively within one metadata object");
        Check.That(YouTubeSourceResolver.Parse(splitJson, false).Audio == null && YouTubeSourceResolver.Parse(mergedJson, false).Audio == null,
            "Video-only mode suppresses both separate and muxed source audio");
        var silent = YouTubeSourceResolver.Parse("""{"url":"https://media.example/video","vcodec":"h264","acodec":"none"}""", true);
        Check.That(silent.Audio == null && silent.Name == "YouTube", "A video with no audio can use the existing silence generator");
        var omittedVideoOnly = YouTubeSourceResolver.Parse("""{"url":"https://media.example/video","acodec":"none"}""", true);
        Check.That(omittedVideoOnly.Audio == null, "An explicit absent audio track still produces silence when the video codec is omitted");
        foreach (var json in new[]
        {
            "", "bad json", "null", "[]", "{}", """{"entries":[]}""", """{"_type":"playlist"}""",
            """{"live_status":"is_upcoming"}""", """{"url":"https://media.example/audio","vcodec":"none","acodec":"aac"}""",
            """{"url":"https://media.example/audio.m3u8","vcodec":"none","protocol":"m3u8_native"}""",
            """{"url":"https://media.example/video","vcodec":"none","acodec":"none"}""",
            """{"url":"https://media.example/video","vcodec":""}""",
            """{"url":"https://media.example/video","vcodec":42}""",
            """{"url":"file:///secret","vcodec":"none"}""",
            """{"url":"file:///secret","vcodec":"h264"}""", """{"url":"https://user:pass@media.example/video","vcodec":"h264"}""",
            """{"url":"https://media.example/video","vcodec":"h264","http_headers":[]}""",
            """{"url":"https://media.example/video","vcodec":"h264","http_headers":{"User-Agent":1}}""",
            """{"requested_formats":[null,{"url":"bad","vcodec":"h264"}]}"""
        })
        {
            await Check.ThrowsAsync<InvalidDataException>(() => YouTubeSourceResolver.Parse(json, true), "Malformed or unavailable YouTube metadata fails explicitly");
        }
        var inputArguments = FfmpegCommands.InputArguments(split.Video);
        Check.That(inputArguments.Contains("User-Agent: video-agent\r\nReferer: " + page + "\r\n") &&
            Array.IndexOf(inputArguments, "-headers") < Array.IndexOf(inputArguments, "-i") && inputArguments.Contains("-live_start_index") && inputArguments.Last() == split.Video.Location,
            "FFmpeg receives literal HTTP headers and starts HLS at the current live edge");
        Check.That(!FfmpegCommands.InputArguments(merged.Video).Contains("-live_start_index"), "Recorded videos do not receive live-only demuxer options");
        foreach (var header in new[] { new KeyValuePair<string, string>("Bad:Name", "value"), new("User-Agent", "value\r\nInjected: yes"), new("", "value") })
        {
            await Check.ThrowsAsync<InvalidDataException>(() => FfmpegCommands.InputArguments(new("https://media.example/video", new Dictionary<string, string> { [header.Key] = header.Value })),
                "Invalid HTTP header names and line injection cannot enter FFmpeg arguments");
        }
        var arguments = YtDlpMetadataReader.Arguments(new(page), new());
        Check.That(arguments.Contains("--skip-download") && arguments.Contains("--ignore-config") && arguments.Contains("--no-playlist") &&
            arguments.Contains("--no-cache-dir") && arguments.Contains("--no-plugin-dirs") && arguments[^2] == "--" && arguments[^1] == page && !arguments.Contains("--live-from-start"),
            "Extractor resolves current streams without downloading videos or inheriting side effects");
        Check.That(YtDlpMetadataReader.Arguments(new(page), explicitOptions).Contains(explicitOptions.YouTubeFormat!), "User format selection overrides the default");
        Check.That(YtDlpMetadataReader.Arguments(new(page), new HarnessOptions { AudioEnabled = false }).Contains("bv*"), "Video-only extraction does not require an audio format");

        var reader = new MetadataReader(mergedJson);
        var resolver = new YouTubeSourceResolver(reader);
        var file = new VideoFile(page, "YouTube", DateTime.UnixEpoch, DateTime.UnixEpoch);
        await resolver.ResolveAsync(file, new(), CancellationToken.None);
        await resolver.ResolveAsync(file, new(), CancellationToken.None);
        Check.That(reader.Calls == 2 && reader.Url?.AbsoluteUri == page, "Media URLs are resolved again on replay so signed URLs can be refreshed");
        await ProcessReaderAsync(page, mergedJson);
    }

    private static async Task ProcessReaderAsync(string page, string json)
    {
        var oldMode = Environment.GetEnvironmentVariable("RTSP_YTDLP_TEST_MODE");
        var oldJson = Environment.GetEnvironmentVariable("RTSP_YTDLP_TEST_JSON");
        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "RtspVideoHarness.Tests.exe" : "RtspVideoHarness.Tests");
        var options = new HarnessOptions { YtDlpExecutable = executable, ProcessTimeout = TimeSpan.FromSeconds(2) };
        var reader = new YtDlpMetadataReader();
        try
        {
            Environment.SetEnvironmentVariable("RTSP_YTDLP_TEST_MODE", "success");
            Environment.SetEnvironmentVariable("RTSP_YTDLP_TEST_JSON", json);
            Check.That(await reader.ReadAsync(new(page), options, CancellationToken.None) == json + Environment.NewLine,
                "Extractor subprocess stdout is read as metadata without logging or downloading it");
            Environment.SetEnvironmentVariable("RTSP_YTDLP_TEST_MODE", "failure");
            await Check.ThrowsAsync<IOException>(() => reader.ReadAsync(new(page), options, CancellationToken.None), "Extractor failures retain stderr diagnostics");
            Environment.SetEnvironmentVariable("RTSP_YTDLP_TEST_MODE", "unsupported");
            try
            {
                await reader.ReadAsync(new(page), options, CancellationToken.None);
                throw new InvalidOperationException("An unsupported extractor option must fail.");
            }
            catch (IOException exception)
            {
                Check.That(exception.Message.Contains("install-ytdlp.sh", StringComparison.Ordinal) &&
                    exception.Message.Contains("install-ytdlp.ps1", StringComparison.Ordinal) &&
                    exception.Message.Contains("--no-plugin-dirs", StringComparison.Ordinal) && exception.InnerException is IOException,
                    "An explicit outdated extractor gets actionable update guidance and retains the original diagnostics");
            }
            Environment.SetEnvironmentVariable("RTSP_YTDLP_TEST_MODE", "hang");
            await Check.ThrowsAsync<TimeoutException>(() => reader.ReadAsync(new(page), options with { ProcessTimeout = TimeSpan.FromMilliseconds(300) }, CancellationToken.None),
                "Hung extraction respects the configurable deadline and kills the child");
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Check.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(new(page), options, stop.Token), "Extraction cancellation stops the child process");
            await Check.ThrowsAsync<IOException>(() => reader.ReadAsync(new(page), options with { YtDlpExecutable = "missing-yt-dlp-" + Guid.NewGuid() }, CancellationToken.None),
                "Missing yt-dlp reports its executable dependency");
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTSP_YTDLP_TEST_MODE", oldMode);
            Environment.SetEnvironmentVariable("RTSP_YTDLP_TEST_JSON", oldJson);
        }
    }

    internal sealed class MetadataReader(string json) : IYouTubeMetadataReader
    {
        public int Calls
        {
            get; private set;
        }
        public Uri? Url
        {
            get; private set;
        }
        public Task<string> ReadAsync(Uri url, HarnessOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Url = url;
            return Task.FromResult(json);
        }
    }
}
