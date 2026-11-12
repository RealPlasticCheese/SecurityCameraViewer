namespace SecurityCameraViewer.Recording;

/// <summary>Reusable spatial/weekly scheduling policy, independent of a motion detection algorithm.
/// Coordinates describe the entire source picture. Exclusions take precedence over inclusions.</summary>
public sealed class MotionRegionMask(MotionZone[] zones, TimeZoneInfo timeZone)
{
    private readonly bool[] _active = new bool[zones.Length];
    private bool _initialized;
    private int _width, _height;
    internal ZonePixelMembership[] Memberships { get; private set; } = [];
    public bool[] Pixels { get; private set; } = [];
    public Dictionary<MotionZone, bool[]> ZonePixels { get; private set; } = [];
    public int Revision
    {
        get; private set;
    }

    public void Update(int width, int height, DateTimeOffset capturedAt)
    {
        var local = TimeZoneInfo.ConvertTime(capturedAt, timeZone).DateTime;
        var changed = _width != width || _height != height || !_initialized && zones.Length > 0;
        for (var index = 0; index < zones.Length; index++)
        {
            var isActive = zones[index].IsActive(local);
            changed |= _active[index] != isActive;
            _active[index] = isActive;
        }
        _initialized = true;
        if (!changed)
        {
            return;
        }

        _width = width;
        _height = height;
        Revision++;
        Pixels = new bool[checked(width * height)];
        var active = zones.Where((_, index) => _active[index]).ToArray();
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
        Memberships = ZonePixels.Select(pair => new ZonePixelMembership(pair.Key,
            Enumerable.Range(0, pair.Value.Length).Where(index => pair.Value[index]).ToArray())).ToArray();
    }
}

internal sealed record ZonePixelMembership(MotionZone Zone, int[] Indices);
