namespace SecurityCameraViewer.Recording;

/// <summary>Optional starting policies, installed explicitly and then edited independently of the catalog.</summary>
public static class DetectionTemplateCatalog
{
    public static DetectionTemplate[] Create() =>
    [
        new("00000000000000000000000000000001", "Existing adaptive behaviour", new()),
        new("00000000000000000000000000000002", "Outdoor foliage", new()
        {
            Adaptive = new()
            {
                LearnNoise = true, SmoothFrames = true, RegionPersistenceFrames = 2,
                IlluminationCompensation = "robust", IncludeDiagnostics = true, CandidateMinimumPixels = 8
            }
        }),
        new("00000000000000000000000000000003", "Small distant subjects", new()
        {
            AnalysisWidth = 640, Sensitivity = 80, MinimumAreaPercent = 0.2,
            Adaptive = new()
            {
                LearnNoise = true, RegionPersistenceFrames = 2,
                IlluminationCompensation = "robust", IncludeDiagnostics = true
            }
        }),
        new("00000000000000000000000000000004", "Shadow reduction", new()
        {
            Adaptive = new()
            {
                LearnNoise = true, RegionPersistenceFrames = 2, SuppressShadows = true,
                IlluminationCompensation = "robust", IncludeDiagnostics = true, CandidateMinimumPixels = 8
            }
        })
    ];
}
