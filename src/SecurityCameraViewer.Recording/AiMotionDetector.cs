namespace SecurityCameraViewer.Recording;

public sealed class ConfigurableMotionDetectorFactory(IAiInferenceService inference) : IMotionDetectorFactory
{
    public string Name => "Per-camera motion / local AI";

    public IFrameMotionDetector Create(CameraRecordingSettings settings, TimeZoneInfo timeZone)
    {
        return settings.DetectionMethod switch
        {
            DetectionMethods.Adaptive => new AdaptiveBackgroundDetector(settings, timeZone),
            DetectionMethods.Hybrid or DetectionMethods.Ai => new AiMotionDetector(settings, timeZone, inference),
            _ => throw new InvalidOperationException("Unknown detection method.")
        };
    }
}

/// <summary>Camera-local zone and motion-gate policy. All costly inference goes through the shared service.</summary>
public sealed class AiMotionDetector : IAsyncMotionDetector
{
    private readonly CameraRecordingSettings _settings;
    private readonly IAiInferenceService _inference;
    private readonly MotionRegionMask _regions;
    private readonly AdaptiveBackgroundDetector? _gate;
    private readonly Dictionary<string, double> _gateUntil = [];
    private ObjectAnalysis? _lastAnalysis;
    private double _lastInference = double.NegativeInfinity;
    private DateTimeOffset _analysisCapturedAt;
    private readonly Dictionary<string, (DateTimeOffset CapturedAt, double ChangedPercent)> _gateEvidence = [];
    private int _revision;

    public AiMotionDetector(CameraRecordingSettings settings, TimeZoneInfo timeZone, IAiInferenceService inference)
    {
        _settings = settings;
        _inference = inference;
        _regions = new(settings.Zones, timeZone);
        if (settings.DetectionMethod == DetectionMethods.Hybrid)
        {
            _gate = new(settings with
            {
                Sensitivity = settings.MotionGateSensitivity,
                MinimumAreaPercent = settings.MotionGateMinimumAreaPercent
            }, timeZone);
        }
    }

