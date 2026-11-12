namespace RtspVideoHarness;

public interface IVideoPlayback
{
    Task PlayFileAsync(VideoFile file, CancellationToken cancellationToken);
    Task PlayBlackAsync(TimeSpan duration, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

public sealed class PlaylistPlayer(IVideoPlayback playback, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task RunAsync(IReadOnlyList<VideoFile> files, HarnessOptions options, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            throw new ArgumentException("The playlist must contain at least one video.", nameof(files));
        }

        do
        {
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await playback.PlayFileAsync(files[index], cancellationToken);
                if (options.BlankPeriod == TimeSpan.Zero || options.Once && index == files.Count - 1)
                {
                    continue;
                }

                if (options.OfflineGaps)
                {
                    await playback.DisconnectAsync(cancellationToken);
                    // Chunk long gaps to respect Task.Delay's actual timer range, without limiting the gap itself.
                    var remaining = options.BlankPeriod;
                    var maximum = TimeSpan.FromMilliseconds(uint.MaxValue - 1L);
                    while (remaining > TimeSpan.Zero)
                    {
                        var delay = remaining > maximum ? maximum : remaining;
                        await Task.Delay(delay, time, cancellationToken);
                        remaining -= delay;
                    }
                }
                else
                {
                    await playback.PlayBlackAsync(options.BlankPeriod, cancellationToken);
                }
            }
        }
        while (!options.Once);
    }
}
