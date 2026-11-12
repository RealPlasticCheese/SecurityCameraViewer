using System.Globalization;

namespace SecurityCameraViewer.Core;

/// <summary>Configured retry delays, with the final delay repeated until video recovers.</summary>
public sealed class CameraRetrySchedule
{
    public const string DefaultDelaysSeconds = "1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 8, 8, 16, 32";
    public const string ValidationMessage = "Enter comma-separated positive whole seconds (each at most 2147483647).";
    private readonly int[] _seconds;

    private CameraRetrySchedule(int[] seconds)
    {
        _seconds = seconds;
    }

    public TimeSpan GetDelay(int failureIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failureIndex);
        return TimeSpan.FromSeconds(_seconds[Math.Min(failureIndex, _seconds.Length - 1)]);
    }

    public static bool TryParse(string? value, out CameraRetrySchedule? schedule)
    {
        schedule = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var entries = value.Split(',');
        var seconds = new int[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            if (!int.TryParse(entries[index].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds[index]) ||
                seconds[index] <= 0)
            {
                return false;
            }
        }

        schedule = new(seconds);
        return true;
    }

    public static CameraRetrySchedule Parse(string value)
    {
        return TryParse(value, out var schedule) ? schedule! : throw new ArgumentException(ValidationMessage, nameof(value));
    }
}
