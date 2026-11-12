namespace SecurityCameraViewer.Web;

/// <summary>Keeps one preview lease for each camera in the current connection generation.</summary>
public sealed class CameraPreviewSubscription : IDisposable
{
    private readonly Dictionary<WebCamera, IDisposable> _leases = [];

    public void Update(IEnumerable<WebCamera> cameras)
    {
        var current = cameras.ToHashSet();
        foreach (var removed in _leases.Keys.Where(camera => !current.Contains(camera)).ToArray())
        {
            _leases.Remove(removed, out var lease);
            lease!.Dispose();
        }
        foreach (var camera in current)
        {
            if (!_leases.ContainsKey(camera))
            {
                _leases.Add(camera, camera.SubscribePreview());
            }
        }
    }

    public void Dispose()
    {
        foreach (var lease in _leases.Values)
        {
            lease.Dispose();
        }
        _leases.Clear();
    }
}
