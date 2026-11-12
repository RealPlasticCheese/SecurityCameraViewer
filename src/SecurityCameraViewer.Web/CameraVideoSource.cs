using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

/// <summary>Shares the camera's selected encoded video stream without decoding or audio conversion.</summary>
public sealed class CameraVideoSource(IFfmpegProcess process, ICameraConnectionRouter? connectionRouter = null) : ILiveMediaSource
{
    public Task RunAsync(ViewerSettings settings, string password, int channel,
        Func<LiveMediaInitialization, Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var connection = connectionRouter?.Resolve(settings, password, channel) ?? new CameraConnection(settings, password);
        var input = FfmpegRtspInput.Create(connection.Settings, connection.Password, channel, includeAudio: false);
        string[] arguments =
        [
            "-f", "concat", "-safe", "0", "-protocol_whitelist", "pipe,tcp,udp,rtp", "-i", "pipe:0",
            "-map", "0:v:0", "-an", "-sn", "-dn", "-c:v", "copy",
            "-movflags", "+empty_moov+frag_keyframe+default_base_moof+negative_cts_offsets",
            "-frag_duration", "1000000", "-flush_packets", "1", "-f", "mp4", "pipe:1"
        ];
        return process.RunAsync(arguments, input, (stream, ct) => FragmentedMp4.ReadAsync(stream,
            bytes =>
            {
                LiveMediaInitialization description;
                try
                {
                    description = FfmpegLiveMediaSource.Describe(bytes);
                }
                catch (InvalidDataException)
                {
                    throw new UnsupportedCameraVideoException();
                }
                return initialized(description);
            }, fragment, ct), token, "grid.copy", channel);
    }
}

public sealed class UnsupportedCameraVideoException : Exception;
