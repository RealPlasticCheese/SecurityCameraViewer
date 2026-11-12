using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

/// <summary>Geometry is relative to the entire source picture, independent of analysis resolution.</summary>
public sealed record DetectionBounds(double X, double Y, double Width, double Height);
public sealed record DetectionZone(string Id, string Name);

/// <summary>Structured evidence values. Display labels and formatting belong to the consumer.</summary>
public sealed record DetectionAttribute(string Name, string? Text = null, double? Number = null, string? Unit = null);
public sealed record DetectionEvidence(string Method, DateTimeOffset CapturedAt, DetectionAttribute[] Attributes, string? Model = null);
public sealed record DetectionObservation(DetectionBounds Bounds, string Kind, DetectionEvidence[] Sources,
    DetectionZone[] Zones, bool Confirmed, DateTimeOffset CapturedAt, DateTimeOffset ExpiresAt)
{
    public string Disposition { get; init; } = "trigger";
}

/// <summary>Extracts bounded connected moving regions with reusable scratch memory.</summary>
internal sealed class MotionRegionExtractor
{
    private readonly MotionConnectedComponents _components = new();

    public DetectionObservation[] Extract(bool[] pixels, int width, int height, int includedPixels,
        Dictionary<MotionZone, bool[]> zones, ZoneMotion[] scores, bool detected, DateTimeOffset capturedAt, double lifetime,
        int minimumPixels = 4, bool[]? qualifyingPixels = null, int[]? persistence = null,
        int requiredPersistence = 1, AdaptiveMotionDiagnostics? diagnostics = null, MotionConnectedComponents? components = null)
    {
        using var measurement = ProcessingMetrics.Measure("motion.regions");
        if (components is null)
        {
            _components.Find(pixels, width, height);
            components = _components;
        }
        var kept = new PriorityQueue<DetectionObservation, int>();
        foreach (var region in components.Regions)
        {
            if (region.Count < minimumPixels)
            {
                continue;
            }
            if (kept.Count == 64 && kept.TryPeek(out _, out var smallest) && region.Count <= smallest)
            {
                continue;
            }

            var observation = CreateObservation(width, height, includedPixels, zones, scores, detected, capturedAt,
                lifetime, qualifyingPixels, persistence, requiredPersistence, diagnostics, components.Pixels(region), region);
            kept.Enqueue(observation, region.Count);
            if (kept.Count > 64)
            {
                kept.Dequeue();
            }

        }

        return kept.UnorderedItems.OrderByDescending(item => item.Priority).Select(item => item.Element).ToArray();
    }

    private DetectionObservation CreateObservation(int width, int height, int includedPixels,
        Dictionary<MotionZone, bool[]> zones, ZoneMotion[] scores, bool detected, DateTimeOffset capturedAt,
        double lifetime, bool[]? qualifyingPixels, int[]? persistence, int requiredPersistence,
        AdaptiveMotionDiagnostics? diagnostics, ReadOnlySpan<int> pixels, MotionComponent region)
    {
        var touchedZones = new List<DetectionZone>();
        foreach (var pair in zones)
        {
            if (Touches(pair.Value, pixels))
            {
                touchedZones.Add(new(pair.Key.Id, pair.Key.Name));
            }
        }
        var touched = touchedZones.ToArray();
        var confirmed = scores.Length == 0 ? detected : touched.Any(zone => scores.Any(score => score.Id == zone.Id && score.Detected));
        var persistent = qualifyingPixels is null || qualifyingPixels[pixels[0]];
        confirmed &= persistent;
        var attributes = new List<DetectionAttribute>
        {
            new("movingPixels", Number: region.Count, Unit: "px"),
            new("movingArea", Number: 100.0 * region.Count / Math.Max(1, includedPixels), Unit: "%")
        };
        if (diagnostics is not null)
        {
            var zoneScores = scores.Where(score => touched.Any(zone => zone.Id == score.Id)).ToArray();
            var score = zoneScores.Length > 0 ? zoneScores.Max(zone => zone.ChangedPercent) : diagnostics.ChangedPercent;
            var debounce = zoneScores.Length > 0 ? zoneScores.Max(zone => zone.DebounceFrames) : diagnostics.DebounceFrames;
            var reason = confirmed ? "confirmed" : !persistent ? "region-persistence"
                : score < diagnostics.MinimumAreaPercent ? "below-area" : "debouncing";
            attributes.AddRange([
                new("monitoredMovingArea", Number: score, Unit: "%"),
                new("minimumMovingArea", Number: diagnostics.MinimumAreaPercent, Unit: "%"),
                new("confirmationProgress", Text: $"{debounce}/{diagnostics.RequiredFrames}"),
                new("regionPersistenceProgress", Text: $"{(persistence is null ? 1 : persistence[pixels[0]])}/{requiredPersistence}"),
                new("illuminationShift", Number: diagnostics.IlluminationShift),
                new("illuminationGain", Number: diagnostics.IlluminationGain),
                new("suppressedShadowPixels", Number: diagnostics.SuppressedShadowPixels, Unit: "px"),
                new("reason", Text: reason)
            ]);
        }
        var evidence = new DetectionEvidence(DetectionMethods.Adaptive, capturedAt, attributes.ToArray());
        return new DetectionObservation(new((double)region.Left / width, (double)region.Top / height,
            (double)(region.Right - region.Left + 1) / width, (double)(region.Bottom - region.Top + 1) / height), "motion", [evidence], touched,
            confirmed, capturedAt, capturedAt.AddSeconds(lifetime));
    }

    private static bool Touches(bool[] mask, ReadOnlySpan<int> pixels)
    {
        foreach (var pixel in pixels)
        {
            if (mask[pixel])
            {
                return true;
            }
        }
        return false;
    }

}
