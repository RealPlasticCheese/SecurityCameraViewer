using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RtspVideoHarness.Tests;

/// <summary>Serves media and an advancing, unfinished live playlist using real HTTP requests.</summary>
internal sealed class MediaHttpFixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, byte[]> files;
    private readonly string[] liveSegments;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Task serving;
    private readonly List<Task> connections = [];
    private int requests;

    public MediaHttpFixture(Dictionary<string, byte[]> files, string[]? liveSegments = null)
    {
        this.files = files;
        this.liveSegments = liveSegments ?? [];
        listener.Start();
        BaseUri = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        serving = ServeAsync();
    }

    public Uri BaseUri
    {
        get;
    }
    public int Requests => Volatile.Read(ref requests);

    private async Task ServeAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                connections.Add(RespondAsync(client));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private async Task RespondAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync(stop.Token);
                if (request == null)
                {
                    return;
                }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        headers[line[..colon]] = line[(colon + 1)..].Trim();
                    }
                }

                Interlocked.Increment(ref requests);
                var path = new Uri(BaseUri, request.Split(' ')[1]).AbsolutePath.TrimStart('/');
                byte[]? data;
                var contentType = "application/octet-stream";
                if (path == "live.m3u8" && liveSegments.Length > 0)
                {
                    var available = Math.Min(liveSegments.Length, 1 + (int)clock.Elapsed.TotalSeconds);
                    var playlist = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n");
                    foreach (var segment in liveSegments.Take(available))
                    {
                        playlist.Append("#EXTINF:1.0,\n").Append(segment).Append('\n');
                    }
                    // Deliberately no ENDLIST: readers must play while this stream is still live.
                    data = Encoding.UTF8.GetBytes(playlist.ToString());
                    contentType = "application/vnd.apple.mpegurl";
                }
                else
                {
                    files.TryGetValue(path, out data);
                }
                var status = 200;
                if (headers.GetValueOrDefault("User-Agent") != "RtspHarness-YouTube-Test")
                {
                    status = 403;
                    data = [];
                }
                else if (data == null)
                {
                    status = 404;
                    data = [];
                }
                var start = 0;
                var end = data.Length - 1;
                var rangeHeader = "";
                if (status == 200 && headers.TryGetValue("Range", out var range) && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    var values = range[6..].Split('-');
                    start = int.Parse(values[0], System.Globalization.CultureInfo.InvariantCulture);
                    if (values.Length == 2 && values[1].Length > 0)
                    {
                        end = Math.Min(end, int.Parse(values[1], System.Globalization.CultureInfo.InvariantCulture));
                    }
                    if (start > end)
                    {
                        status = 416;
                        start = 0;
                        end = -1;
                    }
                    else
                    {
                        status = 206;
                        rangeHeader = $"Content-Range: bytes {start}-{end}/{data.Length}\r\n";
                    }
                }
                var length = end - start + 1;
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Length: {length}\r\nContent-Type: {contentType}\r\n{rangeHeader}Accept-Ranges: bytes\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, stop.Token);
                if (length > 0)
                {
                    await stream.WriteAsync(data.AsMemory(start, length), stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
                // FFmpeg closes range requests once it has enough data or the test is canceled.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        await serving;
        await Task.WhenAll(connections);
        stop.Dispose();
    }
}
