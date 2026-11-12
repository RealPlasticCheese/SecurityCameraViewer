using SecurityCameraViewer.Core;
using SecurityCameraViewer.Web.Settings;

namespace SecurityCameraViewer.Web;

/// <summary>Provides camera configuration operations over the shared settings document.</summary>
public sealed class WebSettingsStore(IAppSettingsStore settingsStore)
{
    public IAppSettingsStore SettingsStore => settingsStore;
    public string FilePath => settingsStore.FilePath;

    public ConfigurationCatalog LoadCatalog()
    {
        var settings = settingsStore.Read();
        return new()
        {
            ActiveId = settings.ActiveConfigurationId,
            Configurations = settings.Configurations.Select(configuration => configuration.PlaybackSettings()).ToArray()
        };
    }

    public IReadOnlyDictionary<string, CameraCredentials> LoadCredentials(string configurationId) =>
        settingsStore.Read().Configurations.FirstOrDefault(configuration => configuration.Connection.ConfigurationId == configurationId)?.Credentials
        ?? new Dictionary<string, CameraCredentials>();

    public string LoadPassword(string configurationId) =>
        settingsStore.Read().Configurations.FirstOrDefault(configuration => configuration.Connection.ConfigurationId == configurationId)?.Password ?? "";

    public (ViewerSettings Settings, string Password, string Notice) Load(bool demo)
    {
        if (demo)
        {
            return (new()
            {
                Host = "127.0.0.1",
                RtspPort = 18554,
                Username = "viewer"
            },
                "local-only-test", "TEST VIDEO \u2014 local test pattern, not your cameras.");
        }

        var settings = settingsStore.Read();
        var active = settings.Configurations.Single(configuration => configuration.Connection.ConfigurationId == settings.ActiveConfigurationId);
        return (active.PlaybackSettings(), active.Password, File.Exists(FilePath)
            ? "Saved web connection loaded." : "Open Connection to configure your recorder.");
    }

    public Task SaveConfigurationAsync(ViewerSettings connection, string password, CancellationToken cancellationToken = default) =>
        settingsStore.UpdateAsync(settings =>
        {
            var previous = settings.Configurations.FirstOrDefault(configuration => configuration.Connection.ConfigurationId == connection.ConfigurationId) ?? new();
            var cameras = connection.WithSources().Sources;
            var cameraIds = cameras.Select(camera => camera.Id).ToHashSet();
            var next = previous with
            {
                Connection = connection,
                Password = password,
                Enhancements = previous.Enhancements.Where(entry => cameraIds.Contains(entry.Key))
                    .ToDictionary(entry => entry.Key, entry => entry.Value),
                Credentials = cameras.Where(camera => connection.Credentials.ContainsKey(camera.Id))
                    .ToDictionary(camera => camera.Id, camera => connection.Credentials[camera.Id])
            };
            return settings with
            {
                ActiveConfigurationId = connection.ConfigurationId,
                Configurations = [.. settings.Configurations.Where(configuration => configuration.Connection.ConfigurationId != connection.ConfigurationId), next]
            };
        }, cancellationToken);

    public Task DeleteConfigurationAsync(string id, CancellationToken cancellationToken = default) =>
        settingsStore.UpdateAsync(settings =>
        {
            if (settings.ActiveConfigurationId == id)
            {
                throw new InvalidOperationException("Select another configuration before deleting the active one.");
            }

            return settings with
            {
                Configurations = settings.Configurations.Where(configuration => configuration.Connection.ConfigurationId != id).ToArray()
            };
        }, cancellationToken);
}
