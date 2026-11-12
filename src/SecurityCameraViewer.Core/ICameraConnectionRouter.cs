namespace SecurityCameraViewer.Core;

public sealed record CameraConnection(ViewerSettings Settings, string Password);

/// <summary>Allows a host to share camera inputs without coupling media workers to a relay implementation.</summary>
public interface ICameraConnectionRouter
{
    CameraConnection Resolve(ViewerSettings settings, string password, int channel);
}
