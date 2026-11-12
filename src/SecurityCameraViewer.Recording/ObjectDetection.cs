using System.Buffers;
using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

public sealed record DetectedObject(string Class, double Confidence, double X, double Y, double Width, double Height);
public sealed record AiAnalysisStatus(string Model, string Device, double Milliseconds, string Message, bool Failed = false);
public sealed record ObjectAnalysis(DetectedObject[] Objects, AiAnalysisStatus Status);
public sealed record ModelTensor(float[] Values, int[] Dimensions);
public sealed record PreparedImage(float[] Values, double Scale, int Left, int Top, int Width, int Height);

public interface IObjectModelAdapter
{
    PreparedImage Prepare(AnalysisFrame frame, int size);
    DetectedObject[] Decode(ModelTensor output, PreparedImage image, int size, double confidence, string[] classes,
        double iouThreshold = 0.45, int maximumDetections = 300);
}

/// <summary>Model-specific preprocessing and output decoding, independent of runtime and recording policy.</summary>
public abstract class YoloModelAdapter(bool ultralytics, AiModelDefinition? model = null) : IObjectModelAdapter
{
    private readonly string[] _labels = model?.Labels ?? AiModelCatalog.Classes.Values.ToArray();
    private static readonly float[] NormalizedBytes = Enumerable.Range(0, 256).Select(value => (float)(value / 255.0)).ToArray();
    private readonly record struct Sample(int First, int Second, double Fraction);
    public PreparedImage Prepare(AnalysisFrame frame, int size)
    {
        using var measurement = ProcessingMetrics.Measure("ai.prepare");
        if (!frame.IsRgb || frame.Pixels.Length != checked(frame.Width * frame.Height * 3))
        {
            throw new InvalidOperationException("AI detection requires an RGB analysis frame.");
        }

        var scale = Math.Min((double)size / frame.Width, (double)size / frame.Height);
        var width = Math.Max(1, (int)(frame.Width * scale));
        var height = Math.Max(1, (int)(frame.Height * scale));
        var left = ultralytics ? (size - width) / 2 : 0;
        var top = ultralytics ? (size - height) / 2 : 0;
        var plane = checked(size * size);
        var values = GC.AllocateUninitializedArray<float>(checked(plane * 3));
        Array.Fill(values, ultralytics ? 114f / 255 : 114f);
        var samples = ArrayPool<Sample>.Shared.Rent(width);
        try
        {
            var identity = width == frame.Width && height == frame.Height;
            if (!identity)
            {
                for (var x = 0; x < width; x++)
                {
                    var sourceX = Math.Clamp((x + 0.5) * frame.Width / width - 0.5, 0, frame.Width - 1);
                    var x0 = (int)sourceX;
                    samples[x] = new(x0 * 3, Math.Min(x0 + 1, frame.Width - 1) * 3, sourceX - x0);
                }
            }
            for (var y = 0; y < height; y++)
            {
                var destination = (y + top) * size + left;
                if (identity)
                {
                    var source = y * frame.Width * 3;
                    for (var x = 0; x < width; x++, source += 3, destination++)
                    {
                        values[destination] = ultralytics ? NormalizedBytes[frame.Pixels[source]] : frame.Pixels[source + 2];
                        values[plane + destination] = ultralytics ? NormalizedBytes[frame.Pixels[source + 1]] : frame.Pixels[source + 1];
                        values[2 * plane + destination] = ultralytics ? NormalizedBytes[frame.Pixels[source + 2]] : frame.Pixels[source];
                    }
                    continue;
                }
                var sourceY = Math.Clamp((y + 0.5) * frame.Height / height - 0.5, 0, frame.Height - 1);
                var y0 = (int)sourceY;
                var firstRow = y0 * frame.Width * 3;
                var secondRow = Math.Min(y0 + 1, frame.Height - 1) * frame.Width * 3;
                var fy = sourceY - y0;
                for (var x = 0; x < width; x++, destination++)
                {
                    var sample = samples[x];
                    // Preserve the original bilinear arithmetic and rounding, including padded edges.
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var sourceChannel = ultralytics ? channel : 2 - channel;
                        var value = (frame.Pixels[firstRow + sample.First + sourceChannel] * (1 - sample.Fraction) +
                            frame.Pixels[firstRow + sample.Second + sourceChannel] * sample.Fraction) * (1 - fy) +
                            (frame.Pixels[secondRow + sample.First + sourceChannel] * (1 - sample.Fraction) +
                            frame.Pixels[secondRow + sample.Second + sourceChannel] * sample.Fraction) * fy;
                        values[channel * plane + destination] = (float)(Math.Round(value) / (ultralytics ? 255 : 1));
                    }
                }
            }
        }
        finally
        {
            ArrayPool<Sample>.Shared.Return(samples);
        }

