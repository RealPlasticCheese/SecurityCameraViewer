// Frozen characterization reference from master 35d3b46. Keep the original arithmetic and policy.
using SecurityCameraViewer.Recording;
namespace AdaptiveReference;

/// <summary>Reusable spatial/weekly scheduling policy, independent of a motion detection algorithm.
/// Coordinates describe the entire source picture. Exclusions take precedence over inclusions.</summary>
public sealed class MotionRegionMask(MotionZone[] zones, TimeZoneInfo timeZone)
{
    private string _key = "";
    private int _width, _height;
    public bool[] Pixels { get; private set; } = [];
    public Dictionary<MotionZone, bool[]> ZonePixels { get; private set; } = [];
    public int Revision
    {
        get; private set;
    }

    public void Update(int width, int height, DateTimeOffset capturedAt)
    {
        var local = TimeZoneInfo.ConvertTime(capturedAt, timeZone).DateTime;
        var key = string.Join(',', zones.Select((zone, i) => zone.IsActive(local) ? i.ToString() : "-"));
        if (_width == width && _height == height && _key == key)
        {
            return;
        }

        _width = width;
        _height = height;
        _key = key;
        Revision++;
        Pixels = new bool[checked(width * height)];
        var active = zones.Where(zone => zone.IsActive(local)).ToArray();
        var regions = active.Where(zone => !zone.Exclude).ToArray();
        var exclusions = active.Where(zone => zone.Exclude).ToArray();
        var hasInclusions = zones.Any(zone => !zone.Exclude);
        ZonePixels = regions.ToDictionary(z => z, _ => new bool[Pixels.Length]);
        for (var i = 0; i < Pixels.Length; i++)
        {
            var x = (i % width + .5) / width;
            var y = (i / width + .5) / height;
            Pixels[i] = (!hasInclusions || regions.Any(zone => zone.Contains(x, y))) && !exclusions.Any(zone => zone.Contains(x, y));
            foreach (var pair in ZonePixels)
            {
                pair.Value[i] = Pixels[i] && pair.Key.Contains(x, y);
            }
        }
    }
}
