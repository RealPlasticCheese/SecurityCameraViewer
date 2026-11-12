using System.Text.Json;
using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

public sealed record TokenRequest(string Name, string[] Scopes, DateTimeOffset? ExpiresAt);
public sealed record LoginRequest(string Token);
public sealed record WebhookRequest(WebhookSubscription Subscription, bool RotateSecret = false);

public static class IntegrationEndpoints
{
    private static object TokenInfo(ApiToken t) => new { t.Id, t.Name, t.Scopes, t.CreatedAt, t.ExpiresAt };
    private static object HookInfo(WebhookSubscription h) => new { h.Id, h.Name, h.Url, h.Enabled, h.Events, h.CameraIds, h.ZoneIds, h.CooldownSeconds };
    public static void MapIntegrationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/auth/status", (HttpContext context, IntegrationSettings settings, IntegrationAuth auth) =>
            new
            {
                enabled = settings.Current.Enabled,
                signedIn = auth.Identity(context) is not null,
                scopes = auth.Identity(context)?.Scopes ?? []
            });
        app.MapPost("/api/auth/session", (LoginRequest request, HttpContext context, IntegrationSettings settings, IntegrationAuth auth) =>
        {
            if (request.Token is null || request.Token.Length > 256 || settings.Authenticate(request.Token) is not { } token)
            {
                return Results.Unauthorized();
            }

            auth.SignIn(context, token);
            return Results.Ok();
        });
        app.MapDelete("/api/auth/session", (HttpContext context, IntegrationAuth auth) => { auth.SignOut(context); return Results.Ok(); });
        app.MapPost("/api/integrations/setup", (HttpContext context, IntegrationSettings settings, IntegrationAuth auth) =>
        {
            var (Token, Value) = settings.CreateToken("Browser administrator", ["admin"], null, setup: true);
            auth.SignIn(context, Token);
            return Results.Ok(new
            {
                token = Value,
                message = "Save this administrator token. It is shown only once."
            });
        });
        app.MapGet("/api/integrations", (IntegrationSettings settings, WebhookDispatcher dispatcher, IntegrationEventHub hub) => new
        {
            enabled = settings.Current.Enabled,
            tokens = (settings.Current.Tokens ?? []).Select(TokenInfo),
            webhooks = (settings.Current.Webhooks ?? []).Select(HookInfo),
            eventTypes = IntegrationSettings.EventTypes,
            scopes = IntegrationSettings.Scopes,
            dispatcher.PendingCount,
            dispatcher.FailedCount,
            dispatcher.Notice,
            droppedEvents = hub.Dropped
        });
        app.MapPost("/api/integrations/tokens", (TokenRequest request, IntegrationSettings settings) =>
        {
            var (Token, Value) = settings.CreateToken(request.Name, request.Scopes, request.ExpiresAt);
            return Results.Ok(new
            {
                token = Value,
                details = TokenInfo(Token)
            });
        });
        app.MapDelete("/api/integrations/tokens/{id}", (string id, IntegrationSettings settings) => { settings.Revoke(id); return Results.Ok(); });
        app.MapPut("/api/integrations/webhooks", (WebhookRequest request, IntegrationSettings settings) =>
        {
            var hook = settings.SaveWebhook(request.Subscription, request.RotateSecret);
            return Results.Ok(new
            {
                subscription = HookInfo(hook),
                secret = hook.Secret
            });
        });
        app.MapDelete("/api/integrations/webhooks/{id}", (string id, IntegrationSettings settings) => { settings.DeleteWebhook(id); return Results.Ok(); });
        app.MapPost("/api/integrations/webhooks/{id}/test", async (string id, IntegrationSettings settings, WebhookDispatcher dispatcher, CancellationToken token) =>
        {
            var hook = settings.Current.Webhooks?.FirstOrDefault(h => h.Id == id);
            if (hook is null)
            {
                return Results.NotFound();
            }

            if (!hook.Enabled)
            {
                return Results.Conflict(new
                {
                    message = "Enable the webhook before testing it."
                });
            }

            var deliveryId = await dispatcher.Enqueue(hook, new("system.test", Data: new
            {
                message = "Security Camera Viewer test delivery"
            }), token, test: true);
            return deliveryId is null ? Results.Conflict(new
            {
                message = "Delivery queue is unavailable or full."
            }) : Results.Accepted(value: new
            {
                deliveryId
            });
        });
        app.MapGet("/api/integrations/deliveries", (WebhookDispatcher dispatcher) => dispatcher.List().Select(d => new
        {
            d.Id,
            d.SubscriptionId,
            d.EventId,
            d.EventType,
            d.CreatedAt,
            d.NextAttempt,
            d.Attempts,
            d.Status,
            d.HttpStatus,
            d.Error
        }));
        app.MapPost("/api/integrations/deliveries/{id}/retry", async (string id, WebhookDispatcher dispatcher, CancellationToken token) => await dispatcher.Update(id, true, token) ? Results.Ok() : Results.NotFound());
        app.MapDelete("/api/integrations/deliveries/{id}", async (string id, WebhookDispatcher dispatcher, CancellationToken token) => await dispatcher.Update(id, false, token) ? Results.Ok() : Results.NotFound());

        var api = app.MapGroup("/api/v1");
        api.MapGet("/cameras", (RecordingService recording, ViewerService viewer) => recording.Connection.Channels.Select(channel => Camera(channel, recording, viewer)));
        api.MapGet("/cameras/{id}", (string id, RecordingService recording, ViewerService viewer) => ValidCamera(id, recording) is var c && c > 0 ? Results.Ok(Camera(c, recording, viewer)) : Results.NotFound());
        api.MapGet("/cameras/{id}/zones", (string id, RecordingService recording) =>
        {
            var c = ValidCamera(id, recording);
            if (c == 0)
            {
                return Results.NotFound();
            }

            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(recording.Settings.TimeZoneId)).DateTime;
            return Results.Ok(new
            {
                timeZoneId = recording.Settings.TimeZoneId,
                arming = recording.Arming(c),
                zones = recording.Settings.Camera(c).Zones.Select(z => new
                {
                    z.Id,
                    z.Name,
                    z.Exclude,
                    z.X,
                    z.Y,
                    z.Width,
                    z.Height,
                    z.Points,
                    z.Times,
                    scheduleActive = z.IsActive(local),
                    active = recording.Arming(c).EffectiveMode != "disarmed" && (recording.Arming(c).EffectiveMode == "armed" || z.IsActive(local))
                })
            });
        });
        api.MapGet("/cameras/{id}/snapshot", async (string id, RecordingService recording, ViewerService viewer, CancellationToken token) =>
            viewer.Cameras.FirstOrDefault(c => c.State.Channel == ValidCamera(id, recording)) is { } camera &&
                await camera.SnapshotAsync(token) is { } frame
                ? Results.Bytes(frame.Jpeg, "image/jpeg") : Results.NotFound());
        api.MapGet("/cameras/{id}/arming", (string id, RecordingService recording) => ValidCamera(id, recording) is var c && c > 0 ? Results.Ok(recording.Arming(c)) : Results.NotFound());
        api.MapPut("/cameras/{id}/arming", async (string id, ArmingOverride value, RecordingService recording, CancellationToken token) =>
        {
            var channel = ValidCamera(id, recording);
            if (channel == 0)
            {
                return Results.NotFound();
            }

            await recording.SetArmingAsync(channel, value, token);
            return Results.Ok(recording.Arming(channel));
        });
        api.MapPost("/recordings/trigger", async (TriggerRequest request, HttpContext context, RecordingTriggers triggers, IntegrationAuth auth, CancellationToken token) =>
        {
            if (request.OverrideSchedule && !auth.Allowed(context, "control"))
            {
                return Results.Json(new
                {
                    message = "Bypassing schedules or disarming requires the control scope."
                }, statusCode: 403);
            }

            var receipt = await triggers.Trigger(request, context.Request.Headers["Idempotency-Key"].ToString(), auth.Identity(context)!.Id, token);
            return Results.Accepted($"/api/v1/recording-requests/{receipt.Id}", receipt);
        });
        api.MapGet("/recording-requests/{id}", (string id, RecordingTriggers triggers, RecordingService recording) =>
        {
            var receipt = triggers.Find(id);
            return receipt is null ? Results.NotFound() : Results.Ok(new
            {
                receipt.Id,
                receipt.CameraId,
                receipt.CreatedAt,
                receipt.Status,
                recordings = recording.Store.FindByRequest(id).Select(RecordingInfo)
            });
        });
        api.MapGet("/recordings", (string? cameraId, DateTimeOffset? before, int? limit, RecordingService recording) =>
        {
            if (cameraId is not null && !RecordingTriggers.IsCameraId(cameraId))
            {
                return Results.BadRequest(new
                {
                    message = "Invalid camera ID."
                });
            }

            return Results.Ok(recording.Store.ListByCameraId(cameraId, before, limit ?? 50).Select(RecordingInfo));
        });
        api.MapGet("/recordings/{id}", (string id, RecordingService recording) => recording.Store.Find(id) is { } value ? Results.Ok(RecordingInfo(value)) : Results.NotFound());
        api.MapGet("/recordings/{id}/video", (string id, bool? download, RecordingService recording) => RecordingEndpoints.Media(recording.Store, id, false, download == true));
        api.MapGet("/recordings/{id}/thumbnail", (string id, RecordingService recording) => RecordingEndpoints.Media(recording.Store, id, true, false));
        api.MapGet("/recordings/{id}/markers", (string id, RecordingService recording) =>
            recording.Store.Find(id) is not null ? Results.Ok(recording.Store.Markers(id)) : Results.NotFound());
        api.MapGet("/recordings/timeline", (string cameraId, DateTimeOffset since, DateTimeOffset before, RecordingService recording) =>
        {
            if (!RecordingTriggers.IsCameraId(cameraId))
            {
                return Results.BadRequest(new
                {
                    message = "Invalid camera ID."
                });
            }

            var timeline = recording.Store.TimelineByCameraId(cameraId, since, before);
            return Results.Ok(new
            {
                recordings = timeline.Recordings.Select(RecordingInfo),
                timeline.Truncated
            });
        });
        api.MapPut("/recordings/{id}/protection", async (string id, ProtectionRequest request, RecordingService recording, CancellationToken token) =>
            await recording.Store.ProtectAsync(id, request.Protected, token) ? Results.Ok() : Results.NotFound());
        api.MapDelete("/recordings/{id}", async (string id, RecordingService recording, CancellationToken token) =>
            await recording.Store.DeleteAsync(id, token) ? Results.NoContent() : Results.Conflict(new
            {
                message = "Recording is protected, active, finalizing or missing."
            }));
        api.MapGet("/settings/recording", (RecordingService recording) => recording.Settings);
        api.MapPut("/settings/recording", async (RecordingSettings settings, RecordingService recording, IAiModelRegistry models, CancellationToken token) =>
        {
            if (settings.Validate(models) is { } error)
            {
                return Results.BadRequest(new
                {
                    message = error
                });
            }

            await recording.ConfigureAsync(settings, token);
            return Results.Ok(recording.Settings);
        });
        api.MapGet("/detections", (RecordingService recording, CancellationToken token, DateTimeOffset? since) => recording.DetectionSnapshotAsync(token, since));
        api.MapGet("/health", (RecordingService recording, WebhookDispatcher dispatcher, IntegrationEventHub hub) => new
        {
            timestamp = DateTimeOffset.UtcNow,
            recordingEnabled = recording.Settings.Enabled,
            recording.Store.Notice,
            storage = new
            {
                freeBytes = recording.FreeBytes(),
                usedBytes = recording.Store.UsedBytes,
                limitBytes = recording.Settings.MaximumStorageGigabytes * 1_000_000_000
            },
            cameras = recording.Cameras.Select(c => new
            {
                cameraId = recording.Connection.CameraId(c.State.Channel),
                c.State.Status,
                c.State.Message,
                preEventSeconds = Math.Min(c.State.BufferSeconds, recording.Settings.PreCaptureSeconds),
                requestedPreEventSeconds = recording.Settings.PreCaptureSeconds,
                reducedCoverage = c.State.BufferSeconds < recording.Settings.PreCaptureSeconds
            }),
            failedRecordings = recording.Store.List(null, null, 100).Where(e => e.Status == "failed").Select(e => e.Id),
            webhooks = new
            {
                dispatcher.PendingCount,
                dispatcher.FailedCount,
                dispatcher.Notice,
                droppedEvents = hub.Dropped
            }
        });
        api.MapGet("/events", StreamEvents);
        api.MapGet("/openapi.json", () => Results.File(Path.Combine(AppContext.BaseDirectory, "IntegrationOpenApi.json"), "application/json"));
    }
    private static int ValidCamera(string id, RecordingService recording) => recording.ChannelForId(id);
    private static object Camera(int channel, RecordingService recording, ViewerService viewer) => new
    {
        id = recording.Connection.CameraId(channel),
        channel,
        name = recording.Connection.CameraName(channel),
        live = viewer.Cameras.FirstOrDefault(c => c.State.Channel == channel)?.State,
        recordingEnabled = recording.Settings.Enabled && recording.Settings.Camera(channel).Enabled,
        recording = recording.Cameras.FirstOrDefault(c => c.State.Channel == channel)?.State,
        arming = recording.Arming(channel)
    };
    private static object RecordingInfo(RecordingEvent e) => new
    {
        e.Id,
        cameraId = e.CameraId,
        e.ConfigurationId,
        e.CameraName,
        e.Status,
        e.Mode,
        e.StartedAt,
        e.TriggeredAt,
        e.EndedAt,
        e.DurationSeconds,
        e.SizeBytes,
        e.Protected,
        e.Interrupted,
        e.Trigger,
        e.AdditionalTriggers,
        videoUrl = e.Status == "ready" ? $"/api/v1/recordings/{e.Id}/video" : null,
        downloadUrl = e.Status is "ready" or "failed" ? $"/api/v1/recordings/{e.Id}/video?download=true" : null,
        thumbnailUrl = e.Status == "ready" ? $"/api/v1/recordings/{e.Id}/thumbnail" : null
    };
    private static async Task StreamEvents(HttpContext context, IntegrationEventHub hub, IntegrationAuth auth, IHostApplicationLifetime lifetime)
    {
        var (Id, Reader) = hub.Subscribe();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            await context.Response.WriteAsync("event: connected\ndata: {\"replay\":false,\"refreshState\":true}\n\n", stop.Token);
            await context.Response.Body.FlushAsync(stop.Token);
            while (!stop.IsCancellationRequested && auth.Allowed(context, "read"))
            {
                using var interval = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                interval.CancelAfter(TimeSpan.FromSeconds(15));
                CameraEvent? value = null;
                try
                {
                    value = await Reader.ReadAsync(interval.Token);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                }
                using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                writeDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                await context.Response.WriteAsync(value is null ? ": keepalive\n\n" : $"id: {value.Id}\nevent: {value.Type}\ndata: {JsonSerializer.Serialize(value, WebhookDispatcher.Json)}\n\n", writeDeadline.Token);
                await context.Response.Body.FlushAsync(writeDeadline.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Threading.Channels.ChannelClosedException)
        {
        }
        finally
        {
            hub.Unsubscribe(Id);
        }
    }
}
