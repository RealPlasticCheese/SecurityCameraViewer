using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

public interface IAiInferenceService
{
    Task<ObjectAnalysis> AnalyseAsync(AnalysisFrame frame, CameraRecordingSettings settings, CancellationToken token);
}

public interface IModelSession : IDisposable
{
    ModelTensor Run(float[] input, int size, CancellationToken token);
}

public interface IModelSessionFactory
{
    string[] GpuDevices();
    IModelSession Create(string path, AiModelDefinition model, string device);
}

public sealed class ModelSessionFactory : IModelSessionFactory
{
    public string[] GpuDevices() => OpenVinoModelSession.GpuDevices();

    public IModelSession Create(string path, AiModelDefinition model, string device)
    {
        return device == "gpu" ? new OpenVinoModelSession(path, model.InputSize) : new OnnxModelSession(path, model.InputSize);
    }
}

public sealed class OnnxModelSession : IModelSession
{
    private readonly InferenceSession _session;
    private readonly string _inputName;

    public OnnxModelSession(string path, int size)
    {
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        _session = new(path, options);
        try
        {
            if (_session.InputMetadata.Count != 1 || _session.OutputMetadata.Count != 1)
            {
                throw new InvalidOperationException("Use a single-input, single-output detection model matching a supported adapter.");
            }

            var input = _session.InputMetadata.Single();
            if (input.Value.ElementType != typeof(float) || !input.Value.Dimensions.SequenceEqual([1, 3, size, size]) ||
                _session.OutputMetadata.Single().Value.ElementType != typeof(float))
            {
                throw new InvalidOperationException("Model input/output does not match its documented fixed NCHW float32 export.");
            }

            _inputName = input.Key;
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    public ModelTensor Run(float[] input, int size, CancellationToken token)
    {
        using var options = new RunOptions();
        using var cancellation = token.Register(() => options.Terminate = true);
        token.ThrowIfCancellationRequested();
        try
        {
            using var output = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, new DenseTensor<float>(input, [1, 3, size, size]))],
                _session.OutputMetadata.Keys.ToArray(), options);
            token.ThrowIfCancellationRequested();
            var tensor = output.Single().AsTensor<float>();
            return new(tensor.ToArray(), tensor.Dimensions.ToArray());
        }
        catch (OnnxRuntimeException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}

/// <summary>One bounded inference lane for every camera, with one shared session per registered model/device.</summary>
public sealed class AiInferenceService(IAiModelRegistry registry, IModelSessionFactory sessions) : IAiInferenceService, IDisposable
{
    public AiInferenceService(string modelDirectory, IModelSessionFactory sessions) : this(new AiModelRegistry(modelDirectory), sessions)
    {
    }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (IModelSession Session, long Used)> _loaded = [];
    private readonly Dictionary<string, (DateTimeOffset RetryAt, string Message)> _failures = [];
    private string[]? _gpuDevices;

    public string[] GpuDevices => _gpuDevices ??= sessions.GpuDevices();
    public IAiModelRegistry Registry => registry;
    public string ModelDirectory => registry.ModelDirectory;

    public object[] Models() => registry.Models.Select(model => (object)new
    {
        model.Id,
        model.Name,
        model.License,
        model.BuiltIn,
        classes = model.Labels,
        model.Options,
        model.Presets,
        installed = File.Exists(Path.Combine(ModelDirectory, model.FileName))
    }).ToArray();

    public object Catalog() => new
    {
        methods = DetectionMethods.All,
        templates = DetectionTemplateCatalog.Create(),
        models = Models(),
        classes = AiModelCatalog.Classes.Values,
        gpuDevices = GpuDevices,
        modelDirectory = ModelDirectory,
        notices = registry.Notices
    };

