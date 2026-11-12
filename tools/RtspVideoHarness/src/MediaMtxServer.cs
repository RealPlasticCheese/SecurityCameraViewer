using System.Text.Json;

namespace RtspVideoHarness;

public sealed class MediaMtxServer : IAsyncDisposable
{
    private readonly string configurationPath;
    private readonly ManagedProcess process;

    private MediaMtxServer(string configurationPath, ManagedProcess process)
    {
        this.configurationPath = configurationPath;
        this.process = process;
    }

    public Task Completion => process.Completion;
    public IOException Failure() => process.Failure();

    public static string Configuration(HarnessOptions options) => JsonSerializer.Serialize(new
    {
        logLevel = "info",
        logDestinations = new[] { "stdout" },
        rtsp = true,
        rtspAddress = $"{(options.ListenAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{options.ListenAddress}]" : options.ListenAddress.ToString())}:{options.Port}",
        rtspTransports = new[] { "tcp" },
        authMethod = "internal",
        authInternalUsers = new[]
        {
            new { user = "any", pass = "", ips = Array.Empty<string>(), permissions = new[] { new { action = "read", path = options.StreamPath } } },
            new { user = "any", pass = "", ips = new[] { options.PublisherAddress.ToString() }, permissions = new[] { new { action = "publish", path = options.StreamPath } } }
        },
        api = false,
        metrics = false,
        pprof = false,
        playback = false,
        rtmp = false,
        hls = false,
        webrtc = false,
        srt = false,
        moq = false,
        paths = new Dictionary<string, object> { [options.StreamPath] = new { source = "publisher", overridePublisher = false } }
    });

    public static async Task<MediaMtxServer> StartAsync(HarnessOptions options, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rtsp-video-harness-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, Configuration(options), cancellationToken);
        ManagedProcess? process = null;
        try
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process = new ManagedProcess(options.MediaMtxExecutable, [path], "MediaMTX", outputLine: line =>
            {
                if (line.Contains("[RTSP]", StringComparison.Ordinal) &&
                    (line.Contains("listener opened", StringComparison.Ordinal) || line.Contains("started with listeners", StringComparison.Ordinal)))
                {
                    ready.TrySetResult();
                }
            });
            var first = await Task.WhenAny(ready.Task, process.Completion).WaitAsync(options.ProcessTimeout, cancellationToken);
            if (first == process.Completion)
            {
                await process.EnsureSuccessAsync(cancellationToken);
                throw process.Failure();
            }

            return new MediaMtxServer(path, process);
        }
        catch
        {
            if (process != null)
            {
                await process.DisposeAsync();
            }

            File.Delete(path);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await process.DisposeAsync();
        }
        finally
        {
            File.Delete(configurationPath);
        }
    }
}
