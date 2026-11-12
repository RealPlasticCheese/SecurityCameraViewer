using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace SecurityCameraViewer.Web;

public static class LiveMediaEndpoints
{
    public static void MapLiveMediaEndpoints(this WebApplication app)
    {
        app.MapMethods("/api/cameras/{channel:int}/live", [HttpMethods.Get, HttpMethods.Connect],
            async (int channel, bool? compatibility, HttpContext context, ViewerService viewer, IntegrationAuth auth) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                await using var subscription = await viewer.SubscribeLiveAsync(channel, context.RequestAborted,
                    compatibility: compatibility == true);
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,
                    app.Lifetime.ApplicationStopping);
                var receive = ObserveCloseAsync(socket, lifetime, context, auth);
                try
                {
                    await foreach (var packet in subscription.ReadAllAsync(lifetime.Token))
                    {
                        if (!auth.Allowed(context, "read") || packet.Type == "fragment" &&
                            Stopwatch.GetElapsedTime(packet.Timestamp).TotalSeconds > 3)
                        {
                            break;
                        }

                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(5));
                        if (packet.Type != "fragment" || packet.ProducedAt is not null)
                        {
                            var metadata = JsonSerializer.SerializeToUtf8Bytes(new
                            {
                                type = packet.Type,
                                streamId = packet.StreamId,
                                startSeconds = packet.StartSeconds,
                                endSeconds = packet.EndSeconds,
                                producedAt = packet.ProducedAt,
                                mimeType = packet.MimeType,
                                hasAudio = packet.HasAudio,
                                status = packet.Status,
                                message = packet.Message
                            });
                            await socket.SendAsync(metadata, WebSocketMessageType.Text, true, deadline.Token);
                        }

                        if (packet.Bytes is not null)
                        {
                            await socket.SendAsync(packet.Bytes, WebSocketMessageType.Binary, true, deadline.Token);
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
                {
                }
                finally
                {
                    if (socket.State == WebSocketState.Open && !lifetime.IsCancellationRequested)
                    {
                        using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        try
                        {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Live session ended.", closing.Token);
                            await Task.WhenAny(receive, Task.Delay(500, closing.Token));
                        }
                        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
                        {
                        }
                    }

                    await lifetime.CancelAsync();
                    socket.Abort();
                    await receive;
                }
            });
    }

    private static async Task ObserveCloseAsync(WebSocket socket, CancellationTokenSource lifetime,
        HttpContext context, IntegrationAuth auth)
    {
        try
        {
            // This endpoint accepts no client commands. Closing or unexpected data ends listening.
            var receive = socket.ReceiveAsync(new byte[64], lifetime.Token);
            while (!receive.IsCompleted && !lifetime.IsCancellationRequested && auth.Allowed(context, "read"))
            {
                await Task.WhenAny(receive, Task.Delay(1000, lifetime.Token));
            }

            await lifetime.CancelAsync();
            await receive;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            await lifetime.CancelAsync();
        }
    }
}
