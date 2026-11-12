namespace SecurityCameraViewer.Recording;

/// <summary>Reusable detection policy, independent of camera capture, actions and zone geometry.</summary>
public record DetectionSettings
{
    public string DetectionMethod { get; init; } = DetectionMethods.Adaptive;
    public int AnalysisWidth { get; init; } = 320;
    public double AnalysisFramesPerSecond { get; init; } = 5;
    public string AiModel { get; init; } = "yolox-nano";
    public string AiDevice { get; init; } = "cpu";
    public double AiFramesPerSecond { get; init; } = 1;
    public double AiConfidence { get; init; } = 0.45;
    public string[] AiClasses { get; init; } = AiModelCatalog.DefaultTriggerClasses.ToArray();
    public string[] AiIgnoreClasses { get; init; } = [];
    public Dictionary<string, double> AiOptions { get; init; } = [];
    public int MotionGateSensitivity { get; init; } = 80;
    public double MotionGateMinimumAreaPercent { get; init; } = 0.1;
    public int Sensitivity { get; init; } = 60;
    public double MinimumAreaPercent { get; init; } = 1;
    public AdaptiveMotionSettings Adaptive { get; init; } = new();

    public string? Validate(IAiModelRegistry? models = null)
    {
        var settings = this;
        if (!DetectionMethods.All.Any(method => method.Id == settings.DetectionMethod) ||
            settings.AnalysisWidth is not (320 or 640 or 960) ||
            !double.IsFinite(settings.AnalysisFramesPerSecond) || settings.AnalysisFramesPerSecond is < 1 or > 10 ||
            !AiModelRegistry.IsIdentifier(settings.AiModel) ||
            settings.AiDevice is not ("cpu" or "gpu" or "auto") ||
            !double.IsFinite(settings.AiFramesPerSecond) || settings.AiFramesPerSecond is < 0.2 or > 5 ||
            !double.IsFinite(settings.AiConfidence) || settings.AiConfidence is < 0.1 or > 0.95 ||
            settings.AiClasses is null || settings.AiIgnoreClasses is null ||
            settings.AiClasses.Length + settings.AiIgnoreClasses.Length is < 1 or > 1000 ||
            settings.AiClasses.Concat(settings.AiIgnoreClasses).Distinct().Count() != settings.AiClasses.Length + settings.AiIgnoreClasses.Length ||
            settings.AiClasses.Concat(settings.AiIgnoreClasses).Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) ||
            settings.AiOptions is null || settings.AiOptions.Count > 2 ||
            settings.AiOptions.Any(option => option.Key is not ("iouThreshold" or "maxDetections") || !double.IsFinite(option.Value)) ||
            settings.MotionGateSensitivity is < 1 or > 100 ||
            !double.IsFinite(settings.MotionGateMinimumAreaPercent) || settings.MotionGateMinimumAreaPercent is < 0.1 or > 50)
        {
            return "Check detection method, resolution, analysis rates, AI model, device, classes, confidence and motion gate settings.";
        }
        if (models is not null)
        {
            if (models.Find(settings.AiModel) is not { } model)
            {
                return "The selected AI model is not registered. Refresh the model catalog and choose an available definition.";
            }
            if (AiModelRegistry.ValidateSettings(model, settings) is { } error)
            {
                return error;
            }
        }

        if (Sensitivity is < 1 or > 100 || !double.IsFinite(MinimumAreaPercent) || MinimumAreaPercent is < 0.1 or > 50)
        {
            return "Check detection sensitivity and minimum moving area.";
        }
        if (Adaptive is null)
        {
            return "Adaptive options are required.";
        }
        return Adaptive.Validate();
    }
}
