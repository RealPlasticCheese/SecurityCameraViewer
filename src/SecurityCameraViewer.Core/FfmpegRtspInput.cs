namespace SecurityCameraViewer.Core;

/// <summary>Builds a private stdin document, never a command-line argument or a temporary file.</summary>
public static class FfmpegRtspInput
{
    public static string Create(ViewerSettings settings, string password, int channel, bool includeAudio = false)
    {
        var uri = settings.StreamUri(channel).AbsoluteUri;
        var userInfo = Uri.EscapeDataString(settings.CameraUsername(channel)) + ":" + Uri.EscapeDataString(settings.CameraPassword(channel, password));
        uri = ("rtsp://" + userInfo + "@" + uri["rtsp://".Length..]).Replace("'", "'\\''");
        var mediaTypes = includeAudio ? "video+audio" : "video";
        return $"ffconcat version 1.0\nfile '{uri}'\noption rtsp_transport tcp\noption allowed_media_types {mediaTypes}\noption analyzeduration 1000000\noption probesize 1000000\n";
    }
}
