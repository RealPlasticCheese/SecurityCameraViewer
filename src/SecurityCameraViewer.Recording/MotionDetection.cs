using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

public sealed record ZoneMotion(string Id, string Name, bool Detected, double ChangedPercent)
{
    public int DebounceFrames
    {
        get; init;
    }
}
public readonly record struct MotionSample(bool Detected, double ChangedPercent, bool WarmingUp, bool ScheduleActive = true, ZoneMotion[]? Zones = null)
{
    public DetectionObservation[] Detections { get; init; } = [];
    public AdaptiveMotionDiagnostics[] AdaptiveDiagnostics { get; init; } = [];
    public AiAnalysisStatus? Ai
    {
        get; init;
    }
}

/// <summary>One detector instance per camera/connection and assigned policy. Pixels are borrowed, row-major grayscale, 0–255.
/// Implementations must not retain the buffer. Time is monotonic seconds. No storage or UI responsibilities.</summary>
public interface IMotionDetector : IFrameMotionDetector
{
    MotionSample Analyse(ReadOnlySpan<byte> pixels, int width, int height, double seconds, DateTimeOffset capturedAt);

    Task<MotionSample> IFrameMotionDetector.AnalyseAsync(AnalysisFrame frame, double seconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Analyse(frame.Grayscale(), frame.Width, frame.Height, seconds, frame.CapturedAt));
    }
}

public interface IMotionDetectorFactory
{
    string Name
    {
        get;
    }
    IFrameMotionDetector Create(CameraRecordingSettings settings, TimeZoneInfo timeZone);
}

public sealed class AdaptiveBackgroundDetectorFactory : IMotionDetectorFactory
{
    public string Name => "Adaptive background";
    public IFrameMotionDetector Create(CameraRecordingSettings settings, TimeZoneInfo timeZone) => new AdaptiveBackgroundDetector(settings, timeZone);
}

/// <summary>Coordinates independent preparation, background statistics, temporal filtering and zone policy.
/// Detects movement, not object classes. Legacy settings retain two-second warmup and three-frame confirmation.</summary>
public sealed class AdaptiveBackgroundDetector(CameraRecordingSettings settings, TimeZoneInfo timeZone) : IMotionDetector
{
    private bool[] _included = [], _changed = [], _spatial = [], _filtered = [];
    private readonly Dictionary<string, int> _zoneStreaks = [];
    private double _started;
    private int _streak, _width, _height;
    private readonly MotionRegionMask _regions = new(settings.Zones, timeZone);
    private int _regionRevision;
    private readonly MotionRegionExtractor _observations = new();
    private readonly AdaptiveFramePreparation _preparation = new();
    private readonly AdaptiveBackgroundModel _model = new();
    private readonly MotionRegionPersistence _persistence = new();
    private byte[] _grayscale = [];

    public Task<MotionSample> AnalyseAsync(AnalysisFrame frame, double seconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (frame.Pixels.Length != checked(frame.Width * frame.Height * (frame.IsRgb ? 3 : 1)))
        {
            throw new ArgumentException("Invalid analysis frame.");
        }
        var pixels = frame.Pixels;
        if (frame.IsRgb)
        {
            var length = checked(frame.Width * frame.Height);
            if (_grayscale.Length != length)
            {
                _grayscale = new byte[length];
            }
            frame.CopyGrayscale(_grayscale);
            pixels = _grayscale;
        }
        return Task.FromResult(Analyse(pixels, frame.IsRgb ? frame.Pixels : [],
            frame.Width, frame.Height, seconds, frame.CapturedAt));
    }

    public MotionSample Analyse(ReadOnlySpan<byte> pixels, int width, int height, double seconds, DateTimeOffset capturedAt) =>
        Analyse(pixels, [], width, height, seconds, capturedAt);

