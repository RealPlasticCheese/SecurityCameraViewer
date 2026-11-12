namespace SecurityCameraViewer.Recording;

public static class RecordingModes
{
    public const string Event = "event";
    public const string Continuous = "continuous";
    public const string AvailableVideo = "available-video";

    public static bool IsValid(string mode) => mode is Event or Continuous or AvailableVideo;
}

/// <summary>Decides when automatic capture is admitted, independently of media and storage.</summary>
public interface IRecordingCapturePolicy
{
    bool UsesEventBuffer
    {
        get;
    }
    bool UsesDetection
    {
        get;
    }
    bool IsAllowed(DateTimeOffset timestamp, ArmingOverride arming);
    RecordingTrigger AutomaticTrigger
    {
        get;
    }
}

public static class RecordingCapturePolicy
{
    public static IRecordingCapturePolicy Create(CameraRecordingSettings camera, TimeZoneInfo timeZone) =>
        camera.EffectiveMode == RecordingModes.Event
            ? new EventRecordingPolicy()
            : new ScheduledRecordingPolicy(camera, timeZone);
}

internal sealed class EventRecordingPolicy : IRecordingCapturePolicy
{
    public bool UsesEventBuffer => true;
    public bool UsesDetection => true;
    public RecordingTrigger AutomaticTrigger => new();
    public bool IsAllowed(DateTimeOffset timestamp, ArmingOverride arming) => arming.EffectiveMode != "disarmed";
}

internal sealed class ScheduledRecordingPolicy(CameraRecordingSettings camera, TimeZoneInfo timeZone) : IRecordingCapturePolicy
{
    public bool UsesEventBuffer => false;
    public bool UsesDetection => camera.EffectiveMode == RecordingModes.Continuous && camera.DetectDuringContinuousRecording;
    public RecordingTrigger AutomaticTrigger => camera.EffectiveMode == RecordingModes.Continuous
        ? new("continuous", "Continuous recording")
        : new("stream", "Camera video became available");

    public bool IsAllowed(DateTimeOffset timestamp, ArmingOverride arming)
    {
        var mode = arming.EffectiveMode;
        var local = TimeZoneInfo.ConvertTime(timestamp, timeZone).DateTime;
        return mode == "armed" || mode != "disarmed" &&
            (camera.StreamSchedule.Length == 0 || camera.StreamSchedule.Any(rule => rule.Contains(local)));
    }
}
