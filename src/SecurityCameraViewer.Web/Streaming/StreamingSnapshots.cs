using System.Collections.Concurrent;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web.Streaming;

public interface IStreamingSnapshotProvider
{
    Task<byte[]> GetAsync(StreamingProfile profile, CancellationToken token);
}

/// <summary>Snapshots use the shared compressed relay, independent of browser connections.</summary>
public sealed class StreamingSnapshots(StreamingSettingsStore settings, MediaRelayService relay, FfmpegProcess ffmpeg) : IStreamingSnapshotProvider
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Time, byte[] Jpeg)> _cache = new();
    private readonly SemaphoreSlim _limit = new(2, 2);

    public async Task<byte[]> GetAsync(StreamingProfile profile, CancellationToken token)
    {
        if (!relay.Status.Running)
        {
            throw new IOException("RTSP relay is unavailable.");
        }

        var key = $"{profile.Token}/{(profile.Substream ? profile.Camera.SubStream : profile.Camera.MainStream)}";
        if (_cache.TryGetValue(key, out var cached) && cached.Time > DateTimeOffset.UtcNow.AddSeconds(-2))
        {
            return cached.Jpeg;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await _limit.WaitAsync(deadline.Token);
        try
        {
            var input = new ViewerSettings
            {
                Sources = [new CameraSource
                {
                    Channel = 1, MainStream = $"rtsp://127.0.0.1:{settings.Current.RtspPort}/{profile.Path}",
                    Username = relay.InternalAccount.Username, UseDefaultPassword = true
                }]
            };
            using var output = new MemoryStream();
            await ffmpeg.RunAsync(["-f", "concat", "-safe", "0", "-protocol_whitelist", "pipe,tcp,udp,rtp", "-i", "pipe:0",
                "-map", "0:v:0", "-an", "-frames:v", "1", "-vf", "scale=w='min(1280,iw)':h=-2", "-f", "image2pipe", "-c:v", "mjpeg", "pipe:1"],
                FfmpegRtspInput.Create(input, relay.InternalAccount.Password, 1), async (stream, ct) =>
                {
                    var buffer = new byte[16384];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, ct)) > 0)
                    {
                        if (output.Length + count > 2 * 1024 * 1024)
                        {
                            throw new IOException("Snapshot is too large.");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                }, deadline.Token);
            var jpeg = output.ToArray();
            if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8)
            {
                throw new IOException("No snapshot received.");
            }

            if (_cache.Count > 64)
            {
                _cache.Clear();
            }

            _cache[key] = (DateTimeOffset.UtcNow, jpeg);
            return jpeg;
        }
        finally
        {
            _limit.Release();
        }
    }
}
