using System.Net;
using System.Net.Sockets;
using System.Text;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;

internal static class AudioMetadataTests
{
    private static int _checks;
    private const string Video = "v=0\r\nm=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\n";

    public static async Task RunAsync()
    {
        foreach (var (payload, mapping, expected) in new[]
        {
            ("97", "a=rtpmap:97 MPEG4-GENERIC/48000/2", "AAC"),
            ("98", "a=rtpmap:98 MP4A-LATM/48000", "AAC"),
            ("99", "a=rtpmap:99\tOPUS/48000/2", "OPUS"),
            ("0", "", "PCMU"), ("8", "", "PCMA"), ("9", "", "G722"),
            ("18", "", "G729"), ("10", "", "PCM")
        })
        {
            var result = RtspAudioMetadata.Read(Video + $"m=audio 0 RTP/AVP {payload}\r\n{mapping}\r\n");
            Check(result.HasAudio && result.Codec == expected, $"Source audio codec {expected} is known before playback");
        }
        Check(RtspAudioMetadata.Read(Video + "m=audio 0 RTP/AVP\r\n") is { HasAudio: true, Codec: null },
            "Incomplete audio media descriptions do not falsely claim No Audio");
        Check(!RtspAudioMetadata.Read(Video).HasAudio, "Video-only SDP has no audio");
        Check(!RtspAudioMetadata.Read(Video + "a=rtpmap:97 MPEG4-GENERIC/48000\r\n").HasAudio, "Mappings outside audio sections do not invent audio");
        Check(!RtspAudioMetadata.Read(Video + "m=audio 0 RTP/AVP 0\r\na=recvonly\r\n").HasAudio, "Talkback-only tracks are excluded");
        Check(!RtspAudioMetadata.Read(Video + "m=audio 0 RTP/AVP 0\r\na=inactive\r\n").HasAudio, "Inactive tracks are excluded");
        Check(RtspAudioMetadata.Read("a=recvonly\r\n" + Video + "m=audio 0 RTP/AVP 0\r\na=sendonly\r\n").HasAudio,
            "Media direction overrides session direction");
        Check(!RtspAudioMetadata.Read("a=recvonly\r\n" + Video + "m=audio 0 RTP/AVP 0\r\n").HasAudio,
            "Session direction applies to audio");
        Check(RtspAudioMetadata.Read(Video + "m=audio 0 RTP/AVP 97\r\na=sendonly\r\nm=video 0 RTP/AVP 97\r\na=rtpmap:97 MPEG4-GENERIC/48000\r\n") is { HasAudio: true, Codec: null },
            "Unknown audio mappings remain available without borrowing another track's codec");
        Check(RtspAudioMetadata.Read(Video + "m=audio 0 RTP/AVP 97\r\na=rtpmap:970 MPEG4-GENERIC/48000\r\n") is { HasAudio: true, Codec: null },
            "Payload IDs match exactly");
        Check(RtspAudioMetadata.Read(Video + "m=audio 0 RTP/AVP 97\r\na=rtpmap:97 <script>/48000\r\n") is { HasAudio: true, Codec: null },
            "Malformed codec names are not displayed");
        Check(RtspAudioMetadata.Read(Video + "m=audio 0 RTP/AVP 97 0\r\n").Codec == "PCMU", "Alternate advertised payloads are considered");
        Check(RtspAudioMetadata.Read(Video + "m=audio 0 RTP/AVP 0\r\na=recvonly\r\nm=audio 0 RTP/AVP 8\r\na=sendonly\r\n").Codec == "PCMA",
            "Receiving media is found after a backchannel");

        await ProbeAsync(Video, false, null);
        var h83 = Video + "a=recvonly\r\nm=audio 0 RTP/AVP 0\r\nc=IN IP4 0.0.0.0\r\na=rtpmap:0 PCMU/8000\r\na=control:track=2\r\na=recvonly\r\n";
        Check(!RtspAudioMetadata.Read(h83).HasAudio &&
            RtspAudioMetadata.Read(h83, includeReceiveOnly: true) is { HasAudio: true, Codec: "PCMU" },
            "H83 audio is a candidate requiring media verification, not an assumed microphone");
        Check(!RtspAudioMetadata.Read(Video + "a=inactive\r\nm=audio 0 RTP/AVP 0\r\na=recvonly\r\n").HasAudio,
            "Inactive video does not enable talkback-only audio");
        Check(!RtspAudioMetadata.Read(Video + "a=recvonly\r\nm=audio 0 RTP/AVP 0\r\na=inactive\r\n").HasAudio,
            "Receive-only video does not enable inactive audio");
        await ProbeAsync(h83, null, "PCMU");
        await ProbeAsync(Video + "m=audio 0 RTP/AVP 0\r\na=recvonly\r\n", null, "PCMU");
        await ProbeAsync(Video + "m=audio 0 RTP/AVP 97\r\na=rtpmap:97 MPEG4-GENERIC/48000\r\n", true, "AAC");
        await DiscoveryAsync();
        await CameraStateAsync();
        await VerificationAsync();
        Console.WriteLine($"PASS: {_checks} audio metadata checks");
    }

