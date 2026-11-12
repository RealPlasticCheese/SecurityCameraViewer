using SecurityCameraViewer.Nvr;
using SecurityCameraViewer.Web.Settings;

namespace SecurityCameraViewer.Web.Nvr;

public sealed record NvrSettings
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 9000;
    public string Username { get; init; } = "admin";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Host.Any(char.IsWhiteSpace) || Host.Contains('/') ||
            Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(Username) || Username.Length > 255)
        {
            throw new ArgumentException("Enter a host, port, and username.");
        }
    }
}

public sealed record NvrSettingsRequest(NvrSettings Settings, string? Password)
{
    public override string ToString() => "NVR settings request";
}

public sealed class NvrSettingsStore(IAppSettingsStore settingsStore)
{
    public NvrSettings Current => settingsStore.Read().Nvr.Settings;
    public string Notice => "";
    public bool HasPassword => settingsStore.Read().Nvr.Password.Length > 0;

    public void Save(NvrSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Settings);
        request.Settings.Validate();
        settingsStore.Update(settings =>
        {
            var previous = settings.Nvr;
            var sameRecorder = request.Settings == previous.Settings;
            return settings with
            {
                Nvr = new()
                {
                    Settings = request.Settings,
                    Password = request.Password ?? (sameRecorder ? previous.Password : "")
                }
            };
        });
    }

    public NvrConnection GetConnection()
    {
        var nvr = settingsStore.Read().Nvr;
        nvr.Settings.Validate();
        if (nvr.Password.Length == 0)
        {
            throw new NvrException("Enter and save an NVR password before connecting.");
        }

        return new(nvr.Settings.Host, nvr.Settings.Port, nvr.Settings.Username, nvr.Password);
    }
}
