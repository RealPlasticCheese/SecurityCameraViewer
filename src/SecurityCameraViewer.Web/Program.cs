using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.HttpOverrides;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Browser;
using SecurityCameraViewer.Web.Settings;
using SecurityCameraViewer.Nvr;
using SecurityCameraViewer.Nvr.Raysharp;
using SecurityCameraViewer.Web.Nvr;
using SecurityCameraViewer.Web.Onvif;
using SecurityCameraViewer.Web.Streaming;

// Keep publish output runnable from any working directory; dotnet run uses the project webroot.
var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = BrowserLaunchOptions.NormalizeArguments(args),
    WebRootPath = Directory.Exists(webRoot) ? webRoot : null
});
// Installed services keep machine configuration outside the application directory.
var hostConfigPath = builder.Configuration["host-config"];
if (hostConfigPath is not null)
{
    builder.Configuration.AddJsonFile(Path.GetFullPath(hostConfigPath), optional: false, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables("ASPNETCORE_");
    builder.Configuration.AddEnvironmentVariables("DOTNET_");
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(BrowserLaunchOptions.NormalizeArguments(args));
}
builder.Services.AddWindowsService(options => options.ServiceName = "SecurityCameraViewer");
BrowserLaunchOptions browserOptions;
try
{
    browserOptions = BrowserLaunchOptions.Read(builder.Configuration);
}
catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
    return;
}
if (browserOptions.ConnectUrl is not null)
{
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole(console => console.SingleLine = true));
    var launcher = new BrowserLauncher(new BrowserPlatform(), loggerFactory.CreateLogger<BrowserLauncher>());
    Environment.ExitCode = launcher.Launch(browserOptions, BrowserAddress.Parse(browserOptions.ConnectUrl)) ? 0 : 1;
    return;
}
if (builder.Configuration["urls"] is null)
{
    builder.WebHost.UseUrls("http://0.0.0.0:5080");
}

builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65536);
builder.Services.AddSingleton(browserOptions);
builder.Services.AddSingleton<IBrowserPlatform, BrowserPlatform>();
builder.Services.AddSingleton<BrowserLauncher>();
builder.Services.AddHostedService<BrowserLaunchService>();
builder.Services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
builder.Services.AddSingleton<WebSettingsStore>();
builder.Services.AddSingleton<CameraEnhancementStore>();
builder.Services.AddSingleton<RaysharpNvrClient>();
builder.Services.AddSingleton<INvrClient>(services => services.GetRequiredService<RaysharpNvrClient>());
builder.Services.AddSingleton<INvrStorageClient>(services => services.GetRequiredService<RaysharpNvrClient>());
builder.Services.AddSingleton<NvrSettingsStore>();
builder.Services.AddSingleton<NvrExportStore>();
builder.Services.AddSingleton<INvrMediaConverter, NvrMediaConverter>();
builder.Services.AddSingleton<NvrExportService>();
builder.Services.AddHostedService(services => services.GetRequiredService<NvrExportService>());
builder.Services.AddSingleton<BatteryCameraRegistry>();
builder.Services.AddSingleton<RecordingSettingsStore>();
builder.Services.AddSingleton<RecordingFilterStore>();
builder.Services.AddSingleton<IntegrationSettings>();
builder.Services.AddSingleton<IntegrationAuth>();
builder.Services.AddSingleton<IntegrationEventHub>();
builder.Services.AddSingleton<ICameraEventPublisher>(s => s.GetRequiredService<IntegrationEventHub>());
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddHostedService(s => s.GetRequiredService<WebhookDispatcher>());
builder.Services.AddSingleton<RecordingTriggers>();
builder.Services.AddSingleton(services => new FfmpegProcess(services.GetRequiredService<IConfiguration>()["ffmpeg"] ?? "ffmpeg"));
builder.Services.AddSingleton<IFfmpegProcess>(services => services.GetRequiredService<FfmpegProcess>());
var liveMediaOptions = builder.Configuration.GetSection("LiveMedia").Get<LiveMediaOptions>() ?? new();
liveMediaOptions.Validate();
builder.Services.AddSingleton(liveMediaOptions);
builder.Services.AddSingleton<IModelSessionFactory, ModelSessionFactory>();
builder.Services.AddSingleton<IAiModelRegistry>(services => new AiModelRegistry(
    Path.GetFullPath(services.GetRequiredService<IConfiguration>()["ai-model-directory"] ?? Path.Combine(AppContext.BaseDirectory, "models"))));
builder.Services.AddSingleton(services => new AiInferenceService(
    services.GetRequiredService<IAiModelRegistry>(),
    services.GetRequiredService<IModelSessionFactory>()));
