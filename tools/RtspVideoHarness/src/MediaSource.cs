namespace RtspVideoHarness;

public sealed record MediaInput(string Location, IReadOnlyDictionary<string, string>? Headers = null, bool LiveHls = false);

public sealed record MediaSource(MediaInput Video, MediaInput? Audio, string Name, bool IsLive = false);

public interface IMediaSourceResolver
{
    Task<MediaSource> ResolveAsync(VideoFile file, HarnessOptions options, CancellationToken cancellationToken);
}

public sealed class FileMediaSourceResolver : IMediaSourceResolver
{
    public async Task<MediaSource> ResolveAsync(VideoFile file, HarnessOptions options, CancellationToken cancellationToken)
    {
        var input = new MediaInput(file.FullPath);
        var hasAudio = options.AudioEnabled && await AudioFrames.HasAudioAsync(file, options, cancellationToken);
        return new(input, hasAudio ? input : null, file.RelativePath);
    }
}
