using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SecurityCameraViewer.Recording;

public sealed record AiModelMetadata(string[] Labels, int? InputSize);
public interface IAiModelMetadataReader
{
    AiModelMetadata Read(string path);
}

/// <summary>Reads bounded ONNX ModelProto metadata; skips graph/weights without creating an inference session.</summary>
public sealed class OnnxModelMetadataReader : IAiModelMetadataReader
{
    public AiModelMetadata Read(string path)
    {
        if (!File.Exists(path))
        {
            return new([], null);
        }
        using var stream = File.OpenRead(path);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var fields = 0;
        var metadataBytes = 0L;
        while (stream.Position < stream.Length)
        {
            if (++fields > 4096)
            {
                throw new InvalidDataException("ONNX metadata exceeds supported bounds.");
            }
            var tag = Varint(stream);
            if (tag == (14 << 3 | 2)) // ModelProto.metadata_props, StringStringEntryProto.
            {
                if (metadata.Count >= 64)
                {
                    throw new InvalidDataException("Too many ONNX metadata entries.");
                }
                using var entry = new MemoryStream(Bytes(stream, 128 * 1024));
                metadataBytes += entry.Length;
                if (metadataBytes > 512 * 1024)
                {
                    throw new InvalidDataException("ONNX metadata exceeds 512 KB.");
                }
                string? key = null;
                string? value = null;
                while (entry.Position < entry.Length)
                {
                    var entryTag = Varint(entry);
                    if (entryTag == 10)
                    {
                        key = Encoding.UTF8.GetString(Bytes(entry, 256));
                    }
                    else if (entryTag == 18)
                    {
                        value = Encoding.UTF8.GetString(Bytes(entry, 128 * 1024));
                    }
                    else
                    {
                        Skip(entry, entryTag);
                    }
                }
                if (key is null || value is null || !metadata.TryAdd(key, value))
                {
                    throw new InvalidDataException("ONNX metadata entries are incomplete or duplicated.");
                }
            }
            else
            {
                Skip(stream, tag);
            }
        }
        if (metadata.TryGetValue("task", out var task) && task != "detect")
        {
            throw new InvalidDataException("Only object detection models are supported by these adapters.");
        }
        var labels = metadata.TryGetValue("names", out var names) ? Labels(names) : [];
        int? size = null;
        if (metadata.TryGetValue("imgsz", out var imageSize))
        {
            using var document = JsonDocument.Parse(imageSize);
            var dimensions = document.RootElement.EnumerateArray().Select(value => value.GetInt32()).ToArray();
            if (dimensions.Length != 2 || dimensions[0] != dimensions[1])
            {
                throw new InvalidDataException("Model metadata must specify a square input size.");
            }
            size = dimensions[0];
        }
        return new(labels, size);
    }

    private static byte[] Bytes(Stream stream, int maximum)
    {
        var length = Varint(stream);
        if (length > (ulong)maximum || length > (ulong)(stream.Length - stream.Position))
        {
            throw new InvalidDataException("ONNX metadata is truncated or oversized.");
        }
        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static ulong Varint(Stream stream)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var next = stream.ReadByte();
            if (next < 0 || shift == 63 && next > 1)
            {
                throw new InvalidDataException("Invalid ONNX metadata encoding.");
            }
            value |= (ulong)(next & 127) << shift;
            if ((next & 128) == 0)
            {
                return value;
            }
        }
        throw new InvalidDataException("Invalid ONNX metadata encoding.");
    }

    private static void Skip(Stream stream, ulong tag)
    {
        if (tag >> 3 == 0)
        {
            throw new InvalidDataException("Invalid ONNX field.");
        }
        ulong length;
        switch (tag & 7)
        {
            case 0:
                _ = Varint(stream);
                return;
            case 1:
                length = 8;
                break;
            case 2:
                length = Varint(stream);
                break;
            case 5:
                length = 4;
                break;
            default:
                throw new InvalidDataException("Unsupported ONNX metadata encoding.");
        }
        if (length > (ulong)(stream.Length - stream.Position))
        {
            throw new InvalidDataException("ONNX model is truncated.");
        }
        stream.Seek((long)length, SeekOrigin.Current);
    }

    private static string[] Labels(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                return document.RootElement.EnumerateArray().Select(value => value.GetString()!).ToArray();
            }
            return Ordered(document.RootElement.EnumerateObject().ToDictionary(
                property => int.Parse(property.Name, CultureInfo.InvariantCulture), property => property.Value.GetString()!));
        }
        catch (JsonException)
        {
            // Ultralytics stores a Python dictionary literal. Parse only integer keys and quoted strings; never evaluate it.
            var position = 0;
            var labels = new Dictionary<int, string>();
            Expect('{');
            while (true)
            {
                Space();
                if (position < text.Length && text[position] == '}')
                {
                    position++;
                    break;
                }
                var start = position;
                while (position < text.Length && char.IsAsciiDigit(text[position]))
                {
                    position++;
                }
                if (!int.TryParse(text.AsSpan(start, position - start), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    throw new InvalidDataException("Class metadata needs integer indices.");
                }
                Expect(':');
                Space();
                if (position >= text.Length || text[position] is not ('\'' or '"'))
                {
                    throw new InvalidDataException("Class metadata needs quoted labels.");
                }
                var quote = text[position++];
                var label = new StringBuilder();
                var closed = false;
                while (position < text.Length)
                {
                    var character = text[position++];
                    if (character == quote)
                    {
                        closed = true;
                        break;
                    }
                    if (character == '\\')
                    {
                        if (position >= text.Length)
                        {
                            throw new InvalidDataException("Class metadata has an incomplete escape.");
                        }
                        character = text[position++];
                        if (character == 'u')
                        {
                            if (position + 4 > text.Length || !ushort.TryParse(text.AsSpan(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            {
                                throw new InvalidDataException("Class metadata has an invalid escape.");
                            }
                            character = (char)code;
                            position += 4;
                        }
                        else if (character is not ('\'' or '"' or '\\'))
                        {
                            throw new InvalidDataException("Class metadata has an unsupported escape.");
                        }
                    }
                    label.Append(character);
                }
                if (!closed || !labels.TryAdd(index, label.ToString()) || labels.Count > 1000)
                {
                    throw new InvalidDataException("Class metadata is incomplete, duplicated or oversized.");
                }
                Space();
                if (position < text.Length && text[position] == ',')
                {
                    position++;
                }
                else
                {
                    Expect('}');
                    break;
                }
            }
            Space();
            if (position != text.Length)
            {
                throw new InvalidDataException("Class metadata contains unsupported content.");
            }
            return Ordered(labels);

            void Space()
            {
                while (position < text.Length && char.IsWhiteSpace(text[position]))
                {
                    position++;
                }
            }
            void Expect(char character)
            {
                Space();
                if (position >= text.Length || text[position++] != character)
                {
                    throw new InvalidDataException("Class metadata has an invalid format.");
                }
            }
        }
    }

    private static string[] Ordered(Dictionary<int, string> labels)
    {
        if (labels.Count is < 1 or > 1000 || !labels.Keys.Order().SequenceEqual(Enumerable.Range(0, labels.Count)))
        {
            throw new InvalidDataException("Class indices must be consecutive, starting at zero.");
        }
        return labels.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
    }
}
