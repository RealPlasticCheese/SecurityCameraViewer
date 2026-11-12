using System.Collections.Concurrent;
using System.Threading.Channels;
using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

/// <summary>Bounded in-memory fan-out. Capture threads never perform network or queue-file I/O.</summary>
public sealed class IntegrationEventHub : ICameraEventPublisher
{
    private readonly ConcurrentDictionary<string, Channel<CameraEvent>> _listeners = new();
    public Channel<CameraEvent> Pending { get; } = Channel.CreateBounded<CameraEvent>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait });
    private long _dropped;
    public long Dropped => Interlocked.Read(ref _dropped);
    public void Publish(CameraEvent value)
    {
        if (!Pending.Writer.TryWrite(value))
        {
            Interlocked.Increment(ref _dropped);
        }

        foreach (var listener in _listeners.Values)
        {
            if (!listener.Writer.TryWrite(value))
            {
                listener.Writer.TryComplete(new IOException("Event subscriber fell behind; reconnect and refresh state."));
            }
        }
    }
    public (string Id, ChannelReader<CameraEvent> Reader) Subscribe()
    {
        if (_listeners.Count >= 64)
        {
            throw new InvalidOperationException("Too many event subscribers.");
        }

        var id = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateBounded<CameraEvent>(128);
        _listeners[id] = channel;
        return (id, channel.Reader);
    }
    public void Unsubscribe(string id)
    {
        if (_listeners.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }
}
