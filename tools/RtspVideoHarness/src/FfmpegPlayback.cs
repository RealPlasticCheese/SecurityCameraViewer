namespace RtspVideoHarness;

public sealed class FfmpegPlayback(HarnessOptions options, IMediaSourceResolver? sourceResolver = null) : IVideoPlayback, IAsyncDisposable
{
    private readonly IMediaSourceResolver resolver = sourceResolver ?? new FileMediaSourceResolver();
    private readonly FramePacer pacer = new(options.Format.FramesPerSecond);
    private readonly AudioFrameClock audioClock = new(options.Format.FramesPerSecond);
    private FfmpegPublisher? publisher;

    public async Task PlayFileAsync(VideoFile file, CancellationToken cancellationToken)
    {
        var source = await resolver.ResolveAsync(file, options, cancellationToken);
        Console.WriteLine($"Playing: {source.Name}{(source.IsLive ? " (live)" : "")}");
        await using var audioDecoder = options.AudioEnabled && source.Audio != null ? new ManagedProcess(options.FfmpegExecutable,
            FfmpegCommands.AudioDecoder(source.Audio), "FFmpeg audio decoder", binaryOutput: true) : null;
        await using var decoder = new ManagedProcess(options.FfmpegExecutable,
            FfmpegCommands.Decoder(source.Video, options.Format), "FFmpeg decoder", binaryOutput: true);
        var frames = await VideoFrames.CopyAsync(decoder.StandardOutput, options.Format.FrameBytes, async (frame, token) =>
        {
            var audio = options.AudioEnabled ? await AudioFrames.ReadAsync(audioDecoder?.StandardOutput, audioClock.NextByteCount(), token) : [];
            await WriteFrameAsync(frame, audio, token);
        }, cancellationToken);
        await decoder.EnsureSuccessAsync(cancellationToken);
        if (audioDecoder != null && audioDecoder.Completion.IsCompleted)
        {
            await audioDecoder.EnsureSuccessAsync(cancellationToken);
        }
        if (frames == 0)
        {
            throw new InvalidDataException($"No video frames decoded from {file.FullPath}.");
        }
    }

    public async Task PlayBlackAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Black gap: {duration.TotalMilliseconds:0} ms");
        var frame = BlackFrames.Create(options.Format);
        var count = BlackFrames.Count(duration, options.Format.FramesPerSecond);
        for (long index = 0; index < count; index++)
        {
            var audio = options.AudioEnabled ? new byte[audioClock.NextByteCount()] : [];
            await WriteFrameAsync(frame, audio, cancellationToken);
        }
    }

    private async Task WriteFrameAsync(ReadOnlyMemory<byte> frame, ReadOnlyMemory<byte> audio, CancellationToken cancellationToken)
    {
        publisher ??= new FfmpegPublisher(options);
        await pacer.WaitAsync(cancellationToken);
        await publisher.WriteAsync(frame, audio, cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (publisher is not { } current)
        {
            return;
        }

        publisher = null;
        await using (current)
        {
            await current.CompleteAsync(cancellationToken);
        }

        Console.WriteLine("Stream offline.");
    }

    public async ValueTask DisposeAsync()
    {
        if (publisher is { } current)
        {
            publisher = null;
            await current.DisposeAsync();
        }
    }
}