    public async Task ReloadModelsAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            foreach (var loaded in _loaded.Values)
            {
                loaded.Session.Dispose();
            }
            _loaded.Clear();
            _failures.Clear();
            registry.Reload();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ObjectAnalysis> AnalyseAsync(AnalysisFrame frame, CameraRecordingSettings settings, CancellationToken token)
    {
        using (ProcessingMetrics.Measure("ai.wait"))
        {
            await _gate.WaitAsync(token);
        }
        try
        {
            token.ThrowIfCancellationRequested();
            var model = registry.Find(settings.AiModel);
            if (model is null)
            {
                return Failed(settings.AiModel, settings.AiDevice, "AI model definition is unavailable. Refresh the model catalog and check its description.");
            }
            if (AiModelRegistry.ValidateSettings(model, settings) is { } settingsError)
            {
                return Failed(model.Id, settings.AiDevice, settingsError);
            }
            var device = settings.AiDevice == "cpu" ? "cpu" : GpuDevices.Length > 0 ? "gpu" : "cpu";
            if (settings.AiDevice == "gpu" && device != "gpu")
            {
                return Failed(model.Id, "gpu", "GPU unavailable. Install the OpenVINO runtime and Intel GPU driver, or select CPU.");
            }

            var watch = Stopwatch.StartNew();
            try
            {
                IObjectModelAdapter adapter = model.Adapter switch
                {
                    "yolox" => new YoloxModelAdapter(model),
                    "ultralytics" or "ultralytics-raw" or "ultralytics-end-to-end" => new UltralyticsModelAdapter(model),
                    _ => throw new InvalidOperationException("Unknown model adapter.")
                };
                var input = adapter.Prepare(frame, model.InputSize);
                ModelTensor output;
                var message = settings.AiDevice == "auto" && device == "cpu"
                    ? "Intel GPU unavailable; Automatic is using CPU." : "Local AI analysis";
                try
                {
                    output = RunModel(model, device, input.Values, token);
                }
                catch (Exception ex) when (settings.AiDevice == "auto" && device == "gpu" && !token.IsCancellationRequested && IsRuntimeFailure(ex))
                {
                    RememberFailure(model, device, "GPU could not run this model.");
                    device = "cpu";
                    output = RunModel(model, device, input.Values, token);
                    message = "GPU could not run this model; Automatic is using CPU.";
                }

                var objects = adapter.Decode(output, input, model.InputSize, settings.AiConfidence,
                    settings.AiClasses.Concat(settings.AiIgnoreClasses).ToArray(),
                    AiModelRegistry.OptionValue(model, settings, "iouThreshold"),
                    (int)AiModelRegistry.OptionValue(model, settings, "maxDetections"));
                return new(objects, new(model.Id, device, watch.Elapsed.TotalMilliseconds, message));
            }
            catch (Exception ex) when (!token.IsCancellationRequested && IsRuntimeFailure(ex))
            {
                var message = ex is InvalidOperationException or FileNotFoundException
                    ? ex.Message : "AI inference failed. Check the model export and runtime installation.";
                if (ex is not FileNotFoundException)
                {
                    RememberFailure(model, device, message);
                }

                return Failed(model.Id, device, message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private ModelTensor RunModel(AiModelDefinition model, string device, float[] input, CancellationToken token)
    {
        IModelSession session;
        using (ProcessingMetrics.Measure("ai.session"))
        {
            session = GetSession(model, device);
        }
        using var measurement = ProcessingMetrics.Measure("ai.inference");
        return session.Run(input, model.InputSize, token);
    }

    private IModelSession GetSession(AiModelDefinition model, string device)
    {
        var key = model.Id + "/" + device;
        if (_failures.TryGetValue(key, out var failure) && failure.RetryAt > DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException(failure.Message);
        }

        if (_loaded.TryGetValue(key, out var loaded))
        {
            _loaded[key] = (loaded.Session, Stopwatch.GetTimestamp());
            return loaded.Session;
        }

        var path = Path.Combine(ModelDirectory, model.FileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"AI model is not installed: {model.FileName}. Open the motion help guide for installation instructions.");
        }

        try
        {
            if (_loaded.Count >= 6)
            {
                var oldest = _loaded.MinBy(item => item.Value.Used);
                oldest.Value.Session.Dispose();
                _loaded.Remove(oldest.Key);
            }

            var session = sessions.Create(path, model, device);
            _loaded.Add(key, (session, Stopwatch.GetTimestamp()));
            _failures.Remove(key);
            return session;
        }
        catch (Exception ex) when (IsRuntimeFailure(ex))
        {
            var message = "AI model could not load. Check the model export and selected runtime.";
            _failures[key] = (DateTimeOffset.UtcNow.AddSeconds(30), message);
            throw new InvalidOperationException(message, ex);
        }
    }

    private void RememberFailure(AiModelDefinition model, string device, string message)
    {
        var key = model.Id + "/" + device;
        if (_loaded.Remove(key, out var loaded))
        {
            loaded.Session.Dispose();
        }

        // Keep the original deadline: repeated status checks must not postpone recovery indefinitely.
        if (!_failures.TryGetValue(key, out var failure) || failure.RetryAt <= DateTimeOffset.UtcNow)
        {
            _failures[key] = (DateTimeOffset.UtcNow.AddSeconds(30), message);
        }
    }

    private static bool IsRuntimeFailure(Exception exception)
    {
        return exception is OnnxRuntimeException or InvalidOperationException or IOException or
            DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;
    }

    private static ObjectAnalysis Failed(string model, string device, string message) => new([], new(model, device, 0, message, true));

    public void Dispose()
    {
        foreach (var loaded in _loaded.Values)
        {
            loaded.Session.Dispose();
        }

        _gate.Dispose();
    }
}
