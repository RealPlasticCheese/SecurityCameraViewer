using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

public sealed record DetectionTemplate(string Id, string Name, DetectionSettings Settings)
{
    [JsonIgnore]
    public bool IsValid => Guid.TryParseExact(Id, "N", out _) && !string.IsNullOrWhiteSpace(Name) &&
        Name.Length <= 80 && !Name.Any(char.IsControl) && Settings is not null;
}

/// <summary>Resolves template inheritance and migrates legacy inline settings without disk or UI dependencies.</summary>
public static class DetectionTemplateResolver
{
    public static RecordingSettings Migrate(RecordingSettings settings)
    {
        var templates = settings.DetectionTemplates.ToList();
        var cameras = settings.Cameras.Select(camera =>
        {
            if (camera.DetectionTemplateId is not null)
            {
                return camera;
            }
            var policy = JsonSerializer.Serialize<DetectionSettings>(camera);
            var template = templates.FirstOrDefault(item => JsonSerializer.Serialize(item.Settings) == policy);
            if (template is null)
            {
                // Preserve excess legacy policies inline rather than invalidating larger camera collections.
                if (templates.Count >= 64)
                {
                    return camera;
                }
                var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("legacy-detection-" + policy)))[..32];
                template = new(id, $"Detection {templates.Count + 1}", JsonSerializer.Deserialize<DetectionSettings>(policy)!);
                templates.Add(template);
            }
            return camera with
            {
                DetectionTemplateId = template.Id
            };
        }).ToArray();
        return settings with
        {
            Cameras = cameras,
            DetectionTemplates = templates.ToArray()
        };
    }

    public static DetectionPlan Resolve(RecordingSettings settings, CameraRecordingSettings camera)
    {
        DetectionSettings Policy(string? id) => id is null ? camera : settings.DetectionTemplates
            .FirstOrDefault(template => template.Id == id)?.Settings ?? throw new ArgumentException("Unknown detection template.");
        var inclusions = camera.Zones.Where(zone => !zone.Exclude).ToArray();
        var exclusions = camera.Zones.Where(zone => zone.Exclude).ToArray();
        var groups = inclusions.Length == 0
            ? new[] { Group(camera.DetectionTemplateId, exclusions) }
            : inclusions.GroupBy(zone => zone.DetectionTemplateId ?? camera.DetectionTemplateId)
                .Select(group => Group(group.Key, group.Concat(exclusions).ToArray())).ToArray();
        var source = camera with
        {
            AnalysisWidth = groups.Max(group => group.Settings.DetectionMethod == DetectionMethods.Adaptive
                ? group.Settings.AnalysisWidth : Math.Max(640, group.Settings.AnalysisWidth)),
            AnalysisFramesPerSecond = groups.Max(group => group.Settings.AnalysisFramesPerSecond),
            DetectionMethod = groups.Any(group => group.Settings.DetectionMethod != DetectionMethods.Adaptive)
                ? DetectionMethods.Ai : DetectionMethods.Adaptive,
            Adaptive = camera.Adaptive with
            {
                SuppressShadows = groups.Any(group => group.Settings.Adaptive.SuppressShadows)
            }
        };
        return new(source, groups);

        DetectionPlanGroup Group(string? id, MotionZone[] zones)
        {
            var effective = new CameraRecordingSettings(Policy(id))
            {
                Channel = camera.Channel,
                Zones = zones
            };
            return new(effective, id is null ? null : settings.DetectionTemplates.Single(template => template.Id == id).Name);
        }
    }
}

public sealed record DetectionPlanGroup(CameraRecordingSettings Settings, string? TemplateName);
public sealed record DetectionPlan(CameraRecordingSettings SourceSettings, DetectionPlanGroup[] Groups)
{
    public IFrameMotionDetector Create(IMotionDetectorFactory factory, TimeZoneInfo timeZone) =>
        new TemplateMotionDetector(Groups, factory, timeZone);
}

/// <summary>One worker per distinct assigned template. Shares capture while preserving per-template rate, size and zones.</summary>
internal sealed class TemplateMotionDetector : IAsyncMotionDetector
{
    private sealed class Worker(DetectionPlanGroup group, IFrameMotionDetector detector)
    {
        public DetectionPlanGroup Group { get; } = group;
        public IFrameMotionDetector Detector { get; } = detector;
        public double LastSeconds { get; set; } = double.NegativeInfinity;
        public MotionSample LastSample
        {
            get; set;
        }
        public byte[] Pixels { get; set; } = [];
        private int[] _sourceColumns = [];
        private int _sourceWidth, _sourceChannels;

