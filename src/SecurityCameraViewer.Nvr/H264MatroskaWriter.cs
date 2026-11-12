using System.Buffers.Binary;
using System.Text;

namespace SecurityCameraViewer.Nvr;

public sealed record NvrCaptureProgress(TimeSpan RecordedDuration, long VideoBytes);

/// <summary>Packages elementary H.264 frames with their original timing, including gaps, for a media converter.</summary>
public static class H264MatroskaWriter
{
    public static Task WriteAsync(Stream destination, IAsyncEnumerable<NvrVideoFrame> frames, CancellationToken token) =>
        WriteAsync(destination, frames, null, token);

    public static async Task WriteAsync(Stream destination, IAsyncEnumerable<NvrVideoFrame> frames,
        Action<NvrCaptureProgress>? reportProgress, CancellationToken token)
    {
        ulong? firstTimestamp = null;
        ulong lastTimestamp = 0;
        long totalBytes = 0;
        long videoBytes = 0;
        await foreach (var frame in frames.WithCancellation(token))
        {
            if (frame.Codec != NvrVideoCodec.H264 || frame.Width is < 1 or > 8192 || frame.Height is < 1 or > 8192)
            {
                throw new NvrException("This recording's video format is not supported.");
            }

            var units = SplitNalUnits(frame.Data.Span);
            if (firstTimestamp is null)
            {
                if (!frame.KeyFrame)
                {
                    continue;
                }

                var sps = units.FirstOrDefault(n => (n[0] & 31) == 7);
                var pps = units.FirstOrDefault(n => (n[0] & 31) == 8);
                if (sps is null || sps.Length < 4 || pps is null || sps.Length > ushort.MaxValue || pps.Length > ushort.MaxValue)
                {
                    throw new NvrException("The recording has no H.264 decoder configuration.");
                }

                firstTimestamp = frame.TimestampMicroseconds;
                await destination.WriteAsync(BuildHeader(frame, sps, pps), token);
            }

            if (frame.TimestampMicroseconds < firstTimestamp || frame.TimestampMicroseconds < lastTimestamp ||
                frame.TimestampMicroseconds - firstTimestamp > 90UL * 60 * 1_000_000)
            {
                throw new NvrException("The recording's timestamps are inconsistent or exceed the export limit.");
            }

            lastTimestamp = frame.TimestampMicroseconds;
            using var block = new MemoryStream();
            block.Write([0x81, 0, 0, frame.KeyFrame ? (byte)0x80 : (byte)0]);
            foreach (var unit in units)
            {
                var length = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(length, unit.Length);
                block.Write(length);
                block.Write(unit);
            }

            var cluster = Element(0x1F43B675, Combine(Integer(0xE7, (frame.TimestampMicroseconds - firstTimestamp.Value) / 1000), Element(0xA3, block.ToArray())));
            totalBytes += cluster.Length;
            if (totalBytes > 1024L * 1024 * 1024)
            {
                throw new NvrException("The export exceeds 1 GiB. Select a shorter time range.");
            }

            await destination.WriteAsync(cluster, token);
            videoBytes += frame.Data.Length;
            // Synchronous reporting keeps frame updates ordered before completion and cancellation.
            reportProgress?.Invoke(new(TimeSpan.FromTicks((long)((frame.TimestampMicroseconds - firstTimestamp.Value) * 10)), videoBytes));
        }

        if (firstTimestamp is null)
        {
            throw new NvrException("The NVR returned no playable video for this time range.");
        }
    }

    internal static List<byte[]> SplitNalUnits(ReadOnlySpan<byte> data)
    {
        var result = new List<byte[]>();
        var start = -1;
        for (var i = 0; i + 2 < data.Length; i++)
        {
            var prefixLength = data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 ? 3 :
                i + 3 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1 ? 4 : 0;
            if (prefixLength == 0)
            {
                continue;
            }

            if (start >= 0 && i > start)
            {
                result.Add(data[start..i].ToArray());
            }

            start = i + prefixLength;
            i = start - 1;
        }

        if (start >= 0 && start < data.Length)
        {
            result.Add(data[start..].ToArray());
        }

        if (result.Count == 0)
        {
            throw new NvrException("The recording contains an invalid H.264 frame.");
        }

        return result;
    }

    private static byte[] BuildHeader(NvrVideoFrame frame, byte[] sps, byte[] pps)
    {
        var configuration = new byte[11 + sps.Length + pps.Length];
        new byte[] { 1, sps[1], sps[2], sps[3], 0xFF, 0xE1 }.CopyTo(configuration, 0);
        BinaryPrimitives.WriteUInt16BigEndian(configuration.AsSpan(6), (ushort)sps.Length);
        sps.CopyTo(configuration, 8);
        configuration[8 + sps.Length] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(configuration.AsSpan(9 + sps.Length), (ushort)pps.Length);
        pps.CopyTo(configuration, 11 + sps.Length);
        return Combine(
            Element(0x1A45DFA3, Combine(Integer(0x4286, 1), Integer(0x42F7, 1), Integer(0x42F2, 4), Integer(0x42F3, 8),
                Text(0x4282, "matroska"), Integer(0x4287, 4), Integer(0x4285, 2))),
            // Unknown Segment length permits sequential capture without holding the recording in memory.
            [0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
            Element(0x1549A966, Combine(Integer(0x2AD7B1, 1_000_000), Text(0x4D80, "SecurityCameraViewer"), Text(0x5741, "SecurityCameraViewer"))),
            Element(0x1654AE6B, Element(0xAE, Combine(Integer(0xD7, 1), Integer(0x73C5, 1), Integer(0x83, 1),
                Text(0x86, "V_MPEG4/ISO/AVC"), Element(0x63A2, configuration),
                Element(0xE0, Combine(Integer(0xB0, (ulong)frame.Width), Integer(0xBA, (ulong)frame.Height)))))));
    }

    private static byte[] Text(uint id, string value) => Element(id, Encoding.UTF8.GetBytes(value));

    private static byte[] Integer(uint id, ulong value)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(data, value);
        var start = 0;
        while (start < 7 && data[start] == 0)
        {
            start++;
        }

        return Element(id, data[start..]);
    }

    private static byte[] Element(uint id, byte[] data)
    {
        var identifier = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(identifier, id);
        var start = 0;
        while (start < 3 && identifier[start] == 0)
        {
            start++;
        }

        var lengthBytes = 1;
        while ((ulong)data.Length >= (1UL << (7 * lengthBytes)) - 1)
        {
            lengthBytes++;
        }

        var size = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(size, (uint)data.Length | (1UL << (7 * lengthBytes)));
        return Combine(identifier[start..], size[(8 - lengthBytes)..], data);
    }

    private static byte[] Combine(params byte[][] parts)
    {
        using var stream = new MemoryStream();
        foreach (var part in parts)
        {
            stream.Write(part);
        }

        return stream.ToArray();
    }
}
