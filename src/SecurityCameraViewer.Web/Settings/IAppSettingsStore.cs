namespace SecurityCameraViewer.Web.Settings;

/// <summary>Owns the installation's settings document and serializes section updates.</summary>
public interface IAppSettingsStore
{
    string FilePath { get; }
    AppSettings Read();
    void Update(Func<AppSettings, AppSettings> update);
    Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default);
}
