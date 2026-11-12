using SecurityCameraViewer.Nvr;
using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web.Nvr;

public interface INvrMediaConverter
{
    Task ConvertAsync(string inputPath, string outputPath, CancellationToken token);
}

public sealed class NvrMediaConverter(FfmpegProcess ffmpeg) : INvrMediaConverter
{
    public async Task ConvertAsync(string inputPath, string outputPath, CancellationToken token)
    {
        try
        {
            await ffmpeg.FileAsync(["-i", inputPath, "-map", "0:v:0", "-c:v", "copy", "-an", "-movflags", "+faststart", "-f", "mp4", outputPath], token);
        }
        catch (IOException)
        {
            throw new NvrException("Could not convert the recording to MP4. Check FFmpeg availability and free disk space.");
        }
    }
}
