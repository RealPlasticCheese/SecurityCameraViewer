namespace SecurityCameraViewer.Web;

/// <summary>Browser picture adjustments, independent of capture and playback settings.</summary>
public sealed record CameraEnhancementSettings
{
    public bool Enabled { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; } = 8;
    public double Gamma { get; init; } = 1;
    public double Denoise { get; init; }
    public double Sharpness { get; init; } = 30;
    public double Radius { get; init; } = 1.2;
    public double Saturation { get; init; }
    public double Vibrance { get; init; }

    public string? Validate() =>
        InRange(Brightness, -50, 50) && InRange(Contrast, -50, 50) &&
        InRange(Gamma, 0.5, 2) && InRange(Denoise, 0, 100) &&
        InRange(Sharpness, 0, 100) && InRange(Radius, 0.5, 4) &&
        InRange(Saturation, -100, 50) && InRange(Vibrance, -50, 50)
            ? null : "Enhancement values must be finite and within the supported slider ranges.";

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;
}

/// <summary>Updates only edited values, so switching off cannot overwrite another browser's adjustments.</summary>
public sealed record CameraEnhancementUpdate
{
    public bool? Enabled { get; init; }
    public double? Brightness { get; init; }
    public double? Contrast { get; init; }
    public double? Gamma { get; init; }
    public double? Denoise { get; init; }
    public double? Sharpness { get; init; }
    public double? Radius { get; init; }
    public double? Saturation { get; init; }
    public double? Vibrance { get; init; }

    public CameraEnhancementSettings Apply(CameraEnhancementSettings settings) => settings with
    {
        Enabled = Enabled ?? settings.Enabled,
        Brightness = Brightness ?? settings.Brightness,
        Contrast = Contrast ?? settings.Contrast,
        Gamma = Gamma ?? settings.Gamma,
        Denoise = Denoise ?? settings.Denoise,
        Sharpness = Sharpness ?? settings.Sharpness,
        Radius = Radius ?? settings.Radius,
        Saturation = Saturation ?? settings.Saturation,
        Vibrance = Vibrance ?? settings.Vibrance
    };
}