    private static async Task ProbeAsync(string sdp, bool? hasAudio, string? codec)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            await ReadRequestAsync(stream, deadline.Token);
            var body = Encoding.ASCII.GetBytes(sdp);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: {body.Length}\r\n\r\n"), deadline.Token);
            await stream.WriteAsync(body, deadline.Token);
        });
        var result = await RtspProbe.DescribeAsync(new ViewerSettings
        {
            Host = "127.0.0.1",
            RtspPort = ((IPEndPoint)listener.LocalEndpoint).Port
        }, "local-only-test", cancellationToken: deadline.Token);
        await server;
        Check(result.Success && result.HasAudio == hasAudio && result.AudioCodec == codec &&
            result.AudioVerificationRequired == (hasAudio is null),
            "RTSP DESCRIBE publishes source audio without opening media playback");
    }

    private static async Task VerificationAsync()
    {
        var candidate = new ProbeResult(true, 200, "OK")
        {
            AudioVerificationRequired = true, AudioCodec = "PCMU"
        };
        var settings = new ViewerSettings { Substream = true };
        var calls = 0;
        var process = new VerificationProcess(async (arguments, input, read, token) =>
        {
            calls++;
            Check(arguments.Contains("0:a:0") && arguments.Contains("pcm_s16le") &&
                arguments.Contains("1") && arguments.Contains("pipe:1"), "Verification requests one decoded audio frame");
            Check(input!.Contains("/ip01/0") && input.Contains("video+audio"), "Verification uses private main-stream input");
            await read(new MemoryStream(new byte[] { 0, 0 }), token);
        });
        var verification = new CameraAudioVerification(process);
        var result = await verification.VerifyAsync(candidate, settings, "secret", 1, CancellationToken.None);
        Check(result is { HasAudio: true, AudioVerificationRequired: false, AudioCodec: "PCMU" },
            "Decoded samples confirm audio, including digital silence");
        foreach (var ordinary in new[] { candidate with { AudioVerificationRequired = false, HasAudio = false },
            candidate with { AudioVerificationRequired = false, HasAudio = true }, candidate with { Success = false } })
        {
            Check(await verification.VerifyAsync(ordinary, settings, "secret", 1, CancellationToken.None) == ordinary,
                "Ordinary or unsuccessful descriptions do not open a media probe");
        }
        Check(calls == 1, "Only ambiguous audio opens an additional connection");
        foreach (var failure in new Func<Func<Stream, CancellationToken, Task>, CancellationToken, Task>[]
        {
            (read, token) => read(Stream.Null, token),
            (_, _) => throw new IOException("private credential detail"),
            (_, _) => throw new OperationCanceledException()
        })
        {
            var failed = new CameraAudioVerification(new VerificationProcess((_, _, read, token) => failure(read, token)));
            Check(await failed.VerifyAsync(candidate, settings, "secret", 1, CancellationToken.None) == candidate,
                "No samples, process failure and timeout leave audio unknown for retry");
        }
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var cancelled = new CameraAudioVerification(new VerificationProcess((_, _, _, token) => Task.FromCanceled(token)));
        try
        {
            await cancelled.VerifyAsync(candidate, settings, "secret", 1, stop.Token);
            Check(false, "Cancellation must propagate");
        }
        catch (OperationCanceledException)
        {
            Check(true, "Disconnect cancellation propagates from verification");
        }
        var retryCalls = 0;
        await using var discovery = new CameraAudioDiscovery(_ => Task.FromResult(++retryCalls == 1 ? candidate : result),
            (_, _) => Task.CompletedTask);
        await UntilAsync(() => discovery.Result is not null);
        Check(retryCalls == 2 && discovery.Result!.HasAudio == true, "Unverified candidates retry before publishing availability");
    }

    private sealed class VerificationProcess(
        Func<string[], string?, Func<Stream, CancellationToken, Task>, CancellationToken, Task> run) : IFfmpegProcess
    {
        public Task RunAsync(IEnumerable<string> arguments, string? input,
            Func<Stream, CancellationToken, Task> read, CancellationToken token, string pipeline = "other", int channel = 0)
            => run(arguments.ToArray(), input, read, token);
    }

    private static async Task DiscoveryAsync()
    {
        var attempts = 0;
        var delays = new System.Collections.Concurrent.ConcurrentQueue<TimeSpan>();
        var allowRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var discovery = new CameraAudioDiscovery(_ =>
        {
            return ++attempts switch
            {
                1 => throw new IOException("secret stream URL"),
                2 => Task.FromResult(new ProbeResult(false, 401, "Rejected") { HasAudio = false }),
                3 => Task.FromResult(new ProbeResult(true, 200, "Legacy result")),
                _ => Task.FromResult(new ProbeResult(true, 200, "OK") { HasAudio = true, AudioCodec = "AAC" })
            };
        }, async (delay, token) =>
        {
            delays.Enqueue(delay);
            await allowRetry.Task.WaitAsync(token);
        });
        await UntilAsync(() => delays.Count == 1);
        Check(discovery.Result is null, "Probe failures leave availability unknown");
        allowRetry.SetResult();
        await UntilAsync(() => discovery.Result is not null);
        Check(attempts == 4 && delays.Count == 3 && delays.ToArray()[0] > TimeSpan.Zero && delays.ToArray()[2] > delays.ToArray()[0],
            "Exceptions, rejected probes and incomplete metadata retry with existing recovery backoff");
        Check(discovery.Result is { HasAudio: true, AudioCodec: "AAC" }, "Successful metadata is retained");
        await discovery.DisposeAsync();
        await discovery.DisposeAsync();
        Check(attempts == 4, "Completed discovery does not keep probing and disposal is idempotent");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var pending = new CameraAudioDiscovery(async token =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return new(true, 200, "Unexpected");
            }
            finally
            {
                cancelled = token.IsCancellationRequested;
            }
        });
        await started.Task;
        await pending.DisposeAsync();
        Check(cancelled && pending.Result is null, "Disconnect cancels a pending discovery without false No Audio");

        var probes = 0;
        var gate = new BatteryCameraAvailability(_ =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult(new ProbeResult(true, 200, "OK") { HasAudio = false });
        }, TimeSpan.FromMinutes(1));
        var tasks = Enumerable.Range(0, 3).Select(_ => gate.WaitAsync(_ => { }, CancellationToken.None));
        await Task.WhenAll(tasks);
        Check(probes == 1, "Battery audio metadata shares the existing availability probe");
    }

    private static async Task CameraStateAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            await ReadRequestAsync(stream, deadline.Token);
            accepted.SetResult();
            await release.Task.WaitAsync(deadline.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"RTSP/1.0 200 OK\r\nContent-Length: {Video.Length}\r\n\r\n{Video}"), deadline.Token);
        });
        await using var camera = new WebCamera(new ViewerSettings
        {
            Host = "127.0.0.1",
            RtspPort = ((IPEndPoint)listener.LocalEndpoint).Port
        }, "local-only-test", 1, "missing-ffmpeg", 1, onDemand: true);
        await accepted.Task.WaitAsync(deadline.Token);
        Check(camera.State.HasAudio is null, "Camera state says unknown before DESCRIBE finishes");
        release.SetResult();
        await server;
        await UntilAsync(() => camera.State.HasAudio is not null);
        Check(camera.State is { HasAudio: false, AudioCodec: null } && camera.Frame is null,
            "Idle browser previews expose No Audio without starting a decoder");
    }

    private static async Task ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var request = new StringBuilder();
        var one = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            await stream.ReadExactlyAsync(one, token);
            request.Append((char)one[0]);
        }
        Check(request.ToString().StartsWith("DESCRIBE ", StringComparison.Ordinal), "Discovery uses read-only DESCRIBE");
    }

    private static async Task UntilAsync(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready())
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    private static void Check(bool passed, string message)
    {
        if (!passed)
        {
            throw new InvalidOperationException(message);
        }
        Interlocked.Increment(ref _checks);
    }
}
