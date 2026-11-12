using System.Text.Json.Serialization;

namespace SecurityCameraViewer.Recording;

public sealed record ZonePoint(double X, double Y)
{
    [JsonIgnore]
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) &&
        X is >= 0 and <= 1 && Y is >= 0 and <= 1;
}

/// <summary>Local weekly range: Sunday=0, inclusive start/exclusive end. Overnight ranges belong to their starting day.</summary>
public sealed record ZoneTimeRange(string Start, string End, int[]? Days = null, bool AllDay = false)
{
    [JsonIgnore]
    public bool IsValid =>
        (Days is null || Days.Length is >= 1 and <= 7 &&
            Days.All(day => day is >= 0 and <= 6) && Days.Distinct().Count() == Days.Length) &&
        (AllDay || Parse(Start, out var start) && Parse(End, out var end) && start != end);

    public bool Contains(DateTime local)
    {
        var day = (int)local.DayOfWeek;
        bool Matches(int value) => Days is null || Days.Contains(value);
        if (AllDay)
        {
            return Matches(day);
        }

        var time = TimeOnly.FromDateTime(local);
        if (!Parse(Start, out var start) || !Parse(End, out var end))
        {
            return false;
        }

        return start < end ? Matches(day) && time >= start && time < end :
            Matches(day) && time >= start || Matches((day + 6) % 7) && time < end;
    }

    private static bool Parse(string value, out TimeOnly time)
    {
        return TimeOnly.TryParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out time);
    }
}

