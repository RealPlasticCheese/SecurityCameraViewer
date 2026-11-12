using SecurityCameraViewer.Web.Settings;

namespace SecurityCameraViewer.Web;

/// <summary>Persists playback presets independently of capture settings and active recording workers.</summary>
public sealed class RecordingFilterStore(IAppSettingsStore settingsStore)
{
    public Task<SavedRecordingFilter[]> ListAsync(string configurationId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(settingsStore.Read().Configurations.FirstOrDefault(configuration => configuration.Connection.ConfigurationId == configurationId)?.RecordingFilters ?? []);
    }

    public async Task<SavedRecordingFilter> SaveAsync(string configurationId, SaveRecordingFilterRequest request,
        CancellationToken token = default)
    {
        if (request.Filter is null)
        {
            throw new ArgumentException("Provide a recording filter.");
        }

        if (request.Filter.Validate() is { } error)
        {
            throw new ArgumentException(error);
        }

        SavedRecordingFilter? result = null;
        await settingsStore.UpdateAsync(settings =>
        {
            var configuration = settings.Configurations.FirstOrDefault(configuration => configuration.Connection.ConfigurationId == configurationId)
                ?? throw new ArgumentException("Unknown camera configuration.");
            var filters = configuration.RecordingFilters;
            var existing = request.Id is null ? null : filters.FirstOrDefault(filter => filter.Id == request.Id)
                ?? throw new ArgumentException("The saved filter no longer exists. Choose another filter or create a new one.");
            var name = existing?.Name ?? request.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
            {
                throw new ArgumentException("Enter a filter name of 1–80 characters.");
            }

            if (existing is null && filters.Any(filter => string.Equals(filter.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("A filter with that name already exists. Select it to replace it.");
            }

            if (existing is null && filters.Length >= 100)
            {
                throw new ArgumentException("Up to 100 filters can be saved. Select an existing filter to replace it.");
            }

            var saved = new SavedRecordingFilter(existing?.Id ?? Guid.NewGuid().ToString("N"), name, request.Filter);
            result = saved;
            return settings with
            {
                Configurations = settings.Configurations.Select(item => item.Connection.ConfigurationId == configurationId
                    ? item with
                    {
                        RecordingFilters = [.. filters.Where(filter => filter.Id != saved.Id), saved]
                    } : item).ToArray()
            };
        }, token);
        return result!;
    }
}
