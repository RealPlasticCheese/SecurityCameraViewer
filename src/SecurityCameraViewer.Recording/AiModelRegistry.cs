using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SecurityCameraViewer.Recording;

public interface IAiModelRegistry
{
    string ModelDirectory
    {
        get;
    }
    IReadOnlyList<AiModelDefinition> Models
    {
        get;
    }
    IReadOnlyList<string> Notices
    {
        get;
    }
    AiModelDefinition? Find(string id);
    void Reload();
}

/// <summary>Discovers data-only descriptions without loading inference sessions or changing saved camera policy.</summary>
public sealed class AiModelRegistry : IAiModelRegistry
{
    private sealed record Snapshot(AiModelDefinition[] Models, string[] Notices);
    private Snapshot _snapshot = new(AiModelCatalog.All.ToArray(), []);
    private readonly IAiModelMetadataReader _metadata;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public AiModelRegistry(string modelDirectory, IAiModelMetadataReader? metadata = null)
    {
        ModelDirectory = Path.GetFullPath(modelDirectory);
        _metadata = metadata ?? new OnnxModelMetadataReader();
        Reload();
    }

    public string ModelDirectory
    {
        get;
    }
    public IReadOnlyList<AiModelDefinition> Models => Volatile.Read(ref _snapshot).Models;
    public IReadOnlyList<string> Notices => Volatile.Read(ref _snapshot).Notices;
    public AiModelDefinition? Find(string id) => Models.FirstOrDefault(model => model.Id == id);
    public static bool IsIdentifier(string? id) => id is { Length: > 0 and <= 80 } &&
        Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant);

    public void Reload()
    {
        var models = AiModelCatalog.All.ToList();
        var notices = new List<string>();
        try
        {
            if (Directory.Exists(ModelDirectory))
            {
                var files = Directory.EnumerateFiles(ModelDirectory, "*.model.json").Order(StringComparer.Ordinal).Take(33).ToArray();
                if (files.Length > 32)
                {
                    notices.Add("Only the first 32 custom model descriptions are loaded.");
                }
                foreach (var path in files.Take(32))
                {
                    try
                    {
                        if (new FileInfo(path).Length > 256 * 1024)
                        {
                            throw new InvalidDataException("Description exceeds 256 KB.");
                        }
                        var model = JsonSerializer.Deserialize<AiModelDefinition>(File.ReadAllText(path), JsonOptions)
                            ?? throw new InvalidDataException("Description is empty.");
                        if (models.Any(existing => existing.Id == model.Id))
                        {
                            throw new InvalidDataException("Model ID is already registered; built-in definitions cannot be replaced.");
                        }
                        model = Complete(model with
                        {
                            BuiltIn = false
                        });
                        if (ValidateDefinition(model) is { } error)
                        {
                            throw new InvalidDataException(error);
                        }
                        models.Add(model);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or OverflowException or InvalidOperationException or FormatException)
                    {
                        // JSON diagnostics can contain arbitrary file contents; expose only a bounded filename and safe reason.
                        var reason = ex is InvalidDataException ? ex.Message : "Invalid or unreadable model description.";
                        notices.Add($"{Path.GetFileName(path)[..Math.Min(100, Path.GetFileName(path).Length)]}: {reason}");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notices.Add("The server could not read the model description folder.");
        }
        Volatile.Write(ref _snapshot, new(models.ToArray(), notices.ToArray()));
    }

    private AiModelDefinition Complete(AiModelDefinition model)
    {
        if (!ValidFileName(model.FileName))
        {
            throw new InvalidDataException("Use an ONNX filename in the model folder, without a path.");
        }
        var metadata = model.Labels is { Length: > 0 } && model.InputSize > 0
            ? new AiModelMetadata([], null) : _metadata.Read(Path.Combine(ModelDirectory, model.FileName));
        var labels = model.Labels is { Length: > 0 } ? model.Labels : model.LabelSet == "coco"
            ? AiModelCatalog.Classes.Values.ToArray() : metadata.Labels;
        var options = model.Options is { Length: > 0 } ? model.Options : AiModelCatalog.DefaultOptions.ToArray();
        if (!ValidOptions(options))
        {
            throw new InvalidDataException("Options must describe supported numeric settings, including confidence and framesPerSecond.");
        }
        var presets = model.Presets is { Length: > 0 } ? model.Presets :
            [new AiModelPreset("default", "Model defaults (all supported objects)", labels, [], options.ToDictionary(option => option.Id, option => option.Default))];
        return model with
        {
            Labels = labels,
            InputSize = model.InputSize > 0 ? model.InputSize : metadata.InputSize ?? 0,
            Options = options,
            Presets = presets
        };
    }

    private static bool ValidFileName(string? value) => value is { Length: > 0 and <= 160 } &&
        !value.Contains('/') && !value.Contains('\\') && !value.Contains(':') && value.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public static string? ValidateDefinition(AiModelDefinition model)
    {
        if (model.Version != 1 || !IsIdentifier(model.Id) || string.IsNullOrWhiteSpace(model.Name) || model.Name.Length > 120 ||
            !ValidFileName(model.FileName) || model.InputSize is < 32 or > 1280 || model.InputSize % 32 != 0 ||
            model.Adapter is not ("yolox" or "ultralytics" or "ultralytics-raw" or "ultralytics-end-to-end") ||
            model.License is null || model.License.Length > 200 || model.LabelSet is not (null or "coco") ||
            model.Labels is null || model.Labels.Length is < 1 or > 1000 ||
            model.Labels.Any(label => string.IsNullOrWhiteSpace(label) || label.Length > 80 || label.Any(char.IsControl)) ||
            model.Labels.Distinct(StringComparer.Ordinal).Count() != model.Labels.Length)
        {
            return "Check version, ID, name, adapter, square input size, license and the ordered class labels.";
        }
        if (!ValidOptions(model.Options))
        {
            return "Options must describe supported numeric settings, including confidence and framesPerSecond.";
        }
        if (model.Presets is null || model.Presets.Length is < 1 or > 16 ||
            model.Presets.Any(preset => preset is null || !IsIdentifier(preset.Id) || preset.Id == "custom" || string.IsNullOrWhiteSpace(preset.Name) ||
                preset.Name.Length > 120 || preset.Options is null || ValidateSettings(model, new()
                {
                    AiClasses = preset.TriggerClasses,
                    AiIgnoreClasses = preset.IgnoreClasses,
                    AiDevice = preset.Device,
                    AiConfidence = preset.Options.GetValueOrDefault("confidence", model.Options.Single(option => option.Id == "confidence").Default),
                    AiFramesPerSecond = preset.Options.GetValueOrDefault("framesPerSecond", model.Options.Single(option => option.Id == "framesPerSecond").Default),
                    AiOptions = preset.Options.Where(pair => pair.Key is not ("confidence" or "framesPerSecond")).ToDictionary()
                }) is not null) || model.Presets.DistinctBy(preset => preset.Id).Count() != model.Presets.Length)
        {
            return "Define unique presets with valid trigger/ignore classes, processing device and option values.";
        }
        return null;
    }

    private static bool ValidOptions(AiNumericOption[]? options) => options is { Length: >= 2 and <= 4 } &&
        options.All(option => option is not null && ValidOption(option)) &&
        options.DistinctBy(option => option.Id).Count() == options.Length &&
        options.Any(option => option.Id == "confidence") && options.Any(option => option.Id == "framesPerSecond");

    private static bool ValidOption(AiNumericOption option)
    {
        var limits = AiModelCatalog.DefaultOptions.FirstOrDefault(item => item.Id == option.Id);
        return limits is not null && !string.IsNullOrWhiteSpace(option.Label) && option.Label.Length <= 120 &&
            option.Help is not null && option.Help.Length <= 500 &&
            double.IsFinite(option.Minimum + option.Maximum + option.Step + option.Default) &&
            option.Minimum >= limits.Minimum && option.Maximum <= limits.Maximum && option.Minimum < option.Maximum &&
            option.Step > 0 && option.Step <= option.Maximum - option.Minimum && option.Default >= option.Minimum && option.Default <= option.Maximum &&
            (option.Id != "maxDetections" || option.Minimum == Math.Truncate(option.Minimum) && option.Maximum == Math.Truncate(option.Maximum) &&
                option.Default == Math.Truncate(option.Default) && option.Step == Math.Truncate(option.Step));
    }

    public static double OptionValue(AiModelDefinition model, DetectionSettings settings, string id) => id switch
    {
        "confidence" => settings.AiConfidence,
        "framesPerSecond" => settings.AiFramesPerSecond,
        _ => settings.AiOptions.GetValueOrDefault(id, model.Options.FirstOrDefault(option => option.Id == id)?.Default ??
            AiModelCatalog.DefaultOptions.Single(option => option.Id == id).Default)
    };

    public static string? ValidateSettings(AiModelDefinition model, DetectionSettings settings)
    {
        if (settings.AiClasses is null || settings.AiIgnoreClasses is null || settings.AiOptions is null ||
            settings.AiClasses.Length + settings.AiIgnoreClasses.Length is < 1 or > 1000 ||
            settings.AiClasses.Concat(settings.AiIgnoreClasses).Distinct().Count() != settings.AiClasses.Length + settings.AiIgnoreClasses.Length ||
            settings.AiClasses.Concat(settings.AiIgnoreClasses).Any(label => !model.Labels.Contains(label, StringComparer.Ordinal)) ||
            settings.AiDevice is not ("cpu" or "gpu" or "auto") ||
            settings.AiOptions.Keys.Any(id => id is "confidence" or "framesPerSecond" || !model.Options.Any(option => option.Id == id)))
        {
            return "Choose supported, distinct trigger/ignore classes and options for the selected model.";
        }
        foreach (var option in model.Options)
        {
            var value = OptionValue(model, settings, option.Id);
            if (!double.IsFinite(value) || value < option.Minimum || value > option.Maximum ||
                option.Id == "maxDetections" && value != Math.Truncate(value))
            {
                return $"{option.Label} must be within the selected model's supported range.";
            }
        }
        return null;
    }
}
