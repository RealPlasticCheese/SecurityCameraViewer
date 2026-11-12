using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web.Nvr;
using SecurityCameraViewer.Web.Streaming;

namespace SecurityCameraViewer.Web.Settings;

/// <summary>Private persistence model. Never return this document from a browser API.</summary>
public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public string ActiveConfigurationId { get; init; } = "default";
    public CameraConfigurationSettings[] Configurations { get; init; } = [new()];
    public StreamingSettingsState Streaming { get; init; } = new();
    public NvrSettingsState Nvr { get; init; } = new();
    public IntegrationConfiguration Integrations { get; init; } = new();
    public LiveRewindSettings LiveRewind { get; init; } = new();

    public override string ToString() => "Application settings (private)";
}

public sealed record CameraConfigurationSettings
{
    public ViewerSettings Connection { get; init; } = new();
    public string Password { get; init; } = "";
    public Dictionary<string, CameraCredentials> Credentials { get; init; } = [];
    public RecordingSettings? Recording { get; init; }
    public SavedRecordingFilter[] RecordingFilters { get; init; } = [];
    public Dictionary<string, CameraEnhancementSettings> Enhancements { get; init; } = [];

    public ViewerSettings PlaybackSettings() => Connection.Normalize() with
    {
        Credentials = Credentials
    };

    public override string ToString() => "Camera configuration (private)";
}

public sealed record StreamingSettingsState
{
    public StreamingConfiguration Settings { get; init; } = new();
    public string Password { get; init; } = "";

    public override string ToString() => "Streaming settings (private)";
}

public sealed record NvrSettingsState
{
    public NvrSettings Settings { get; init; } = new();
    public string Password { get; init; } = "";

    public override string ToString() => "NVR settings (private)";
}
