using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace SecurityCameraViewer.Web.Browser;

internal sealed class BrowserLaunchService(
    BrowserLaunchOptions options,
    BrowserLauncher launcher,
    IServer server,
    IHostApplicationLifetime lifetime,
    ILogger<BrowserLaunchService> logger) : IHostedService, IDisposable
{
    private CancellationTokenRegistration _registration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Enabled)
        {
            _registration = lifetime.ApplicationStarted.Register(OpenViewer);
        }
        return Task.CompletedTask;
    }

    private void OpenViewer()
    {
        if (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return;
        }
        try
        {
            var address = options.Url is not null ? BrowserAddress.Parse(options.Url) :
                BrowserAddress.ForServer(server.Features.Get<IServerAddressesFeature>()?.Addresses ?? []);
            launcher.Launch(options, address);
        }
        catch (ArgumentException exception)
        {
            logger.LogWarning(exception, "Could not determine the browser address. Browse to the server manually or set --browser-url.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _registration.Dispose();
}
