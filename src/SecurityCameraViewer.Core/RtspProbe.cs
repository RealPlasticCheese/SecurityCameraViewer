using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SecurityCameraViewer.Core;

public sealed record ProbeResult(bool Success, int StatusCode, string Message, string? Codec = null)
{
    public bool AudioVerificationRequired { get; init; }
    public bool? HasAudio
    {
        get; init;
    }
    public string? AudioCodec
    {
        get; init;
    }
}

/// <summary>Read-only DESCRIBE probe. Handles the extra CR characters sent by older recorders.</summary>
public static partial class RtspProbe
{
    public static async Task<ProbeResult> DescribeAsync(ViewerSettings settings, string password,
        int channel = 1, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        try
        {
            using var client = new TcpClient();
            var address = settings.StreamUri(channel);
            await client.ConnectAsync(address.Host, address.Port, timeout.Token);
            using var stream = client.GetStream();
            var uri = settings.StreamUri(channel).AbsoluteUri;
            await SendAsync(stream, uri, 1, null, timeout.Token);
            var response = await ReadAsync(stream, timeout.Token);
            if (response.Status == 401 && response.Headers.TryGetValue("WWW-Authenticate", out var challenge))
            {
                var authorization = BuildAuthorization(challenge, "DESCRIBE", uri, settings.CameraUsername(channel), settings.CameraPassword(channel, password));
                if (authorization is null)
                {
                    return new(false, 401, "The recorder requested an unsupported authentication method.");
                }

                await SendAsync(stream, uri, 2, authorization, timeout.Token);
                response = await ReadAsync(stream, timeout.Token);
            }
            if (response.Status == 200)
            {
                var codec = CodecPattern().Match(response.Body);
                var hasVideo = response.Body.Contains("m=video", StringComparison.OrdinalIgnoreCase);
                var audio = RtspAudioMetadata.Read(response.Body);
                var candidate = audio.HasAudio ? audio : RtspAudioMetadata.Read(response.Body, includeReceiveOnly: true);
                return hasVideo
                    ? new(true, 200, "Video stream found. Ready to connect.", codec.Success ? codec.Groups[1].Value : null)
                    {
                        HasAudio = !audio.HasAudio && candidate.HasAudio ? null : audio.HasAudio,
                        AudioVerificationRequired = !audio.HasAudio && candidate.HasAudio,
                        AudioCodec = candidate.Codec
                    }
                    : new(false, 200, "The recorder accepted the request but did not advertise a video track.");
            }
            return response.Status switch
            {
                401 => new(false, 401, "RTSP login rejected. Check the username/password and the recorder's separate RTSP permissions. Phone access on port 9000 can still work."),
                403 => new(false, 403, "This account does not have permission to view the stream."),
                404 => new(false, 404, "Stream path not found. Try a different path template or camera number."),
                _ => new(false, response.Status, $"The recorder returned RTSP status {response.Status}.")
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, 0, "The recorder timed out. Check its address, RTSP port and network connection.");
        }
        catch (SocketException)
        {
            return new(false, 0, "Cannot reach the RTSP service. Check the address and port (usually 554, not 9000).");
        }
        catch (IOException)
        {
            return new(false, 0, "The recorder closed the connection before completing the RTSP response.");
        }
        catch (InvalidDataException)
        {
            return new(false, 0, "The recorder returned an invalid RTSP response.");
        }
    }

