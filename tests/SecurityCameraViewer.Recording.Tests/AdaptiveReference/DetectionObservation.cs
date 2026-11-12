// Frozen characterization reference from master 35d3b46. Keep the original arithmetic and policy.
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Core;
namespace AdaptiveReference;
/// <summary>Extracts bounded connected moving regions with reusable scratch memory.</summary>
internal sealed class MotionRegionExtractor
{
    private bool[] _visited = [];
    private int[] _queue = [];

    public DetectionObservation[] Extract(bool[] pixels, int width, int height, int includedPixels,
        Dictionary<MotionZone, bool[]> zones, ZoneMotion[] scores, bool detected, DateTimeOffset capturedAt, double lifetime,
        int minimumPixels = 4, bool[]? qualifyingPixels = null, int[]? persistence = null,
        int requiredPersistence = 1, AdaptiveMotionDiagnostics? diagnostics = null)
    {
        using var measurement = ProcessingMetrics.Measure("motion.regions");
        if (_visited.Length != pixels.Length)
        {
            _visited = new bool[pixels.Length];
            _queue = new int[pixels.Length];
        }

        Array.Clear(_visited);
        var kept = new PriorityQueue<DetectionObservation, int>();
        for (var index = 0; index < pixels.Length; index++)
        {
            if (!pixels[index] || _visited[index])
            {
                continue;
            }

            var head = 0;
            var tail = 0;
            var left = width;
            var top = height;
            var right = 0;
            var bottom = 0;
            Visit(index, pixels, ref tail);
            while (head < tail)
            {
                var pixel = _queue[head++];
                var x = pixel % width;
                var y = pixel / width;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
                if (x > 0)
                {
                    Visit(pixel - 1, pixels, ref tail);
                }
                if (x + 1 < width)
                {
                    Visit(pixel + 1, pixels, ref tail);
                }
                if (y > 0)
                {
                    Visit(pixel - width, pixels, ref tail);
                }
                if (y + 1 < height)
                {
                    Visit(pixel + width, pixels, ref tail);
                }
            }

            if (tail < minimumPixels)
            {
                continue;
            }
            if (kept.Count == 64 && kept.TryPeek(out _, out var smallest) && tail <= smallest)
            {
                continue;
            }

            var observation = CreateObservation(width, height, includedPixels, zones, scores, detected, capturedAt,
                lifetime, qualifyingPixels, persistence, requiredPersistence, diagnostics, tail, left, top, right, bottom);
            kept.Enqueue(observation, tail);
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
        AdaptiveMotionDiagnostics? diagnostics, int tail, int left, int top, int right, int bottom)
    {
        var touched = zones.Where(pair => Touches(pair.Value, tail)).Select(pair => new DetectionZone(pair.Key.Id, pair.Key.Name)).ToArray();
        var confirmed = scores.Length == 0 ? detected : touched.Any(zone => scores.Any(score => score.Id == zone.Id && score.Detected));
        var persistent = qualifyingPixels is null || qualifyingPixels[_queue[0]];
        confirmed &= persistent;
        var attributes = new List<DetectionAttribute>
        {
            new("movingPixels", Number: tail, Unit: "px"),
            new("movingArea", Number: 100.0 * tail / Math.Max(1, includedPixels), Unit: "%")
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
                new("regionPersistenceProgress", Text: $"{(persistence is null ? 1 : persistence[_queue[0]])}/{requiredPersistence}"),
                new("illuminationShift", Number: diagnostics.IlluminationShift),
                new("illuminationGain", Number: diagnostics.IlluminationGain),
                new("suppressedShadowPixels", Number: diagnostics.SuppressedShadowPixels, Unit: "px"),
                new("reason", Text: reason)
            ]);
        }
        var evidence = new DetectionEvidence(DetectionMethods.Adaptive, capturedAt, attributes.ToArray());
        return new DetectionObservation(new((double)left / width, (double)top / height,
            (double)(right - left + 1) / width, (double)(bottom - top + 1) / height), "motion", [evidence], touched,
            confirmed, capturedAt, capturedAt.AddSeconds(lifetime));
    }

    private void Visit(int pixel, bool[] pixels, ref int tail)
    {
        if (pixels[pixel] && !_visited[pixel])
        {
            _visited[pixel] = true;
            _queue[tail++] = pixel;
        }
    }

    private bool Touches(bool[] mask, int tail)
    {
        for (var index = 0; index < tail; index++)
        {
            if (mask[_queue[index]])
            {
                return true;
            }
        }
        return false;
    }

}
