using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

/// <summary>Verifies ambiguous SDP by receiving and decoding audio; never transmits microphone media.</summary>
public sealed class CameraAudioVerification(IFfmpegProcess process)
{
    public async Task<ProbeResult> VerifyAsync(ProbeResult result, ViewerSettings settings, string password,
        int channel, CancellationToken token)
    {
        if (!result.Success || !result.AudioVerificationRequired)
        {
            return result;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var received = false;
        try
        {
            await process.RunAsync([
                "-f", "concat", "-safe", "0", "-protocol_whitelist", "pipe,tcp,udp,rtp",
                "-i", "pipe:0", "-map", "0:a:0", "-frames:a", "1",
                "-acodec", "pcm_s16le", "-f", "s16le", "pipe:1"
            ], FfmpegRtspInput.Create(settings with { Substream = false }, password, channel, includeAudio: true),
                async (stream, ct) =>
                {
                    var buffer = new byte[4096];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, ct)) > 0)
                    {
                        received |= count > 0;
                    }
                }, deadline.Token, pipeline: "audio-discovery", channel: channel);
            return received ? result with { HasAudio = true, AudioVerificationRequired = false } : result;
        }
        catch (Exception ex) when (ex is IOException || ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            // An unavailable camera, unsupported codec or timeout is not proof of absent audio.
            return result;
        }
    }
}
