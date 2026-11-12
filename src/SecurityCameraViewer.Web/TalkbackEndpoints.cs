using System.Net.WebSockets;
using System.Text.Json;

namespace SecurityCameraViewer.Web;

public static class TalkbackEndpoints
{
    public static void MapTalkbackEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cameras/{channel:int}/talkback", async (int channel, HttpContext context,
            ViewerService viewer, IntegrationAuth auth) =>
        {
            var capability = await viewer.TalkbackCapabilityAsync(channel, context.RequestAborted);
            return Results.Ok(new
            {
                capability.Supported,
                capability.Status,
                capability.Message,
                capability.Codec,
                canTalk = auth.Allowed(context, "control")
            });
        });
        app.MapMethods("/api/cameras/{channel:int}/talk", [HttpMethods.Get, HttpMethods.Connect],
            async (int channel, HttpContext context, ViewerService viewer, IntegrationAuth auth) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                // Reserve exclusively before accepting the upgrade. Other speakers receive HTTP 409.
                await using var speaker = await viewer.ReserveTalkbackAsync(channel, context.RequestAborted);
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,
                    speaker.Token, app.Lifetime.ApplicationStopping);
                var audit = AuditAsync(auth, context, lifetime);
                Task keepAlive = Task.CompletedTask;
                try
                {
                    var session = await speaker.Session.WaitAsync(lifetime.Token);
                    keepAlive = KeepAliveAsync(session, lifetime);
                    await SendAsync(socket, new
                    {
                        type = "ready",
                        sampleRate = 8000,
                        channels = 1,
                        frameSamples = 160,
                        encoding = "pcm_s16le",
                        codec = session.Codec
                    }, lifetime.Token);
                    var frame = new byte[320];
                    var first = true;
                    while (!lifetime.IsCancellationRequested && auth.Allowed(context, "control"))
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        deadline.CancelAfter(TimeSpan.FromMilliseconds(first ? 2000 : 750));
                        var offset = 0;
                        ValueWebSocketReceiveResult received;
                        do
                        {
                            received = await socket.ReceiveAsync(frame.AsMemory(offset), deadline.Token);
                            if (received.MessageType == WebSocketMessageType.Close)
                            {
                                return;
                            }
                            if (received.MessageType != WebSocketMessageType.Binary || received.Count == 0 ||
                                received.Count + offset > frame.Length || received.Count + offset == frame.Length && !received.EndOfMessage)
                            {
                                throw new InvalidDataException("Invalid talkback frame.");
                            }
                            offset += received.Count;
                        }
                        while (!received.EndOfMessage);
                        if (offset != frame.Length)
                        {
                            throw new InvalidDataException("Invalid talkback frame size.");
                        }
                        if (!auth.Allowed(context, "control"))
                        {
                            break;
                        }
                        await session.SendAsync(frame, deadline.Token);
                        first = false;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Sockets.SocketException or ArgumentException or FormatException)
                {
                    // RTSP data can contain credentials. Never return exception/device diagnostics.
                    try
                    {
                        await SendAsync(socket, new
                        {
                            type = "error",
                            message = "Camera talkback could not continue. Release and try again."
                        }, lifetime.Token);
                    }
                    catch (Exception sendException) when (sendException is OperationCanceledException or WebSocketException)
                    {
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
                {
                }
                finally
                {
                    await lifetime.CancelAsync();
                    socket.Abort();
                    await Task.WhenAll(audit, keepAlive);
                }
            });
    }

    private static async Task SendAsync(WebSocket socket, object value, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, deadline.Token);
    }

    private static async Task AuditAsync(IntegrationAuth auth, HttpContext context, CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested && auth.Allowed(context, "control"))
            {
                await Task.Delay(250, lifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await lifetime.CancelAsync();
        }
    }

    private static async Task KeepAliveAsync(SecurityCameraViewer.Core.ITalkbackSession session, CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(1000, lifetime.Token);
                await session.KeepAliveAsync(lifetime.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException or FormatException)
        {
        }
        finally
        {
            await lifetime.CancelAsync();
        }
    }
}
