namespace SecurityCameraViewer.Recording;

/// <summary>Optional background-model and artifact filters. Defaults preserve existing detection policy.</summary>
public sealed record AdaptiveMotionSettings
{
    public bool LearnNoise
    {
        get; init;
    }
    public double NoiseAllowance { get; init; } = 3;
    public bool SmoothFrames
    {
        get; init;
    }
    public int RegionPersistenceFrames { get; init; } = 1;
    public int ConfirmationFrames { get; init; } = 3;
    public string IlluminationCompensation { get; init; } = "mean";
    public bool SuppressShadows
    {
        get; init;
    }
    public double ShadowMinimumBrightnessRatio { get; init; } = 0.6;
    public double ShadowColorTolerance { get; init; } = 0.08;
    public int CandidateMinimumPixels { get; init; } = 4;
    public bool IncludeDiagnostics
    {
        get; init;
    }

    public string? Validate()
    {
        return !double.IsFinite(NoiseAllowance) || NoiseAllowance is < 1 or > 6 ||
            RegionPersistenceFrames is < 1 or > 10 || ConfirmationFrames is < 1 or > 10 ||
            IlluminationCompensation is not ("mean" or "robust") ||
            !double.IsFinite(ShadowMinimumBrightnessRatio) || ShadowMinimumBrightnessRatio is < 0.3 or > 0.95 ||
            !double.IsFinite(ShadowColorTolerance) || ShadowColorTolerance is < 0.01 or > 0.3 ||
            CandidateMinimumPixels is < 4 or > 10000
            ? "Check adaptive noise, persistence, lighting, shadow and candidate display options." : null;
    }
}

public sealed record AdaptiveMotionDiagnostics(double ChangedPercent, double MinimumAreaPercent,
    int DebounceFrames, int RequiredFrames, double IlluminationShift, double IlluminationGain,
    int SuppressedShadowPixels, string Reason)
{
    public string? TemplateName
    {
        get; init;
    }
    public DetectionZone[] Zones { get; init; } = [];
}
