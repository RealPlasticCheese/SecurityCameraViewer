using System.Runtime.InteropServices;

namespace SecurityCameraViewer.Recording;

/// <summary>Optional OpenVINO C runtime backend. No dependency on web hosting or camera policy.</summary>
public sealed class OpenVinoModelSession : IModelSession
{
    private nint _core;
    private nint _compiled;
    private nint _request;
    private readonly int _size;

    public OpenVinoModelSession(string path, int size, string device = "GPU")
    {
        _size = size;
        try
        {
            OpenVinoNative.Check(OpenVinoNative.CoreCreate(out _core));
            OpenVinoNative.Check(OpenVinoNative.CompileFromFile(_core, path, device, 0, out _compiled));
            OpenVinoNative.Check(OpenVinoNative.CreateRequest(_compiled, out _request));
            OpenVinoNative.Check(OpenVinoNative.GetInput(_request, out var input));
            try
            {
                var shape = OpenVinoNative.Dimensions(input);
                OpenVinoNative.Check(OpenVinoNative.GetElementType(input, out var type));
                if (type != 4 || !shape.SequenceEqual([1, 3, size, size]))
                {
                    throw new InvalidOperationException("GPU model input must be the documented fixed NCHW float32 export.");
                }
            }
            finally
            {
                OpenVinoNative.TensorFree(input);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static string[] GpuDevices()
    {
        nint core = 0;
        try
        {
            OpenVinoNative.Check(OpenVinoNative.CoreCreate(out core));
            OpenVinoNative.Check(OpenVinoNative.GetDevices(core, out var devices));
            try
            {
                var names = new List<string>();
                for (var i = 0; i < checked((int)devices.Count); i++)
                {
                    var name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(devices.Names, i * nint.Size));
                    if (name is not null && name.StartsWith("GPU", StringComparison.Ordinal))
                    {
                        names.Add(name);
                    }
                }

                return [.. names];
            }
            finally
            {
                OpenVinoNative.DevicesFree(ref devices);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            return [];
        }
        finally
        {
            if (core != 0)
            {
                OpenVinoNative.CoreFree(core);
            }
        }
    }

    public ModelTensor Run(float[] input, int size, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_request == 0, this);
        if (size != _size)
        {
            throw new ArgumentException("Unexpected model input size.", nameof(size));
        }

        token.ThrowIfCancellationRequested();
        OpenVinoNative.Check(OpenVinoNative.GetInput(_request, out var tensor));
        try
        {
            OpenVinoNative.Check(OpenVinoNative.TensorData(tensor, out var data));
            Marshal.Copy(input, 0, data, input.Length);
        }
        finally
        {
            OpenVinoNative.TensorFree(tensor);
        }

        // The request remains owned by this lane until the native call has completed.
        OpenVinoNative.Check(OpenVinoNative.StartAsync(_request));
        using var cancellation = token.Register(() => OpenVinoNative.Cancel(_request));
        var result = OpenVinoNative.Wait(_request);
        token.ThrowIfCancellationRequested();
        OpenVinoNative.Check(result);
        OpenVinoNative.Check(OpenVinoNative.GetOutput(_request, out var output));
        try
        {
            var shape = OpenVinoNative.Dimensions(output);
            OpenVinoNative.Check(OpenVinoNative.GetElementType(output, out var type));
            if (type != 4)
            {
                throw new InvalidOperationException("GPU model output must be float32.");
            }

            var length = shape.Aggregate(1, (a, b) => checked(a * b));
            if (length > 2_000_000)
            {
                throw new InvalidOperationException("GPU model output exceeds the supported detection size.");
            }

            OpenVinoNative.Check(OpenVinoNative.TensorData(output, out var data));
            var values = new float[length];
            Marshal.Copy(data, values, 0, length);
            return new(values, shape);
        }
        finally
        {
            OpenVinoNative.TensorFree(output);
        }
    }

    public void Dispose()
    {
        if (_request != 0)
        {
            OpenVinoNative.RequestFree(_request);
            _request = 0;
        }

        if (_compiled != 0)
        {
            OpenVinoNative.CompiledFree(_compiled);
            _compiled = 0;
        }

        if (_core != 0)
        {
            OpenVinoNative.CoreFree(_core);
            _core = 0;
        }
    }
}

internal static class OpenVinoNative
{
    private const string Library = "openvino_c";

    static OpenVinoNative()
    {
        // Optional absolute library path helps deployments with versioned OpenVINO shared libraries.
        NativeLibrary.SetDllImportResolver(typeof(OpenVinoNative).Assembly, (name, assembly, searchPath) =>
        {
            var path = Environment.GetEnvironmentVariable("SCV_OPENVINO_LIBRARY");
            return name == Library && !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                ? NativeLibrary.Load(path) : 0;
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Devices
    {
        public nint Names;
        public nuint Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Shape
    {
        public long Rank;
        public nint Values;
    }

    internal static void Check(int status)
    {
        if (status != 0)
        {
            throw new InvalidOperationException("OpenVINO could not complete AI inference. Check the runtime, GPU driver and model export.");
        }
    }

    internal static int[] Dimensions(nint tensor)
    {
        Check(GetShape(tensor, out var shape));
        try
        {
            if (shape.Rank is < 1 or > 4)
            {
                throw new InvalidOperationException("Unsupported model tensor rank.");
            }

            var dimensions = new long[shape.Rank];
            Marshal.Copy(shape.Values, dimensions, 0, dimensions.Length);
            return dimensions.Select(value => checked((int)value)).ToArray();
        }
        finally
        {
            ShapeFree(ref shape);
        }
    }

    [DllImport(Library, EntryPoint = "ov_core_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CoreCreate(out nint core);
    [DllImport(Library, EntryPoint = "ov_core_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void CoreFree(nint core);
    [DllImport(Library, EntryPoint = "ov_core_get_available_devices", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetDevices(nint core, out Devices devices);
    [DllImport(Library, EntryPoint = "ov_available_devices_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void DevicesFree(ref Devices devices);
    [DllImport(Library, EntryPoint = "ov_core_compile_model_from_file", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CompileFromFile(nint core, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string device, nuint properties, out nint model);
    [DllImport(Library, EntryPoint = "ov_compiled_model_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void CompiledFree(nint model);
    [DllImport(Library, EntryPoint = "ov_compiled_model_create_infer_request", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CreateRequest(nint model, out nint request);
    [DllImport(Library, EntryPoint = "ov_infer_request_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void RequestFree(nint request);
    [DllImport(Library, EntryPoint = "ov_infer_request_get_input_tensor", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetInput(nint request, out nint tensor);
    [DllImport(Library, EntryPoint = "ov_infer_request_get_output_tensor", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetOutput(nint request, out nint tensor);
    [DllImport(Library, EntryPoint = "ov_infer_request_start_async", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int StartAsync(nint request);
    [DllImport(Library, EntryPoint = "ov_infer_request_wait", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Wait(nint request);
    [DllImport(Library, EntryPoint = "ov_infer_request_cancel", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Cancel(nint request);
    [DllImport(Library, EntryPoint = "ov_tensor_data", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int TensorData(nint tensor, out nint data);
    [DllImport(Library, EntryPoint = "ov_tensor_get_element_type", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetElementType(nint tensor, out int type);
    [DllImport(Library, EntryPoint = "ov_tensor_get_shape", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetShape(nint tensor, out Shape shape);
    [DllImport(Library, EntryPoint = "ov_shape_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ShapeFree(ref Shape shape);
    [DllImport(Library, EntryPoint = "ov_tensor_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void TensorFree(nint tensor);
}