public sealed record MotionZone(
    double X,
    double Y,
    double Width,
    double Height,
    bool Exclude = false,
    ZonePoint[]? Points = null,
    ZoneTimeRange[]? Times = null,
    string Name = "Zone")
{
    public string Id { get; init; } = "";
    public string? DetectionTemplateId
    {
        get; init;
    }
    public string[]? ActionIds
    {
        get; init;
    }
    [JsonIgnore]
    public bool IsValid
    {
        get
        {
            if ((Id != "" && !Guid.TryParseExact(Id, "N", out _)) ||
                string.IsNullOrWhiteSpace(Name) || Name.Length > 80 ||
                Times is not null && (Times.Length > 16 || Times.Any(time => time is null || !time.IsValid)))
            {
                return false;
            }

            if (Points is null)
            {
                return double.IsFinite(X + Y + Width + Height) &&
                    X >= 0 && Y >= 0 && Width > 0 && Height > 0 &&
                    X + Width <= 1.000001 && Y + Height <= 1.000001;
            }

            if (Points.Length is < 3 or > 64 || Points.Any(point => point is null || !point.IsValid))
            {
                return false;
            }

            var twiceArea = Points.Select((point, index) =>
            {
                var next = Points[(index + 1) % Points.Length];
                return point.X * next.Y - next.X * point.Y;
            }).Sum();
            return Math.Abs(twiceArea) > 0.00001;
        }
    }

    public bool IsActive(DateTime localTime) => Times is null or { Length: 0 } || Times.Any(t => t.Contains(localTime));

    public bool Contains(double x, double y)
    {
        if (Points is null)
        {
            return x >= X && x < X + Width && y >= Y && y < Y + Height;
        }

        var inside = false;
        for (int i = 0, j = Points.Length - 1; i < Points.Length; j = i++)
        {
            var a = Points[i];
            var b = Points[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }
}

public sealed record CameraRecordingSettings : DetectionSettings
{
    public CameraRecordingSettings()
    {
    }

    public CameraRecordingSettings(DetectionSettings detection) : base(detection)
    {
    }

    public string? DetectionTemplateId
    {
        get; init;
    }
    public string? CameraId
    {
        get; init;
    }
    public int Channel
    {
        get; init;
    }
    public bool Enabled { get; init; } = true;
    public string Mode { get; init; } = RecordingModes.Event;
    public bool DetectDuringContinuousRecording
    {
        get; init;
    }
    [JsonIgnore]
    public string EffectiveMode => RecordWhenStreaming ? RecordingModes.AvailableVideo : Mode;
    public bool RecordWhenStreaming
    {
        get; init;
    }
    public ZoneTimeRange[] StreamSchedule { get; init; } = [];
    public bool DetectionSubstream { get; init; } = true;
    public MotionZone[] Zones { get; init; } = [];
    public string[] DefaultActionIds { get; init; } = [MotionAction.DefaultRecordingId];
}

public sealed record RecordingSettings
{
    public string ConfigurationId { get; init; } = "default";
    public bool Enabled
    {
        get; init;
    }
    public string Directory { get; init; } = "";
    public string TimeZoneId { get; init; } = TimeZoneInfo.Local.Id;
    public int PreCaptureSeconds { get; init; } = 10;
    public int PostCaptureSeconds { get; init; } = 20;
    public int MaximumEventSeconds { get; init; } = 300;
    public int BufferMegabytesPerCamera { get; init; } = 32;
    public int RetentionDays { get; init; } = 7;
    public double MaximumStorageGigabytes { get; init; } = 100;
    public string? PrimaryLocationId { get; init; }
    public bool RegisterPrimaryReplacement { get; init; }
    public RecordingLocation[] Mirrors { get; init; } = [];
    public CameraRecordingSettings[] Cameras { get; init; } = [];
    public DetectionTemplate[] DetectionTemplates { get; init; } = [];
    public MotionAction[] Actions { get; init; } = [MotionAction.DefaultRecording];

    // Deterministic migration gives pre-existing zones stable IDs before their next settings save.
    public RecordingSettings WithZoneIds(bool migrate = true)
    {
        var cameras = Cameras.Select(camera => camera with
        {
            Zones = [.. camera.Zones.Select((zone, index) =>
            {
                if (zone.Id.Length > 0)
                {
                    return zone;
                }

                var identity = System.Text.Encoding.UTF8.GetBytes($"legacy-zone-{camera.Channel}-{index}");
                var id = migrate
                    ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(identity))[..32]
                    : Guid.NewGuid().ToString("N");
                return zone with
                {
                    Id = id
                };
            })]
        }).ToArray();
        return this with
        {
            Cameras = cameras
        };
    }

    public CameraRecordingSettings Camera(int channel) => Cameras.FirstOrDefault(c => c.Channel == channel) ?? new() { Channel = channel };
    public string? Validate(IAiModelRegistry? models = null)
    {
        if (string.IsNullOrWhiteSpace(Directory) || !Path.IsPathFullyQualified(Directory))
        {
            return "Choose an absolute recording folder on the server.";
        }

        try
        {
            var paths = new List<string> { RecordingLocation.Normalize(Directory) };
            if (Mirrors is null || Mirrors.Any(mirror => mirror is null || string.IsNullOrWhiteSpace(mirror.Directory) ||
                !Path.IsPathFullyQualified(mirror.Directory) || !RecordingLocation.ValidQuota(mirror.MaximumStorageGigabytes)))
            {
                return "Each mirror needs an absolute server folder and a valid storage limit.";
            }
            foreach (var mirror in Mirrors)
            {
                var path = RecordingLocation.Normalize(mirror.Directory);
                if (paths.Any(other => Overlaps(other, path) || Overlaps(path, other)))
                {
                    return "Use separate recording folders. Recording folders cannot contain one another.";
                }
                paths.Add(path);
            }

            static bool Overlaps(string parent, string child) =>
                child.Equals(parent, RecordingLocation.PathComparison) ||
                child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
                    RecordingLocation.PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return "The recording folder is invalid.";
        }
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId ?? "");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return "Enter a valid schedule timezone, such as Australia/Brisbane.";
        }
        if (PreCaptureSeconds is < 0 or > 60 || PostCaptureSeconds is < 1 or > 120)
        {
            return "Pre-event time must be 0–60 seconds and post-event time 1–120 seconds.";
        }

        if (MaximumEventSeconds is < 60 or > 3600 || MaximumEventSeconds <= PreCaptureSeconds + PostCaptureSeconds)
        {
            return "Maximum event length must be 60–3600 seconds and exceed pre/post-event time.";
        }

        if (BufferMegabytesPerCamera is < 8 or > 128)
        {
            return "RAM buffer must be 8–128 MB per camera.";
        }

        if (RetentionDays is < 0 or > 365 || !RecordingLocation.ValidQuota(MaximumStorageGigabytes))
        {
            return "Choose 0–365 retention days (0 disables age-based expiry) and a positive storage limit.";
        }

        if (Cameras is null || Cameras.Any(c => c is null) || Cameras.Select(c => c.Channel).Distinct().Count() != Cameras.Length)
        {
            return "Camera settings must contain unique channels.";
        }

        if (Actions is null || Actions.Length is < 1 or > 64 || Actions.Any(action => action is null) ||
            Actions.Select(action => action.Id).Distinct().Count() != Actions.Length ||
            !Actions.Any(action => action.Id == MotionAction.DefaultRecordingId))
        {
            return "Define up to 64 actions with unique IDs, including the default Record action.";
        }

        foreach (var action in Actions)
        {
            if (action.Validate() is { } error)
            {
                return error;
            }
        }

        if (DetectionTemplates is null || DetectionTemplates.Length > 64 ||
            DetectionTemplates.Any(template => template is null || !template.IsValid) ||
            DetectionTemplates.DistinctBy(template => template.Id).Count() != DetectionTemplates.Length)
        {
            return "Define up to 64 detection templates with unique IDs and valid names.";
        }
        foreach (var template in DetectionTemplates)
        {
            if (template.Settings.Validate(models) is { } templateError)
            {
                return $"{template.Name}: {templateError}";
            }
        }
        foreach (var camera in Cameras)
        {
            if (camera.DetectionTemplateId is not null && !DetectionTemplates.Any(template => template.Id == camera.DetectionTemplateId) ||
                camera.Zones is not null && camera.Zones.Any(zone => zone is not null && zone.DetectionTemplateId is not null &&
                    (zone.Exclude || !DetectionTemplates.Any(template => template.Id == zone.DetectionTemplateId))))
            {
                return "Assign existing detection templates to cameras and inclusion zones. Exclusion zones cannot select a detector.";
            }
            if (!RecordingModes.IsValid(camera.Mode))
            {
                return "Choose Event, Continuous, or Available video recording.";
            }
            var detection = DetectionTemplates.FirstOrDefault(template => template.Id == camera.DetectionTemplateId)?.Settings ?? camera;
            if (detection.Validate(models) is { } detectionError)
            {
                return detectionError;
            }

            if (camera.StreamSchedule is null || camera.StreamSchedule.Length > 16 ||
                camera.StreamSchedule.Any(time => time is null || !time.IsValid))
            {
                return "Use up to 16 valid weekly schedules for continuous or available-video recording.";
            }

            if (camera.Channel < 1 ||
                camera.Zones is null || camera.Zones.Length > 32 ||
                camera.Zones.Any(zone => zone is null || !zone.IsValid) ||
                camera.Zones.Where(zone => zone.Id.Length > 0).Select(zone => zone.Id).Distinct().Count() !=
                    camera.Zones.Count(zone => zone.Id.Length > 0))
            {
                return "Check camera sensitivity, minimum area and detection zones.";
            }

            if (!ValidActionIds(camera.DefaultActionIds) ||
                camera.Zones.Any(zone => zone.ActionIds is not null &&
                    (!ValidActionIds(zone.ActionIds) || zone.Exclude && zone.ActionIds.Length > 0)))
            {
                return "Select existing actions for camera defaults and detection zones. Exclusion zones cannot run actions.";
            }
        }

        return null;
    }

    private bool ValidActionIds(string[]? ids)
    {
        return ids is not null && ids.Length <= Actions.Length &&
            ids.Distinct().Count() == ids.Length &&
            ids.All(id => Actions.Any(action => action.Id == id));
    }
}