        return new(values, scale, left, top, frame.Width, frame.Height);
    }

    public DetectedObject[] Decode(ModelTensor output, PreparedImage image, int size, double confidence, string[] classes,
        double iouThreshold = 0.45, int maximumDetections = 300)
    {
        var shape = output.Dimensions;
        using var measurement = ProcessingMetrics.Measure("ai.decode");
        if (shape.Length != 3 || shape[0] != 1 || output.Values.Length != shape.Aggregate(1, (a, b) => checked(a * b)))
        {
            throw new InvalidOperationException("The selected model has an unsupported output shape.");
        }

        var endToEnd = ultralytics && model?.Adapter != "ultralytics-raw" && shape[2] == 6;
        var rows = endToEnd ? shape[1] : ultralytics ? shape[2] : shape[1];
        var expectedYoloxRows = new[] { 8, 16, 32 }.Sum(stride => (size / stride) * (size / stride));
        if (ultralytics ? model?.Adapter == "ultralytics-end-to-end" && !endToEnd || !endToEnd && shape[1] != 4 + _labels.Length
            : shape[2] != 5 + _labels.Length || rows != expectedYoloxRows)
        {
            throw new InvalidOperationException("Model output does not match its adapter and ordered class labels. Check the model description and export.");
        }

        var detections = new List<DetectedObject>();
        var gridOffset = 0;
        var strideIndex = 0;
        int[] strides = [8, 16, 32];
        for (var row = 0; row < rows; row++)
        {
            float Value(int column) => output.Values[ultralytics && !endToEnd ? column * rows + row : row * shape[2] + column];
            var classId = endToEnd ? (int)Value(5) : -1;
            var score = endToEnd ? Value(4) : 0;
            if (!endToEnd)
            {
                // Use the model's winning class, then filter: a low-ranked person score must not reclassify another object.
                for (var category = 0; category < _labels.Length; category++)
                {
                    var candidate = Value(category + (ultralytics ? 4 : 5)) * (ultralytics ? 1 : Value(4));
                    if (candidate > score)
                    {
                        score = candidate;
                        classId = category;
                    }
                }
            }

            if (!ultralytics && row - gridOffset >= (size / strides[strideIndex]) * (size / strides[strideIndex]))
            {
                gridOffset += (size / strides[strideIndex]) * (size / strides[strideIndex]);
                strideIndex++;
            }

            if (!float.IsFinite(score) || score < confidence ||
                classId < 0 || classId >= _labels.Length || endToEnd && (!float.IsFinite(Value(5)) || Value(5) != classId) ||
                !classes.Contains(_labels[classId]))
            {
                continue;
            }

            double x = Value(0), y = Value(1), width = Value(2), height = Value(3);
            if (!ultralytics)
            {
                var stride = strides[strideIndex];
                var grid = size / stride;
                x = (x + (row - gridOffset) % grid) * stride;
                y = (y + (row - gridOffset) / grid) * stride;
                width = Math.Exp(width) * stride;
                height = Math.Exp(height) * stride;
            }

            var x1 = endToEnd ? x : x - width / 2;
            var y1 = endToEnd ? y : y - height / 2;
            var x2 = endToEnd ? width : x + width / 2;
            var y2 = endToEnd ? height : y + height / 2;
            x1 = Math.Clamp((x1 - image.Left) / image.Scale / image.Width, 0, 1);
            y1 = Math.Clamp((y1 - image.Top) / image.Scale / image.Height, 0, 1);
            x2 = Math.Clamp((x2 - image.Left) / image.Scale / image.Width, 0, 1);
            y2 = Math.Clamp((y2 - image.Top) / image.Scale / image.Height, 0, 1);
            if (double.IsFinite(x1 + y1 + x2 + y2) && x2 > x1 && y2 > y1)
            {
                detections.Add(new(_labels[classId], score, x1, y1, x2 - x1, y2 - y1));
            }
        }

        var kept = new List<DetectedObject>();
        foreach (var candidate in detections.OrderByDescending(item => item.Confidence))
        {
            if (kept.Any(item => item.Class == candidate.Class && IntersectionOverUnion(item, candidate) > iouThreshold))
            {
                continue;
            }

            kept.Add(candidate);
            if (kept.Count >= maximumDetections)
            {
                break;
            }
        }

        return [.. kept];
    }

    private static double IntersectionOverUnion(DetectedObject a, DetectedObject b)
    {
        var intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) *
            Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return intersection / (a.Width * a.Height + b.Width * b.Height - intersection);
    }
}

public sealed class YoloxModelAdapter(AiModelDefinition? model = null) : YoloModelAdapter(false, model)
{
}

public sealed class UltralyticsModelAdapter(AiModelDefinition? model = null) : YoloModelAdapter(true, model)
{
}
