using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace SecurityCameraViewer.Web;

public static class CameraVideoEndpoints
{
    public static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromSeconds(10);

    public static void MapCameraVideoEndpoints(this WebApplication app)
    {
        app.MapMethods("/api/cameras/{channel:int}/video", [HttpMethods.Get, HttpMethods.Connect],
            async (int channel, HttpContext context, ViewerService viewer, IntegrationAuth auth, ILoggerFactory loggerFactory) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                await using var subscription = await viewer.SubscribeLiveAsync(channel, context.RequestAborted, cameraVideo: true);
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
                using var acknowledged = new SemaphoreSlim(0, 1);
                var receive = ReceiveAsync(socket, acknowledged, lifetime, () => auth.Allowed(context, "read"));
                var logger = loggerFactory.CreateLogger(nameof(CameraVideoEndpoints));
                var lastAcknowledgementMilliseconds = 0d;
                var awaitingAcknowledgement = false;
                try
                {
                    await foreach (var packet in subscription.ReadAllAsync(lifetime.Token))
                    {
                        if (!auth.Allowed(context, "read"))
                        {
                            break;
                        }
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        deadline.CancelAfter(AcknowledgementTimeout);
                        var sentAt = Stopwatch.GetTimestamp();
                        if (packet.Reason == "queue-overflow")
                        {
                            logger.LogWarning("Camera {Channel} viewer queue overflow over {Protocol}: {Queue}; previous acknowledgement {AcknowledgementMilliseconds:F1} ms.",
                                channel, context.Request.Protocol, packet.Queue, lastAcknowledgementMilliseconds);
                        }
                        if (packet.Type != "fragment" || packet.ProducedAt is not null)
                        {
                            var metadata = JsonSerializer.SerializeToUtf8Bytes(Metadata(packet, context.Request.Protocol));
                            await socket.SendAsync(metadata, WebSocketMessageType.Text, true, deadline.Token);
                        }
                        if (packet.Bytes is not null)
                        {
                            await socket.SendAsync(packet.Bytes, WebSocketMessageType.Binary, true, deadline.Token);
                        }
                        // One fragment may be decoded at a time. The hub's configurable queue bounds the remainder.
                        awaitingAcknowledgement = true;
                        await acknowledged.WaitAsync(deadline.Token);
                        awaitingAcknowledgement = false;
                        lastAcknowledgementMilliseconds = Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds;
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
                {
                    if (!lifetime.IsCancellationRequested)
                    {
                        logger.LogWarning("Camera {Channel} viewer transport interrupted over {Protocol}: {Reason}; {Queue}; previous acknowledgement {AcknowledgementMilliseconds:F1} ms.",
                            channel, context.Request.Protocol, ex is OperationCanceledException
                                ? awaitingAcknowledgement ? "acknowledgement-timeout" : "send-timeout"
                                : "socket-interrupted", subscription.QueueDiagnostics, lastAcknowledgementMilliseconds);
                    }
                }
                finally
                {
                    await lifetime.CancelAsync();
                    socket.Abort();
                    await receive;
                }
            });
    }

    internal static object Metadata(LiveMediaPacket packet, string protocol) => new
    {
        type = packet.Type,
        streamId = packet.StreamId,
        startSeconds = packet.StartSeconds,
        endSeconds = packet.EndSeconds,
        producedAt = packet.ProducedAt,
        mimeType = packet.MimeType,
        status = packet.Status,
        message = packet.Message,
        fallback = packet.Status == "unsupported",
        reason = packet.Reason,
        queue = packet.Queue is { } queue ? new
        {
            queuedPackets = queue.QueuedPackets,
            queuedBytes = queue.QueuedBytes,
            incomingBytes = queue.IncomingBytes,
            maximumQueuedPackets = queue.MaximumQueuedPackets,
            maximumQueuedBytes = queue.MaximumQueuedBytes
        } : null,
        serverProtocol = protocol
    };

    internal static async Task ReceiveAsync(WebSocket socket, SemaphoreSlim acknowledged, CancellationTokenSource lifetime,
        Func<bool> allowed)
    {
        var bytes = new byte[3];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var pending = socket.ReceiveAsync(bytes, lifetime.Token);
                while (!pending.IsCompleted && !lifetime.IsCancellationRequested && allowed())
                {
                    await Task.WhenAny(pending, Task.Delay(1000, lifetime.Token));
                }
                if (!allowed())
                {
                    await lifetime.CancelAsync();
                }
                var reply = await pending;
                if (reply.MessageType != WebSocketMessageType.Text || !reply.EndOfMessage || reply.Count != 2 ||
                    bytes[0] != 'o' || bytes[1] != 'k')
                {
                    break;
                }
                acknowledged.Release();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or SemaphoreFullException)
        {
        }
        finally
        {
            await lifetime.CancelAsync();
        }
    }
}
