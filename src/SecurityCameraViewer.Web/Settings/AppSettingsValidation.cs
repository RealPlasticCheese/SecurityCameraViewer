using SecurityCameraViewer.Web.Nvr;
using SecurityCameraViewer.Web.Streaming;

namespace SecurityCameraViewer.Web.Settings;

public static class AppSettingsValidation
{
    public static void Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.LiveRewind is null || settings.LiveRewind.Validate() is not null)
        {
            throw new ArgumentException("Live rewind settings are invalid.");
        }
        if (settings.Version != 1 || settings.Configurations is null ||
            settings.Configurations.Length is < 1 or > 32 ||
            settings.Configurations.Any(configuration => configuration?.Connection is null) ||
            settings.Configurations.DistinctBy(configuration => configuration.Connection.ConfigurationId).Count() != settings.Configurations.Length ||
            !settings.Configurations.Any(configuration => configuration.Connection.ConfigurationId == settings.ActiveConfigurationId))
        {
            throw new ArgumentException("Choose an active configuration and up to 32 valid configurations with unique IDs.");
        }

        foreach (var configuration in settings.Configurations)
        {
            if (configuration.Connection.Validate() is { } error)
            {
                throw new ArgumentException(error);
            }

            ValidatePassword(configuration.Password);
            if (configuration.Credentials is null ||
                configuration.Credentials.Any(entry => !Guid.TryParseExact(entry.Key, "N", out _) || entry.Value is null ||
                    entry.Value.Password is null || entry.Value.Password.Length > 4096 ||
                    entry.Value.OnvifUsername?.Length > 256 || entry.Value.OnvifPassword?.Length > 4096))
            {
                throw new ArgumentException("Camera credentials are invalid.");
            }

            if (configuration.Recording is { } recording &&
                (recording.Validate() is not null || recording.ConfigurationId != configuration.Connection.ConfigurationId))
            {
                throw new ArgumentException("Recording settings must be valid and belong to their camera configuration.");
            }

            ValidateFilters(configuration.RecordingFilters);
            var cameraIds = configuration.Connection.WithSources().Sources.Select(camera => camera.Id).ToHashSet();
            if (configuration.Enhancements is null || configuration.Enhancements.Any(entry =>
                !cameraIds.Contains(entry.Key) || entry.Value is null || entry.Value.Validate() is not null))
            {
                throw new ArgumentException("Camera enhancement settings must be valid and belong to their camera configuration.");
            }
        }

        if (settings.Streaming?.Settings is null || settings.Streaming.Settings.Validate() is not null ||
            settings.Streaming.Password is null || settings.Streaming.Password.Length > 256 ||
            !StreamingAccount.IsSupportedCredential(settings.Streaming.Password) ||
            settings.Streaming.Settings.Enabled && settings.Streaming.Password.Length < 8)
        {
            throw new ArgumentException("Streaming settings or client credentials are invalid.");
        }

        if (settings.Nvr?.Settings is null || settings.Nvr.Settings.Host is null || settings.Nvr.Settings.Username is null)
        {
            throw new ArgumentException("NVR settings are invalid.");
        }

        ValidatePassword(settings.Nvr.Password, 255);
        // An untouched installation has no NVR connection configured yet.
        if (settings.Nvr.Settings.Host.Length > 0 || settings.Nvr.Password.Length > 0)
        {
            settings.Nvr.Settings.Validate();
        }
        else if (settings.Nvr.Settings != new NvrSettings())
        {
            throw new ArgumentException("An unconfigured NVR must use the default connection settings.");
        }

        var integrations = settings.Integrations;
        if (integrations is null || integrations.Tokens?.Length > 64 || integrations.Webhooks?.Length > 32 ||
            integrations.Tokens?.Any(token => token is null || string.IsNullOrEmpty(token.Id) ||
                token.Hash is null || token.Hash.Length != 64 || !token.Hash.All(char.IsAsciiHexDigit) ||
                token.Scopes is null || token.Scopes.Length == 0 || token.Scopes.Any(scope => !IntegrationSettings.Scopes.Contains(scope))) == true ||
            integrations.Webhooks?.Any(webhook => webhook is null || !Guid.TryParseExact(webhook.Id, "N", out _) ||
                !Uri.TryCreate(webhook.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                webhook.Secret is null || webhook.Events is null || webhook.CameraIds is null || webhook.ZoneIds is null) == true ||
            integrations.Tokens?.DistinctBy(token => token.Id).Count() != integrations.Tokens?.Length ||
            integrations.Webhooks?.DistinctBy(webhook => webhook.Id).Count() != integrations.Webhooks?.Length)
        {
            throw new ArgumentException("Integration settings are invalid.");
        }
    }

    public static void ValidateFilters(SavedRecordingFilter[] filters)
    {
        if (filters is null || filters.Length > 100 ||
            filters.Any(filter => filter is null || !Guid.TryParseExact(filter.Id, "N", out _) ||
                string.IsNullOrWhiteSpace(filter.Name) || filter.Name.Length > 80 ||
                filter.Filter is null || filter.Filter.Validate() is not null) ||
            filters.DistinctBy(filter => filter.Id).Count() != filters.Length ||
            filters.DistinctBy(filter => filter.Name, StringComparer.OrdinalIgnoreCase).Count() != filters.Length)
        {
            throw new ArgumentException("Saved recording filters are invalid.");
        }
    }

    private static void ValidatePassword(string password, int maximumLength = 4096)
    {
        if (password is null || password.Length > maximumLength)
        {
            throw new ArgumentException("Password exceeds the supported length.");
        }
    }
}
