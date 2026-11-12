using System.Net.Sockets;
using System.Text;

namespace SecurityCameraViewer.Core;

internal sealed record RtspResponse(int Status, Dictionary<string, string> Headers, string Body);

/// <summary>Bounded authenticated RTSP control and interleaved writes to one configured authority.</summary>
internal sealed class RtspControlConnection(TcpClient client, Uri address, string username, string password) : IDisposable
{
    private readonly NetworkStream _stream = client.GetStream();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private string? _challenge;
    private uint _nonceCount;
    private int _sequence;

    public static async Task<RtspControlConnection> ConnectAsync(Uri address, string username, string password, CancellationToken token)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(address.Host, address.Port, token);
            client.NoDelay = true;
            return new(client, address, username, password);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<RtspResponse> RequestAsync(string method, Uri target, string? session, string? transport, CancellationToken token)
    {
        EnsureAuthority(address, target);
        await _requests.WaitAsync(token);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var sequence = ++_sequence;
                var request = new StringBuilder($"{method} {target.AbsoluteUri} RTSP/1.0\r\nCSeq: {sequence}\r\nRequire: www.onvif.org/ver20/backchannel\r\n");
                if (method == "DESCRIBE")
                {
                    request.Append("Accept: application/sdp\r\n");
                }
                if (method == "GET_PARAMETER")
                {
                    request.Append("Content-Length: 0\r\n");
                }
                if (session is not null)
                {
                    request.Append("Session: ").Append(session).Append("\r\n");
                }
                if (transport is not null)
                {
                    request.Append("Transport: ").Append(transport).Append("\r\n");
                }
                if (_challenge is not null)
                {
                    var authorization = RtspProbe.BuildAuthorization(_challenge, method, target.AbsoluteUri, username, password, ++_nonceCount)
                        ?? throw new IOException("Unsupported RTSP authentication.");
                    request.Append("Authorization: ").Append(authorization).Append("\r\n");
                }
                // Omit User-Agent for compatibility with HomeBase and other older RTSP servers.
                request.Append("\r\n");
                await WriteAsync(Encoding.UTF8.GetBytes(request.ToString()), token);
                var response = await ReadAsync(token);
                if (!response.Headers.TryGetValue("CSeq", out var value) || value != sequence.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    throw new InvalidDataException("Unexpected RTSP response sequence.");
                }
                if (response.Status != 401 || attempt == 1 || !response.Headers.TryGetValue("WWW-Authenticate", out _challenge))
                {
                    return response;
                }
                _nonceCount = 0;
            }
            throw new IOException("RTSP authentication failed.");
        }
        finally
        {
            _requests.Release();
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        await _writes.WaitAsync(token);
        try
        {
            await _stream.WriteAsync(bytes, token);
        }
        finally
        {
            _writes.Release();
        }
    }

    private async Task<RtspResponse> ReadAsync(CancellationToken token)
    {
        var one = new byte[1];
        var interleavedBytes = 0;
        // Cameras can return RTCP between control replies. Drain bounded packets without retaining them.
        while (true)
        {
            await _stream.ReadExactlyAsync(one, token);
            if (one[0] != '$')
            {
                break;
            }
            var framing = new byte[3];
            await _stream.ReadExactlyAsync(framing, token);
            var length = framing[1] << 8 | framing[2];
            interleavedBytes += length + 4;
            if (interleavedBytes > 1024 * 1024)
            {
                throw new InvalidDataException("Excessive RTSP interleaved data.");
            }
            var buffer = new byte[Math.Min(1024, length)];
            while (length > 0)
            {
                var count = Math.Min(length, buffer.Length);
                await _stream.ReadExactlyAsync(buffer.AsMemory(0, count), token);
                length -= count;
            }
        }
        var header = new StringBuilder();
        while (header.Length < 32768)
        {
            if (one[0] != '\r' && (header.Length > 0 || one[0] != '\n'))
            {
                header.Append((char)one[0]);
            }
            if (header.Length >= 2 && header[^1] == '\n' && header[^2] == '\n')
            {
                break;
            }
            await _stream.ReadExactlyAsync(one, token);
        }
        if (header.Length >= 32768)
        {
            throw new InvalidDataException("Oversized RTSP headers.");
        }
        var lines = header.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var statusParts = lines[0].Split(' ');
        if (statusParts.Length < 2 || statusParts[0] != "RTSP/1.0" || !int.TryParse(statusParts[1], out var status))
        {
            throw new InvalidDataException("Invalid RTSP response.");
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
        var lengthText = headers.GetValueOrDefault("Content-Length", "0");
        if (!int.TryParse(lengthText, out var bodyLength) || bodyLength is < 0 or > 262144)
        {
            throw new InvalidDataException("Invalid RTSP content length.");
        }
        var body = new byte[bodyLength];
        await _stream.ReadExactlyAsync(body, token);
        return new(status, headers, Encoding.UTF8.GetString(body));
    }

    internal static void EnsureAuthority(Uri address, Uri target)
    {
        if (target.Scheme != "rtsp" || !string.Equals(address.Host, target.Host, StringComparison.OrdinalIgnoreCase) ||
            address.Port != target.Port || target.UserInfo.Length > 0 || target.Fragment.Length > 0 ||
            target.AbsoluteUri.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("Backchannel address must remain on the configured RTSP authority.");
        }
    }

    public void Dispose() => client.Dispose();
}
