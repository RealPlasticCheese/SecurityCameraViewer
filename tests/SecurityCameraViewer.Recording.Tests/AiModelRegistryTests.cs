using System.Text;
using System.Text.Json;
using SecurityCameraViewer.Recording;

internal static class AiModelRegistryTests
{
    public static async Task RunAsync(string root)
    {
        var directory = Path.Combine(root, "descriptions");
        Directory.CreateDirectory(directory);
        var registry = new AiModelRegistry(directory);
        var builtIn = registry.Find("yolox-nano")!;
        Check(AiModelCatalog.All.All(model => model.BuiltIn && model.Labels.Length == 80 && model.Labels[16] == "dog" &&
            model.Presets[0].TriggerClasses.SequenceEqual(["person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat"]) &&
            model.Presets[0].IgnoreClasses.Length == 0 && model.Presets[0].Options["confidence"] == 0.45 && model.Presets[0].Device == "cpu" &&
            model.Presets.Single(preset => preset.Id == "original").TriggerClasses.SequenceEqual(["person", "car"])),
            "Built-in defaults trigger people and every supported vehicle while retaining the original selectable preset");

        var custom = new AiModelDefinition("parcel-model", "Parcel camera", "parcel.onnx", 32, "ultralytics-raw", "Test")
        {
            Labels = ["parcel", "cat", "person"]
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "parcel.model.json"), JsonSerializer.Serialize(custom));
        registry.Reload();
        var model = registry.Find(custom.Id)!;
        Check(!model.BuiltIn && model.Presets[0].TriggerClasses.SequenceEqual(custom.Labels) && model.Options.Length == 4,
            "A custom description adds its own classes, generated defaults and settings without source changes");
        var settings = new CameraRecordingSettings { Channel = 1, AiModel = model.Id, AiClasses = ["parcel", "person"], AiIgnoreClasses = ["cat"] };
        var recording = new RecordingSettings { Directory = root, Cameras = [settings] };
        Check(recording.Validate(registry) is null && recording.Validate() is null &&
            (recording with { Cameras = [settings with { AiClasses = ["car"] }] }).Validate(registry) is not null &&
            (recording with { Cameras = [settings with { AiIgnoreClasses = ["parcel"] }] }).Validate(registry) is not null,
            "Persistence accepts custom labels while runtime validation enforces each model and disjoint trigger/ignore roles");
        var raw = new float[7 * 6];
        raw[0] = 16;
        raw[6] = 16;
        raw[12] = 10;
        raw[18] = 10;
        raw[24] = 0.7f;
        raw[30] = 0.9f;
        var adapter = new UltralyticsModelAdapter(model);
        var frame = new AnalysisFrame(new byte[32 * 32 * 3], 32, 32, true, DateTimeOffset.UtcNow);
        var input = adapter.Prepare(frame, 32);
        Check(adapter.Decode(new(raw, [1, 7, 6]), input, 32, 0.45, ["cat"]).Single().Class == "cat" &&
            adapter.Decode(new(raw, [1, 7, 6]), input, 32, 0.45, ["parcel"]).Length == 0,
            "Custom class counts and explicit raw layouts decode without reclassifying an ignored winning class");
        var endToEnd = new UltralyticsModelAdapter(model with
        {
            Adapter = "ultralytics-end-to-end"
        });
        var overlapping = new ModelTensor([2, 2, 12, 12, 0.9f, 0, 3, 3, 13, 13, 0.8f, 0], [1, 2, 6]);
        Check(endToEnd.Decode(overlapping, input, 32, 0.45, ["parcel"], 0.9).Length == 2 &&
            endToEnd.Decode(overlapping, input, 32, 0.45, ["parcel"], 0.4).Length == 1 &&
            endToEnd.Decode(overlapping, input, 32, 0.45, ["parcel"], 0.9, 1).Length == 1,
            "Supported descriptor options control duplicate suppression and retained-object bounds");

        WriteMetadata(Path.Combine(directory, "metadata.onnx"), "{0: 'parcel', 1: 'cat', 2: 'person'}");
        await File.WriteAllTextAsync(Path.Combine(directory, "metadata.model.json"), JsonSerializer.Serialize(custom with
        {
            Id = "metadata-model",
            FileName = "metadata.onnx",
            Labels = [],
            InputSize = 0
        }));
        registry.Reload();
        Check(registry.Find("metadata-model") is { InputSize: 32 } fromMetadata && fromMetadata.Labels.SequenceEqual(custom.Labels),
            "Embedded ONNX labels and square input metadata complete a model description without an inference session");
        WriteMetadata(Path.Combine(directory, "invalid.onnx"), "{0: __import__('os').system('anything')}");
        await File.WriteAllTextAsync(Path.Combine(directory, "invalid.model.json"), JsonSerializer.Serialize(custom with
        {
            Id = "invalid-model",
            FileName = "invalid.onnx",
            Labels = [],
            InputSize = 0
        }));
        await File.WriteAllTextAsync(Path.Combine(directory, "traversal.model.json"), JsonSerializer.Serialize(custom with
        {
            Id = "traversal",
            FileName = "../outside.onnx"
        }));
        await File.WriteAllTextAsync(Path.Combine(directory, "duplicate.model.json"), JsonSerializer.Serialize(custom with
        {
            Id = "yolox-nano"
        }));
        registry.Reload();
        Check(registry.Find("invalid-model") is null && registry.Find("traversal") is null && registry.Notices.Count == 3 &&
            registry.Find("yolox-nano")!.BuiltIn && registry.Find("parcel-model") is not null,
            "Invalid metadata, traversal and built-in overrides are isolated with actionable notices");
        Check(AiModelRegistry.ValidateDefinition(model with { Options = [new("unknown", "Unknown", 0, 1, 0.1, 0.5, "")] }) is not null &&
            AiModelRegistry.ValidateDefinition(model with { Labels = ["cat", "cat"] }) is not null,
            "Descriptions cannot invent executable options or ambiguous class mappings");
        await File.WriteAllTextAsync(Path.Combine(directory, "null-options.model.json"), JsonSerializer.Serialize(custom with
        {
            Id = "null-options",
            Options = [null!, AiModelCatalog.DefaultOptions[1]]
        }));
        registry.Reload();
        Check(registry.Find("null-options") is null && registry.Find("parcel-model") is not null && registry.Notices.Count == 4,
            "Malformed nullable options do not interrupt discovery of other models");
        Console.WriteLine("All model registry checks passed.");
    }

    private static void WriteMetadata(string path, string names)
    {
        using var stream = File.Create(path);
        // An opaque graph is skipped rather than loaded; these files test discovery, not inference.
        Varint(stream, 7 << 3 | 2);
        Varint(stream, 1024 * 1024);
        stream.Write(new byte[1024 * 1024]);
        foreach (var pair in new[] { ("names", names), ("imgsz", "[32, 32]"), ("task", "detect") })
        {
            using var entry = new MemoryStream();
            Text(entry, 10, pair.Item1);
            Text(entry, 18, pair.Item2);
            Varint(stream, 14 << 3 | 2);
            Varint(stream, (ulong)entry.Length);
            entry.Position = 0;
            entry.CopyTo(stream);
        }
    }
    private static void Text(Stream stream, ulong tag, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Varint(stream, tag);
        Varint(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }
    private static void Varint(Stream stream, ulong value)
    {
        while (value >= 128)
        {
            stream.WriteByte((byte)(value | 128));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }
    private static void Check(bool value, string name)
    {
        if (!value)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
    }
}