    public async Task<MotionSample> AnalyseAsync(AnalysisFrame frame, double seconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _regions.Update(frame.Width, frame.Height, frame.CapturedAt);
        if (_revision != _regions.Revision)
        {
            _revision = _regions.Revision;
            _gateUntil.Clear();
            _gateEvidence.Clear();
            _lastAnalysis = null;
            _lastInference = double.NegativeInfinity;
        }

        MotionSample? gateSample = _gate is null ? null : await _gate.AnalyseAsync(frame, seconds, token);
        if (gateSample is { Detected: true } motion)
        {
            if (motion.Zones is { Length: > 0 })
            {
                foreach (var zone in motion.Zones.Where(zone => zone.Detected))
                {
                    _gateUntil[zone.Id] = seconds + 3;
                    _gateEvidence[zone.Id] = (frame.CapturedAt, zone.ChangedPercent);
                }
            }
            else
            {
                _gateUntil["full-frame"] = seconds + 3;
                _gateEvidence["full-frame"] = (frame.CapturedAt, motion.ChangedPercent);
            }
        }

        var active = _regions.Pixels.Any(pixel => pixel);
        var eligible = active && (_gate is null || _gateUntil.Values.Any(until => until >= seconds));
        if (eligible && seconds - _lastInference >= 1 / _settings.AiFramesPerSecond)
        {
            _lastAnalysis = await _inference.AnalyseAsync(frame, _settings, token);
            _lastInference = seconds;
            _analysisCapturedAt = frame.CapturedAt;
        }

        var fresh = eligible && _lastAnalysis is { Status.Failed: false } &&
            seconds - _lastInference <= Math.Max(2, 2 / _settings.AiFramesPerSecond);
        var objects = fresh ? _lastAnalysis!.Objects : [];
        var triggers = objects.Where(item => _settings.AiClasses.Contains(item.Class) && !_settings.AiIgnoreClasses.Contains(item.Class)).ToArray();
        var zones = _regions.ZonePixels.Select(pair =>
        {
            var gated = _gate is null || _gateUntil.GetValueOrDefault(pair.Key.Id, double.NegativeInfinity) >= seconds;
            var detected = gated && triggers.Any(item => Inside(item, pair.Value, frame.Width, frame.Height));
            return new ZoneMotion(pair.Key.Id, pair.Key.Name, detected, 0);
        }).ToArray();
        var fullFrame = triggers.Any(item => Inside(item, _regions.Pixels, frame.Width, frame.Height));
        var detections = objects.Where(item => (_settings.AiClasses.Contains(item.Class) || _settings.AiIgnoreClasses.Contains(item.Class)) &&
            Inside(item, _regions.Pixels, frame.Width, frame.Height)).OrderBy(item => _settings.AiIgnoreClasses.Contains(item.Class)).Take(64).Select(item =>
        {
            var ignored = _settings.AiIgnoreClasses.Contains(item.Class);
            var objectZones = _regions.ZonePixels.Where(pair => Inside(item, pair.Value, frame.Width, frame.Height))
                .Select(pair => new DetectionZone(pair.Key.Id, pair.Key.Name)).ToArray();
            var gateKeys = objectZones.Length > 0 ? objectZones.Select(zone => zone.Id).ToArray() : ["full-frame"];
            var gates = gateKeys.Where(key => _gateUntil.GetValueOrDefault(key, double.NegativeInfinity) >= seconds).ToArray();
            var evidence = new List<DetectionEvidence>();
            foreach (var key in gates)
            {
                var gate = _gateEvidence[key];
                evidence.Add(new(DetectionMethods.Adaptive, gate.CapturedAt,
                [
                    new("zone", Text: objectZones.FirstOrDefault(zone => zone.Id == key)?.Name ?? "Whole active image"),
                    new("changedArea", Number: gate.ChangedPercent, Unit: "%")
                ]));
            }
            evidence.Add(new(DetectionMethods.Ai, _analysisCapturedAt,
            [
                new("objectType", Text: item.Class),
                new("confidence", Number: item.Confidence, Unit: "ratio"),
                new("device", Text: _lastAnalysis!.Status.Device),
                new("inferenceTime", Number: _lastAnalysis.Status.Milliseconds, Unit: "ms")
            ], _settings.AiModel));
            return new DetectionObservation(new(item.X, item.Y, item.Width, item.Height), "object", evidence.ToArray(), objectZones,
                !ignored && (_gate is null || gates.Length > 0), _analysisCapturedAt,
                _analysisCapturedAt.AddSeconds(Math.Max(2, 2 / _settings.AiFramesPerSecond)))
            {
                Disposition = ignored ? "ignore" : "trigger"
            };
        });
        var motionRegions = (gateSample?.Detections ?? []).Select(item => item with { Confirmed = false });
        return new(zones.Length > 0 ? zones.Any(zone => zone.Detected) : fullFrame,
            gateSample?.ChangedPercent ?? 0, gateSample?.WarmingUp ?? false, active, zones)
        {
            Detections = motionRegions.Concat(detections).Take(128).ToArray(),
            AdaptiveDiagnostics = gateSample?.AdaptiveDiagnostics ?? [],
            Ai = _lastAnalysis?.Status ?? new(_settings.AiModel, _settings.AiDevice, 0,
                _gate is null ? "Waiting for AI analysis" : "Waiting for motion before AI analysis")
        };
    }

    private static bool Inside(DetectedObject item, bool[] mask, int width, int height)
    {
        var x = Math.Clamp((int)((item.X + item.Width / 2) * width), 0, width - 1);
        var y = Math.Clamp((int)((item.Y + item.Height / 2) * height), 0, height - 1);
        return mask[y * width + x];
    }

    public void Dispose()
    {
        _lastAnalysis = null;
        _gateUntil.Clear();
        _gateEvidence.Clear();
    }
}