builder.Services.AddSingleton<IAiInferenceService>(services => services.GetRequiredService<AiInferenceService>());
builder.Services.AddSingleton<IMotionDetectorFactory, ConfigurableMotionDetectorFactory>();
builder.Services.AddSingleton<FfmpegCameraMediaSource>();
builder.Services.AddSingleton<ILiveMediaSource, FfmpegLiveMediaSource>();
builder.Services.AddSingleton<LiveMediaHub>();
builder.Services.AddSingleton<ITalkbackSource, RtspAudioBackchannel>();
builder.Services.AddSingleton<TalkbackHub>();
builder.Services.AddSingleton<IMotionFrameSource>(services => services.GetRequiredService<FfmpegCameraMediaSource>());
builder.Services.AddSingleton<IEncodedVideoSource>(services => services.GetRequiredService<FfmpegCameraMediaSource>());
builder.Services.AddSingleton<IRecordingClipRenderer, FfmpegRecordingClipRenderer>();
builder.Services.AddSingleton<RewindRecordingExporter>();
builder.Services.AddSingleton<RecordingService>();
builder.Services.AddSingleton<IMotionActionCatalog>(services => services.GetRequiredService<RecordingService>());
builder.Services.AddSingleton<IRecordingConnection>(services => services.GetRequiredService<RecordingService>());
builder.Services.AddHostedService(services => services.GetRequiredService<RecordingService>());
builder.Services.AddSingleton<ViewerService>();
builder.Services.AddHostedService(services => services.GetRequiredService<ViewerService>());
builder.Services.AddStreamingServices();
var app = builder.Build();
if (builder.Configuration.GetValue<bool>("create-admin-token"))
{
    var (Token, Value) = app.Services.GetRequiredService<IntegrationSettings>().CreateToken("Recovery administrator", ["admin"], null);
    Console.WriteLine("Save this administrator token; it will not be shown again:");
    Console.WriteLine(Value);
    return;
}

// A local HTTPS proxy must restore the scheme before origin checks and session cookies.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
forwardedHeadersOptions.KnownProxies.Add(IPAddress.Loopback);
forwardedHeadersOptions.KnownProxies.Add(IPAddress.IPv6Loopback);
app.UseForwardedHeaders(forwardedHeadersOptions);

