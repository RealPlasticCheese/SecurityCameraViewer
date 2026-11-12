using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web.Settings;

namespace SecurityCameraViewer.Web;

/// <summary>Accesses recording settings for the selected camera configuration.</summary>
public sealed class RecordingSettingsStore(IAppSettingsStore settingsStore)
{
    private string _configurationId = "default";
    public string Notice => "";

    public void SelectConfiguration(string id) => _configurationId = id;

    public RecordingSettings Load() => settingsStore.Read().Configurations
        .FirstOrDefault(configuration => configuration.Connection.ConfigurationId == _configurationId)?.Recording ?? new()
        {
            ConfigurationId = _configurationId,
            Directory = Path.Combine(Path.GetDirectoryName(settingsStore.FilePath)!,
                _configurationId == "default" ? "recordings" : "recordings-" + _configurationId)
        };

    public Task SaveAsync(RecordingSettings recording, CancellationToken token) =>
        settingsStore.UpdateAsync(settings =>
        {
            if (!settings.Configurations.Any(configuration => configuration.Connection.ConfigurationId == recording.ConfigurationId))
            {
                throw new ArgumentException("Unknown camera configuration.");
            }

            return settings with
            {
                Configurations = settings.Configurations.Select(configuration => configuration.Connection.ConfigurationId == recording.ConfigurationId
                    ? configuration with
                    {
                        Recording = recording
                    } : configuration).ToArray()
            };
        }, token);
}
