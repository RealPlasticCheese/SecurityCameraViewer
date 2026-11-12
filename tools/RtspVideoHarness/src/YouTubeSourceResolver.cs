using System.Text.Json;

namespace RtspVideoHarness;

public static class YouTubeAddress
{
    public static Uri Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https" ||
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
            !(uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".youtube-nocookie.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Supply a YouTube video or livestream HTTP(S) URL (youtube.com or youtu.be).");
        }

        return uri;
    }
}

public interface IYouTubeMetadataReader
{
    Task<string> ReadAsync(Uri url, HarnessOptions options, CancellationToken cancellationToken);
}

/// <summary>Resolves the page without downloading media, playlists, caches or user-configured side effects.</summary>
public sealed class YtDlpMetadataReader : IYouTubeMetadataReader
{
    public static string[] Arguments(Uri url, HarnessOptions options) =>
    [
        "--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-playlist", "--skip-download", "--dump-single-json",
        "--js-runtimes", "node", "--format", options.YouTubeFormat ?? (options.AudioEnabled ? "bv*+ba/bv*" : "bv*"),
        "--", url.AbsoluteUri
    ];

    public async Task<string> ReadAsync(Uri url, HarnessOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.ProcessTimeout);
        await using var process = new ManagedProcess(options.YtDlpExecutable, Arguments(url, options), "yt-dlp", binaryOutput: true);
        using var reader = new StreamReader(process.StandardOutput);
        try
        {
            var json = await reader.ReadToEndAsync(deadline.Token);
            await process.EnsureSuccessAsync(deadline.Token);
            return json;
        }
        catch (IOException exception) when (exception.Message.Contains("no such option:", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The selected yt-dlp is too old for the harness options. Install the current tool-local release with " +
                "'bash install-ytdlp.sh' (Linux) or 'powershell -File install-ytdlp.ps1' (Windows), then retry without --yt-dlp. " +
                "You can also point --yt-dlp at a current release. " + exception.Message, exception);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("YouTube URL resolution timed out. Increase --timeout-seconds, and check network access and yt-dlp dependencies.");
        }
    }
}

public sealed class YouTubeSourceResolver(IYouTubeMetadataReader? metadataReader = null) : IMediaSourceResolver
{
    private readonly IYouTubeMetadataReader reader = metadataReader ?? new YtDlpMetadataReader();

    public async Task<MediaSource> ResolveAsync(VideoFile file, HarnessOptions options, CancellationToken cancellationToken)
    {
        var url = YouTubeAddress.Parse(file.FullPath);
        Console.WriteLine("Resolving YouTube media with yt-dlp...");
        return Parse(await reader.ReadAsync(url, options, cancellationToken), options.AudioEnabled);
    }

    public static MediaSource Parse(string json, bool audioEnabled)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("entries", out _) ||
                Text(root, "_type") is "playlist" or "multi_video")
            {
                throw new InvalidDataException("Select an individual YouTube video or active livestream URL.");
            }

            var live = root.TryGetProperty("is_live", out var isLive) && isLive.ValueKind == JsonValueKind.True;
            if (Text(root, "live_status") == "is_upcoming")
            {
                throw new InvalidDataException("This livestream has not started yet. Run the harness when it is live.");
            }

            var formats = root.TryGetProperty("requested_formats", out var requested) && requested.ValueKind == JsonValueKind.Array
                ? requested.EnumerateArray().ToArray() : [root];
            var video = formats.FirstOrDefault(format => Codec(format, "vcodec"));
            if (video.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("yt-dlp returned no playable video format. Update yt-dlp or change --youtube-format.");
            }

            var audio = formats.FirstOrDefault(format => Codec(format, "acodec"));
            MediaInput Input(JsonElement format)
            {
                var location = Text(format, "url");
                if (!Uri.TryCreate(location, UriKind.Absolute, out var address) || address.Scheme is not "http" and not "https" || address.UserInfo.Length != 0)
                {
                    throw new InvalidDataException("yt-dlp returned an invalid HTTP(S) media URL.");
                }

                var headers = Headers(root);
                foreach (var header in Headers(format))
                {
                    headers[header.Key] = header.Value;
                }

                return new(address.AbsoluteUri, headers, live && (Text(format, "protocol")?.StartsWith("m3u8", StringComparison.Ordinal) == true));
            }

            return new(Input(video), audioEnabled && audio.ValueKind == JsonValueKind.Object ? Input(audio) : null,
                Text(root, "title") ?? "YouTube", live);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("yt-dlp returned invalid media metadata.", exception);
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Live HLS codecs can be omitted or null when unknown; only "none" explicitly marks an absent track.
    private static bool Codec(JsonElement format, string name) => format.ValueKind == JsonValueKind.Object &&
        (!format.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ||
            (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } codec && codec != "none"));

    private static Dictionary<string, string> Headers(JsonElement format)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (format.TryGetProperty("http_headers", out var headers) && headers.ValueKind != JsonValueKind.Null)
        {
            if (headers.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("yt-dlp returned invalid HTTP headers.");
            }

            foreach (var header in headers.EnumerateObject())
            {
                if (header.Value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("yt-dlp returned a non-text HTTP header.");
                }

                result[header.Name] = header.Value.GetString()!;
            }
        }

        return result;
    }
}