app.Use(async (context, next) =>
{
    // A large camera zone/schedule document can exceed the small connection-form limit.
    if ((context.Request.Path == "/api/recording" || context.Request.Path == "/api/v1/settings/recording" ||
        context.Request.Path == "/api/recordings/search" || context.Request.Path == "/api/recordings/filters" ||
        context.Request.Path.StartsWithSegments("/api/setup")) && (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)) &&
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
    {
        sizeLimit.MaxRequestBodySize = 4 * 1024 * 1024;
    }

    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' blob:; media-src 'self' blob:; style-src 'self'; script-src 'self' 'wasm-unsafe-eval'; worker-src 'self'; connect-src 'self'; frame-ancestors 'none'";
    // Same-origin browser controls. Bearer clients can omit Origin.
    if (context.Request.Headers.Origin is { Count: > 0 } origins &&
        (!Uri.TryCreate(origins.ToString(), UriKind.Absolute, out var origin) ||
         !string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase) ||
         origin.Scheme != context.Request.Scheme))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    try
    {
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException && !context.Response.HasStarted)
    {
        context.Response.StatusCode = ex is ArgumentException ? 400 : 409;
        await context.Response.WriteAsJsonAsync(new
        {
            message = ex.Message
        });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Could not read or save server data. Check folder permissions and free disk space."
            });
        }
    }
});
app.Use((context, next) => app.Services.GetRequiredService<IntegrationAuth>().Authorize(context, next));
app.UseDefaultFiles();
var staticTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
staticTypes.Mappings[".xz"] = "application/x-xz";
staticTypes.Mappings[".c"] = "text/plain";
staticTypes.Mappings[".sh"] = "text/plain";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = staticTypes
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.MapRecordingEndpoints();
app.MapNvrEndpoints();
app.MapIntegrationEndpoints();
app.MapCameraSetupEndpoints();
app.MapCameraEnhancementEndpoints();
app.MapLiveMediaEndpoints();
app.MapCameraVideoEndpoints();
app.MapTalkbackEndpoints();
app.MapStreamingEndpoints();
app.MapOnvifEndpoints();
app.MapGet("/api/state", (ViewerService viewer) => viewer.Snapshot());
app.MapPost("/api/connect", async (ViewerService viewer) => { await viewer.ConnectAsync(); return Results.Ok(); });
app.MapLiveRewindEndpoints();
app.MapPost("/api/disconnect", async (ViewerService viewer) => { await viewer.DisconnectAsync(); return Results.Ok(); });
app.MapPost("/api/quality", async (ViewerService viewer) => { await viewer.ToggleQualityAsync(); return Results.Ok(); });
app.MapPost("/api/cameras/{channel:int}/check", (int channel, ViewerService viewer) =>
{
    var camera = viewer.Cameras.FirstOrDefault(c => c.State.Channel == channel && c.State.MotionActivated);
    if (camera is null)
    {
        return Results.BadRequest(new
        {
            message = "Connect a motion-activated camera before checking it."
        });
    }

    camera.CheckNow();
    return Results.Ok(new
    {
        message = "Availability check requested. This does not wake the camera."
    });
});
app.MapPost("/api/connection", async (ConnectionRequest request, ViewerService viewer) =>
{
    if (request.Settings is null || request.Settings.Validate() is not null)
    {
        return Results.BadRequest(new
        {
            message = request.Settings?.Validate() ?? "Connection settings are required."
        });
    }

    await viewer.ConfigureAsync(request);
    return Results.Ok();
});
app.MapPost("/api/test", async (ConnectionRequest request, ViewerService viewer, CancellationToken cancellationToken) =>
{
    if (request.Settings is null || request.Settings.Validate() is not null)
    {
        return Results.BadRequest(new
        {
            message = request.Settings?.Validate() ?? "Connection settings are required."
        });
    }

    return Results.Ok(await viewer.ProbeAsync(request, cancellationToken));
});
// HTTP/1.1 WebSockets use GET; HTTP/2 WebSockets use extended CONNECT.
app.MapMethods("/api/video", [HttpMethods.Get, HttpMethods.Connect], async (HttpContext context, ViewerService viewer) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }
    var selected = PreviewChannelSelection.Parse(context.Request.Query.ContainsKey("channels")
        ? context.Request.Query["channels"].ToString() : null);
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var previews = new CameraPreviewSubscription();
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
    var seen = new Dictionary<int, CameraFrame>();
    // Channel, configuration generation, then frame age in milliseconds (little endian).
    var header = new byte[selected is null ? 13 : 16];
    var acknowledgement = new byte[8];
    try
    {
        while (!lifetime.IsCancellationRequested && socket.State == WebSocketState.Open && app.Services.GetRequiredService<IntegrationAuth>().Allowed(context, "read"))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var cameras = viewer.Cameras.Where(camera => selected is null || selected.Contains(camera.State.Channel)).ToArray();
            previews.Update(cameras);
            foreach (var camera in cameras)
            {
                var frame = camera.VideoFrame;
                var channel = camera.State.Channel;
                if (frame is null || ReferenceEquals(frame, seen.GetValueOrDefault(channel)) || selected is null && channel > byte.MaxValue)
                {
                    continue;
                }

                var offset = selected is null ? 1 : 4;
                if (selected is null)
                {
                    header[0] = (byte)channel;
                }
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(header, channel);
                }
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(offset), camera.Generation);
                BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(offset + 4), System.Diagnostics.Stopwatch.GetElapsedTime(frame.Timestamp).TotalMilliseconds);
                await socket.SendAsync(header.AsMemory(), WebSocketMessageType.Binary, false, deadline.Token);
                await socket.SendAsync(frame.Jpeg.AsMemory(), WebSocketMessageType.Binary, true, deadline.Token);
                seen[channel] = frame;
            }
            await socket.SendAsync("tick"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
            // The browser acknowledges after decoding this batch. Slow clients skip frames, never queue them.
            var reply = await socket.ReceiveAsync(acknowledgement.AsMemory(), deadline.Token);
            if (reply.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (!reply.EndOfMessage || reply.MessageType != WebSocketMessageType.Text ||
                reply.Count != 2 || acknowledgement[0] != 'o' || acknowledgement[1] != 'k')
            {
                break;
            }

            await Task.Delay(100, lifetime.Token);
        }
    }
    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
    {
    }
    finally
    {
        socket.Abort();
    }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine("\nSecurity Camera Viewer — web server");
    foreach (var address in app.Urls)
    {
        var uri = new Uri(address);
        if (uri.Host is "0.0.0.0" or "[::]" or "::")
        {
            Console.WriteLine($"  Local: {uri.Scheme}://localhost:{uri.Port}/");
            foreach (var ip in NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Distinct())
            {
                Console.WriteLine($"  LAN:   {uri.Scheme}://{ip}:{uri.Port}/");
            }
        }
        else
        {
            Console.WriteLine($"  Browse: {address}/");
        }
    }
    Console.WriteLine(app.Services.GetRequiredService<IntegrationSettings>().Current.Enabled ? "  API token authentication enabled. Browser sign-in is available in the menu." : "  Trusted LAN mode. Enable Integrations in the menu to require scoped API tokens and browser sign-in.");
    Console.WriteLine("  Press Ctrl+C to stop.\n");
});
await app.RunAsync();