    private MotionSample Analyse(ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> rgb, int width, int height,
        double seconds, DateTimeOffset capturedAt)
    {
        if (width < 3 || height < 3 || pixels.Length != checked(width * height))
        {
            throw new ArgumentException("Invalid grayscale frame.");
        }
        using var measurement = ProcessingMetrics.Measure("motion.total");
        var options = settings.Adaptive;
        if (options.SuppressShadows && rgb.Length != checked(pixels.Length * 3))
        {
            throw new InvalidOperationException("Colour analysis frames are required for adaptive shadow suppression.");
        }
        _regions.Update(width, height, capturedAt);
        _included = _regions.Pixels;
        pixels = _preparation.Prepare(pixels, width, height, _included, options.SmoothFrames);
        if (_width != width || _height != height || _regionRevision != _regions.Revision)
        {
            _regionRevision = _regions.Revision;
            _width = width;
            _height = height;
            _started = seconds;
            _streak = 0;
            _changed = new bool[pixels.Length];
            _spatial = new bool[pixels.Length];
            _filtered = options.RegionPersistenceFrames == 1 ? _spatial : new bool[pixels.Length];
            _zoneStreaks.Clear();
            _model.Reset(pixels, rgb, options);
            _persistence.Reset(pixels.Length);
        }
        var warmup = seconds - _started < 2;
        var lighting = _model.Estimate(pixels, _included, options.IlluminationCompensation == "robust");
        var shadows = _model.Update(pixels, rgb, _included, _changed, lighting,
            8 + (100 - settings.Sensitivity) * 0.42, warmup, options);
        Array.Clear(_spatial);
        var lightingChange = Math.Abs(lighting.Shift) > 35;
        var spatialCount = 0;
        // Warmup/exposure recovery cannot supply temporal confirmation for later frames.
        if (!warmup && !lightingChange)
        {
            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    var i = y * width + x;
                    if (_changed[i] && ((_changed[i - 1] ? 1 : 0) + (_changed[i + 1] ? 1 : 0) +
                        (_changed[i - width] ? 1 : 0) + (_changed[i + width] ? 1 : 0)) >= 2)
                    {
                        _spatial[i] = true;
                        spatialCount++;
                    }
                }
            }
        }
        int changed;
        if (options.RegionPersistenceFrames == 1)
        {
            _filtered = _spatial;
            changed = spatialCount;
        }
        else
        {
            changed = _persistence.Apply(_spatial, _filtered, width, height, seconds,
                options.RegionPersistenceFrames, settings.AnalysisFramesPerSecond);
        }
        var percent = 100.0 * changed / Math.Max(1, lighting.IncludedPixels);
        _streak = !warmup && !lightingChange && percent >= settings.MinimumAreaPercent
            ? Math.Min(options.ConfirmationFrames, _streak + 1) : 0;
        var zones = new ZoneMotion[_regions.Memberships.Length];
        for (var index = 0; index < zones.Length; index++)
        {
            var membership = _regions.Memberships[index];
            var zone = membership.Zone;
            var moving = 0;
            foreach (var pixel in membership.Indices)
            {
                if (_filtered[pixel])
                {
                    moving++;
                }
            }
            var score = 100.0 * moving / Math.Max(1, membership.Indices.Length);
            var streak = !warmup && !lightingChange && score >= settings.MinimumAreaPercent
                ? Math.Min(options.ConfirmationFrames, _zoneStreaks.GetValueOrDefault(zone.Id) + 1) : 0;
            _zoneStreaks[zone.Id] = streak;
            zones[index] = new ZoneMotion(zone.Id, zone.Name, streak >= options.ConfirmationFrames, score)
            {
                DebounceFrames = streak
            };
        }
        var detected = zones.Length > 0 ? zones.Any(zone => zone.Detected) : _streak >= options.ConfirmationFrames;
        var policyPercent = zones.Length > 0 ? zones.Max(zone => zone.ChangedPercent) : percent;
        var streakProgress = zones.Length > 0 ? zones.Max(zone => zone.DebounceFrames) : _streak;
        var reason = lighting.IncludedPixels == 0 ? "inactive" : warmup ? "warmup" : lightingChange ? "lighting-change"
            : detected ? "confirmed" : spatialCount > changed && changed == 0 ? "region-persistence"
            : policyPercent < settings.MinimumAreaPercent ? "below-area" : "debouncing";
        var diagnostics = options.IncludeDiagnostics ? new AdaptiveMotionDiagnostics(policyPercent, settings.MinimumAreaPercent,
            streakProgress, options.ConfirmationFrames, lighting.Shift, lighting.Gain, shadows, reason) : null;
        return new(detected, percent, warmup, lighting.IncludedPixels > 0, zones)
        {
            AdaptiveDiagnostics = diagnostics is null ? [] : [diagnostics],
            Detections = !warmup && !lightingChange && spatialCount > 0
                ? _observations.Extract(_spatial, width, height, lighting.IncludedPixels, _regions.ZonePixels, zones, detected,
                    capturedAt, Math.Max(1.5, 3 / settings.AnalysisFramesPerSecond), options.CandidateMinimumPixels,
                    _filtered, options.RegionPersistenceFrames > 1 ? _persistence.Progress : null,
                    options.RegionPersistenceFrames, diagnostics,
                    options.RegionPersistenceFrames > 1 ? _persistence.Components : null) : []
        };
    }
}