    internal static string? BuildAuthorization(string challenge, string method, string uri, string user, string password, uint nonceCount = 1)
    {
        if (challenge.StartsWith("Basic", StringComparison.OrdinalIgnoreCase))
        {
            return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
        }

        if (!challenge.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match field in AuthFieldPattern().Matches(challenge))
        {
            if (!fields.TryAdd(field.Groups[1].Value, field.Groups[2].Success ? field.Groups[2].Value : field.Groups[3].Value))
            {
                throw new InvalidDataException("Duplicate RTSP authentication parameter.");
            }
        }
        if (!fields.TryGetValue("realm", out var realm) || !fields.TryGetValue("nonce", out var nonce))
        {
            return null;
        }

        var algorithm = fields.GetValueOrDefault("algorithm", "MD5");
        var session = algorithm.EndsWith("-sess", StringComparison.OrdinalIgnoreCase);
        var hashAlgorithm = session ? algorithm[..^5] : algorithm;
        if (!hashAlgorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase) &&
            !hashAlgorithm.Equals("SHA-256", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var cnonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        string Digest(string value) => hashAlgorithm.Equals("SHA-256", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value))) : Hash(value);
        var ha1 = Digest($"{user}:{realm}:{password}");
        if (session)
        {
            ha1 = Digest($"{ha1}:{nonce}:{cnonce}");
        }

        var ha2 = Digest($"{method}:{uri}");
        var qop = fields.GetValueOrDefault("qop");
        if (qop is not null && !qop.Split(',').Any(x => x.Trim().Equals("auth", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var nc = nonceCount.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        var digest = qop is null ? Digest($"{ha1}:{nonce}:{ha2}") : Digest($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}");
        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var result = $"Digest username={Quote(user)}, realm={Quote(realm)}, nonce={Quote(nonce)}, uri={Quote(uri)}, response={Quote(digest)}, algorithm={algorithm}";
        if (qop is not null)
        {
            result += $", qop=auth, nc={nc}, cnonce={Quote(cnonce)}";
        }
        else if (session)
        {
            result += $", cnonce={Quote(cnonce)}";
        }

        if (fields.TryGetValue("opaque", out var opaque))
        {
            result += $", opaque={Quote(opaque)}";
        }

        return result;
    }

    private static string Hash(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Task SendAsync(NetworkStream stream, string uri, int cseq, string? authorization, CancellationToken ct)
    {
        // HomeBase 2 rejects valid Digest credentials with our User-Agent header.
        // The header is optional; omit it for compatibility with these RTSP servers.
        var request = $"DESCRIBE {uri} RTSP/1.0\r\nCSeq: {cseq}\r\nAccept: application/sdp\r\n";
        if (authorization is not null)
        {
            request += $"Authorization: {authorization}\r\n";
        }

        return stream.WriteAsync(Encoding.UTF8.GetBytes(request + "\r\n"), ct).AsTask();
    }

    private sealed record Response(int Status, Dictionary<string, string> Headers, string Body);

    private static async Task<Response> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        // Read headers one byte at a time so fragmented replies and nonstandard CRCRLF never lose SDP bytes.
        var header = new StringBuilder();
        var one = new byte[1];
        while (header.Length < 32768)
        {
            if (await stream.ReadAsync(one, ct) == 0)
            {
                throw new IOException("Connection closed");
            }

            if (one[0] == '\r')
            {
                continue;
            }

            if (header.Length == 0 && one[0] == '\n')
            {
                continue;
            }

            header.Append((char)one[0]);
            if (header.Length >= 2 && header[^1] == '\n' && header[^2] == '\n')
            {
                break;
            }
        }
        if (header.Length >= 32768)
        {
            throw new InvalidDataException();
        }

        var lines = header.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var statusParts = lines[0].Split(' ');
        if (statusParts.Length < 2 || !statusParts[0].StartsWith("RTSP/") || !int.TryParse(statusParts[1], out var status))
        {
            throw new InvalidDataException();
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }
        var length = 0;
        if (headers.TryGetValue("Content-Length", out var lengthText) &&
            (!int.TryParse(lengthText, out length) || length is < 0 or > 262144))
        {
            throw new InvalidDataException();
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct);
        return new(status, headers, Encoding.UTF8.GetString(bytes));
    }

    [GeneratedRegex("([a-zA-Z0-9_-]+)\\s*=\\s*(?:\"([^\"]*)\"|([^,\\s]+))")]
    private static partial Regex AuthFieldPattern();
    [GeneratedRegex(@"a=rtpmap:\d+\s+(H264|H265|HEVC|JPEG|MP4V-ES)/", RegexOptions.IgnoreCase)]
    private static partial Regex CodecPattern();
}
