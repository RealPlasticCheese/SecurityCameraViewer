using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SecurityCameraViewer.Core;

public sealed record CameraDiagnostic(string CameraId, int Channel, string Name, DateTimeOffset TestedAt,
    string Stream, string Transport, ProbeResult Rtsp, bool VideoDecoded, string PlaybackMessage, StreamMediaInfo? Media = null);

public static partial class CameraDiagnostics
{
    public static async Task<CameraDiagnostic> TestAsync(ViewerSettings settings, string password, int channel,
        string ffmpeg = "ffmpeg", CancellationToken cancellationToken = default)
    {
        var result = await RtspProbe.DescribeAsync(settings, password, channel, cancellationToken);
        var decoded = false;
        var message = "Video decode was not attempted because the RTSP probe failed.";
        if (result.Success)
        {
            await using var session = new FfmpegSession(settings, password, channel, ffmpeg);
            var clock = Stopwatch.StartNew();
            while (!session.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (session.ConsumeFrame(_ => { }, out _))
                {
                    decoded = true;
                    break;
                }

                await Task.Delay(50, cancellationToken);
            }

            message = decoded ? "A video frame was decoded successfully." : session.IsCompleted ? session.Failure : "RTSP advertised video, but no frame arrived within 8 seconds.";
        }

        var media = decoded ? await MediaInspector.InspectAsync(settings, password, channel, ffmpeg, cancellationToken) : null;
        var source = settings.Sources.FirstOrDefault(c => c.Channel == channel);
        return new(settings.CameraId(channel), channel,
            settings.CameraName(channel), DateTimeOffset.UtcNow,
            settings.Substream ? source is not null && string.IsNullOrEmpty(source.SubStream) ? "main (no substream configured)" : "substream" : "main",
            "RTSP over TCP", result, decoded, message, media);
    }

    /// <summary>Exports reports with secrets, user info and every URL query value redacted by default.</summary>
    public static string Export(object report, bool includePasswords = false)
    {
        var node = JsonSerializer.SerializeToNode(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Redact(node, includePasswords);
        return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
    }

    private static void Redact(JsonNode? node, bool includePasswords = false)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if ((!includePasswords && property.Key.Contains("password", StringComparison.OrdinalIgnoreCase)) ||
                    property.Key.Equals("credentials", StringComparison.OrdinalIgnoreCase) ||
                    property.Key.Equals("username", StringComparison.OrdinalIgnoreCase))
                {
                    obj.Remove(property.Key);
                }
                else if (property.Value is JsonValue value && value.TryGetValue<string>(out var text) &&
                    !property.Key.Contains("password", StringComparison.OrdinalIgnoreCase))
                {
                    obj[property.Key] = RedactUrls(text);
                }
                else
                {
                    Redact(property.Value, includePasswords);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    array[i] = RedactUrls(text);
                }
                else
                {
                    Redact(array[i], includePasswords);
                }
            }
        }
    }

    private static string RedactUrls(string value)
    {
        return UrlPattern().Replace(value, match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri))
            {
                return "[invalid URL]";
            }

            var builder = new UriBuilder(uri) { UserName = "", Password = "", Fragment = "" };
            if (uri.Query.Length > 0)
            {
                builder.Query = string.Join("&", uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=')[0] + "=REDACTED"));
            }

            return builder.Uri.AbsoluteUri;
        });
    }

    [GeneratedRegex("(?:rtsp|https?)://[^\\s\"<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
