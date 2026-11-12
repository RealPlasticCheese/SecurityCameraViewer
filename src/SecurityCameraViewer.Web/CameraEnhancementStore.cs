using SecurityCameraViewer.Web.Settings;

namespace SecurityCameraViewer.Web;

/// <summary>Persists adjustments by stable camera identity within each camera configuration.</summary>
public sealed class CameraEnhancementStore(IAppSettingsStore settingsStore)
{
    public IReadOnlyDictionary<string, CameraEnhancementSettings> List(string configurationId)
    {
        var configuration = settingsStore.Read().Configurations.FirstOrDefault(item => item.Connection.ConfigurationId == configurationId);
        if (configuration is null)
        {
            return new Dictionary<string, CameraEnhancementSettings>();
        }

        return configuration.Connection.WithSources().Sources.ToDictionary(camera => camera.Id,
            camera => configuration.Enhancements.GetValueOrDefault(camera.Id) ?? new());
    }

    public async Task<CameraEnhancementSettings> UpdateAsync(string configurationId, string cameraId,
        CameraEnhancementUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        CameraEnhancementSettings? result = null;
        await settingsStore.UpdateAsync(settings =>
        {
            var configuration = FindConfiguration(settings, configurationId);
            if (!configuration.Connection.WithSources().Sources.Any(camera => camera.Id == cameraId))
            {
                throw new ArgumentException("Unknown camera.");
            }

            var next = update.Apply(configuration.Enhancements.GetValueOrDefault(cameraId) ?? new());
            if (next.Validate() is { } error)
            {
                throw new ArgumentException(error);
            }

            var enhancements = new Dictionary<string, CameraEnhancementSettings>(configuration.Enhancements)
            {
                [cameraId] = next
            };
            result = next;
            return settings with
            {
                Configurations = settings.Configurations.Select(item => item.Connection.ConfigurationId == configurationId
                    ? item with
                    {
                        Enhancements = enhancements
                    } : item).ToArray()
            };
        }, cancellationToken);
        return result!;
    }

    private static CameraConfigurationSettings FindConfiguration(AppSettings settings, string configurationId) =>
        settings.Configurations.FirstOrDefault(item => item.Connection.ConfigurationId == configurationId)
        ?? throw new ArgumentException("Unknown camera configuration.");
}
