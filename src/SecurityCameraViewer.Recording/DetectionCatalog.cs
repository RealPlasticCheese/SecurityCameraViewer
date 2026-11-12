namespace SecurityCameraViewer.Recording;

public sealed record DetectionMethod(string Id, string Name);

public static class DetectionMethods
{
    public const string Adaptive = "adaptive";
    public const string Hybrid = "adaptive-ai";
    public const string Ai = "ai";

    public static IReadOnlyList<DetectionMethod> All
    {
        get;
    } = Array.AsReadOnly<DetectionMethod>(
    [
        new(Adaptive, "Adaptive background subtraction"),
        new(Hybrid, "Adaptive background subtraction + AI"),
        new(Ai, "AI object detection")
    ]);
}

public sealed record AiNumericOption(string Id, string Label, double Minimum, double Maximum, double Step, double Default, string Help);
public sealed record AiModelPreset(string Id, string Name, string[] TriggerClasses, string[] IgnoreClasses,
    Dictionary<string, double> Options, string Device = "cpu");
public sealed record AiModelDefinition(string Id, string Name, string FileName, int InputSize, string Adapter, string License)
{
    public int Version { get; init; } = 1;
    public string? LabelSet
    {
        get; init;
    }
    public string[] Labels { get; init; } = [];
    public AiNumericOption[] Options { get; init; } = [];
    public AiModelPreset[] Presets { get; init; } = [];
    public bool BuiltIn
    {
        get; init;
    }
}

public static class AiModelCatalog
{
    private sealed record Defaults(string[] Labels, AiNumericOption[] Options, AiModelDefinition[] Models);
    private static readonly Defaults Configuration = Load();
    public static IReadOnlyList<AiModelDefinition> All
    {
        get;
    } = Array.AsReadOnly(Configuration.Models
        .Select(model => model with { Labels = Configuration.Labels, Options = Configuration.Options, BuiltIn = true }).ToArray());
    public static IReadOnlyList<AiNumericOption> DefaultOptions => Configuration.Options;
    public static IReadOnlyList<string> DefaultTriggerClasses => All[0].Presets.Single(preset => preset.Id == "default").TriggerClasses;
    public static IReadOnlyDictionary<int, string> Classes
    {
        get;
    } = Configuration.Labels
        .Select((name, index) => (name, index)).ToDictionary(item => item.index, item => item.name);

    private static Defaults Load()
    {
        using var stream = typeof(AiModelCatalog).Assembly.GetManifestResourceStream("SecurityCameraViewer.Recording.BuiltInModels.json")!;
        return System.Text.Json.JsonSerializer.Deserialize<Defaults>(stream,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
