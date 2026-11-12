using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

public interface IMotionFrameSource
{
    Task RunAsync(ViewerSettings connection, string password, int channel, Func<byte[], int, int, Task> frame, CancellationToken token);

    Task RunAsync(ViewerSettings connection, string password, int channel, CameraRecordingSettings settings,
        Func<AnalysisFrame, Task> frame, CancellationToken token)
    {
        return RunAsync(connection, password, channel,
            (pixels, width, height) => frame(new(pixels, width, height, false, DateTimeOffset.UtcNow)), token);
    }
}

public interface IEncodedVideoSource
{
    Task RunAsync(ViewerSettings connection, string password, int channel, Func<byte[], Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token);
}

public sealed class FfmpegCameraMediaSource(FfmpegProcess process, ICameraConnectionRouter? connectionRouter = null) : IMotionFrameSource, IEncodedVideoSource
{
    public const int Width = 320, Height = 180;
    private static readonly string[] InputArguments = ["-threads", "2", "-f", "concat", "-safe", "0", "-protocol_whitelist", "pipe,tcp,udp,rtp", "-i", "pipe:0", "-map", "0:v:0", "-sn", "-dn"];

    public Task RunAsync(ViewerSettings connection, string password, int channel, CameraRecordingSettings settings,
        Func<AnalysisFrame, Task> frame, CancellationToken token)
    {
        var rgb = settings.DetectionMethod != DetectionMethods.Adaptive || settings.Adaptive.SuppressShadows;
        var width = settings.DetectionMethod != DetectionMethods.Adaptive ? Math.Max(640, settings.AnalysisWidth) : settings.AnalysisWidth;
        var height = width * 9 / 16;
        var interval = (1 / settings.AnalysisFramesPerSecond - 0.001).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return process.RunAsync(InputArguments.Concat(["-an", "-filter_threads", "1", "-vf",
            $"select='isnan(prev_selected_t)+gte(t-prev_selected_t,{interval})',scale={width}:{height}",
            "-vsync", "0", "-threads", "1", "-pix_fmt", rgb ? "rgb24" : "gray", "-f", "rawvideo", "pipe:1"]),
            CreateInput(connection, password, channel, false),
            (stream, ct) => AnalysisFrameReader.ReadAsync(stream, width, height, rgb, frame, ct), token, "analysis", channel);
    }

    public Task RunAsync(ViewerSettings connection, string password, int channel, Func<byte[], int, int, Task> frame, CancellationToken token) =>
        process.RunAsync(InputArguments.Concat(["-an", "-filter_threads", "1", "-vf",
            "select='isnan(prev_selected_t)+gte(t-prev_selected_t,0.199)',scale=320:180", "-vsync", "0", "-threads", "1", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1"]),
            CreateInput(connection, password, channel, false), async (stream, ct) =>
            {
                var pixels = new byte[Width * Height];
                while (true)
                {
                    try
                    {
                        await stream.ReadExactlyAsync(pixels, ct);
                    }
                    catch (EndOfStreamException)
                    {
                        return;
                    }
                    await frame(pixels, Width, Height);
                }
            }, token, "analysis", channel);

    public Task RunAsync(ViewerSettings connection, string password, int channel, Func<byte[], Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token) =>
        process.RunAsync(InputArguments.Concat(["-map", "0:a?", "-c:v", "copy", "-c:a", "aac", "-b:a", "64k", "-threads:a", "1",
            "-movflags", "+empty_moov+frag_keyframe+default_base_moof", "-flush_packets", "1", "-f", "mp4", "pipe:1"]),
            CreateInput(connection with
            {
                Substream = false
            }, password, channel, includeAudio: true),
            (stream, ct) => FragmentedMp4.ReadAsync(stream, initialized, fragment, ct), token, "recording", channel);

    private string CreateInput(ViewerSettings settings, string password, int channel, bool includeAudio)
    {
        var connection = connectionRouter?.Resolve(settings, password, channel) ?? new CameraConnection(settings, password);
        return FfmpegRtspInput.Create(connection.Settings, connection.Password, channel, includeAudio);
    }
}
