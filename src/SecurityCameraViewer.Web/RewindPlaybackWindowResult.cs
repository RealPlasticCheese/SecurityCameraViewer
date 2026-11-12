using System.Buffers.Binary;
using System.Text.Json;

namespace SecurityCameraViewer.Web;

/// <summary>Streams one admitted window without concatenating or base64-encoding its media.</summary>
public sealed class RewindPlaybackWindowResult(RewindPlaybackWindow window) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        var header = new RewindWindowHeader(window.Playlist, window.Initialization.Length,
            window.Fragments.Select(fragment => fragment.Length).ToArray());
        var metadata = JsonSerializer.SerializeToUtf8Bytes(header, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)metadata.Length));

        var response = context.Response;
        response.ContentType = "application/octet-stream";
        response.ContentLength = prefix.Length + (long)metadata.Length + window.Initialization.LongLength +
            window.Fragments.Sum(fragment => fragment.LongLength);
        response.Headers.CacheControl = "no-store";
        var token = context.RequestAborted;
        await response.Body.WriteAsync(prefix, token);
        await response.Body.WriteAsync(metadata, token);
        await response.Body.WriteAsync(window.Initialization, token);
        foreach (var fragment in window.Fragments)
        {
            await response.Body.WriteAsync(fragment, token);
        }
    }
}