        public AnalysisFrame Resize(AnalysisFrame frame)
        {
            var width = Group.Settings.DetectionMethod == DetectionMethods.Adaptive
                ? Group.Settings.AnalysisWidth : Math.Max(640, Group.Settings.AnalysisWidth);
            var height = width * 9 / 16;
            if (frame.Width <= width && frame.Height <= height)
            {
                return frame;
            }
            using var measurement = ProcessingMetrics.Measure("image.resize");
            var channels = frame.IsRgb ? 3 : 1;
            var length = checked(width * height * channels);
            if (Pixels.Length != length)
            {
                Pixels = new byte[length];
            }
            if (_sourceColumns.Length != width || _sourceWidth != frame.Width || _sourceChannels != channels)
            {
                _sourceColumns = new int[width];
                _sourceWidth = frame.Width;
                _sourceChannels = channels;
                for (var x = 0; x < width; x++)
                {
                    _sourceColumns[x] = x * frame.Width / width * channels;
                }
            }
            for (var y = 0; y < height; y++)
            {
                var row = y * frame.Height / height * frame.Width * channels;
                var destination = y * width * channels;
                for (var x = 0; x < width; x++, destination += channels)
                {
                    var source = row + _sourceColumns[x];
                    Pixels[destination] = frame.Pixels[source];
                    if (channels == 3)
                    {
                        Pixels[destination + 1] = frame.Pixels[source + 1];
                        Pixels[destination + 2] = frame.Pixels[source + 2];
                    }
                }
            }
            return new(Pixels, width, height, frame.IsRgb, frame.CapturedAt);
        }
    }

    private readonly Worker[] _workers;
    private readonly double _captureRate;

    public TemplateMotionDetector(DetectionPlanGroup[] groups, IMotionDetectorFactory factory, TimeZoneInfo timeZone)
    {
        _captureRate = groups.Max(group => group.Settings.AnalysisFramesPerSecond);
        var workers = new List<Worker>();
        try
        {
            foreach (var group in groups)
            {
                workers.Add(new(group, factory.Create(group.Settings, timeZone)));
            }
            _workers = workers.ToArray();
        }
        catch
        {
            foreach (var worker in workers)
            {
                (worker.Detector as IDisposable)?.Dispose();
            }
            throw;
        }
    }

    public async Task<MotionSample> AnalyseAsync(AnalysisFrame frame, double seconds, CancellationToken token)
    {
        foreach (var worker in _workers)
        {
            token.ThrowIfCancellationRequested();
            // Capture already limits the fastest worker. A second clock gate would drop jittered frames,
            // and would change the existing contract of replacement frame sources.
            if (worker.Group.Settings.AnalysisFramesPerSecond == _captureRate ||
                seconds - worker.LastSeconds + 0.001 >= 1 / worker.Group.Settings.AnalysisFramesPerSecond)
            {
                worker.LastSample = await worker.Detector.AnalyseAsync(worker.Resize(frame), seconds, token);
                worker.LastSeconds = seconds;
            }
        }
        var zones = _workers.SelectMany(worker => worker.LastSample.Zones ?? []).ToArray();
        var observations = _workers.SelectMany(worker => (worker.LastSample.Detections ?? []).Select(observation =>
            worker.Group.TemplateName is null ? observation : observation with
            {
                Sources = observation.Sources.Select(source => source with
                {
                    Attributes = [.. source.Attributes, new("detectionTemplate", Text: worker.Group.TemplateName)]
                }).ToArray()
            })).ToArray();
        // Confirmed objects first, then confirmed pixel regions, then the largest diagnostic candidates.
        observations = observations.OrderByDescending(observation => observation.Confirmed)
            .ThenByDescending(observation => observation.Kind == "object")
            .ThenByDescending(observation => observation.Bounds.Width * observation.Bounds.Height).Take(128).ToArray();
        return new(_workers.Any(worker => worker.LastSample.Detected),
            _workers.Max(worker => worker.LastSample.ChangedPercent), _workers.Any(worker => worker.LastSample.WarmingUp),
            _workers.Any(worker => worker.LastSample.ScheduleActive), zones)
        {
            Detections = observations,
            AdaptiveDiagnostics = _workers.SelectMany(worker => (worker.LastSample.AdaptiveDiagnostics ?? []).Select(diagnostic => diagnostic with
            {
                TemplateName = worker.Group.TemplateName,
                Zones = worker.Group.Settings.Zones.Where(zone => !zone.Exclude).Select(zone => new DetectionZone(zone.Id, zone.Name)).ToArray()
            })).ToArray(),
            Ai = _workers.Select(worker => worker.LastSample.Ai).FirstOrDefault(status => status?.Failed == true)
                ?? _workers.Select(worker => worker.LastSample.Ai).FirstOrDefault(status => status is not null)
        };
    }

    public void Dispose()
    {
        foreach (var worker in _workers)
        {
            (worker.Detector as IDisposable)?.Dispose();
        }
    }
}
