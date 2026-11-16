using System.Text.Json;

namespace SecurityCameraViewer.Web.Settings;

internal static class AppSettingsBackup
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static byte[] Create(AppSettings settings, bool includePasswords) =>
        JsonSerializer.SerializeToUtf8Bytes(includePasswords ? settings : WithoutPasswords(settings), JsonOptions);

    private static AppSettings WithoutPasswords(AppSettings settings) => settings with
    {
        Configurations = settings.Configurations.Select(configuration => configuration with
        {
            Password = "",
            Credentials = configuration.Credentials.ToDictionary(entry => entry.Key, entry => entry.Value with
            {
                Password = "",
                OnvifPassword = null
            })
        }).ToArray(),
        Nvr = settings.Nvr with { Password = "" },
        Streaming = settings.Streaming with
        {
            Password = "",
            Settings = settings.Streaming.Settings with { Enabled = false }
        },
        Integrations = settings.Integrations with
        {
            Webhooks = settings.Integrations.Webhooks?.Select(webhook => webhook with
            {
                Secret = "",
                Enabled = false
            }).ToArray()
        }
    };
}
