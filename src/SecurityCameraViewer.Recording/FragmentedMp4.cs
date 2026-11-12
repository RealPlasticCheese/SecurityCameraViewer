using System.Buffers.Binary;
using System.Text;
using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

/// <summary>Reads FFmpeg's fragmented MP4 with video and optional audio without files. Keyframe status
/// and durations use the video track. Box sizes/sample counts are bounded.</summary>
public static class FragmentedMp4
{
    public const int MaximumBoxBytes = 16 * 1024 * 1024;
    public static async Task ReadAsync(Stream stream, Func<byte[], Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token)
    {
        using var header = new MemoryStream();
        byte[]? moof = null;
        VideoTrack? videoTrack = null;
        var prefix = new byte[8];
        byte[]? discard = null;
        while (true)
        {
            var first = await stream.ReadAsync(prefix.AsMemory(0, 1), token);
            if (first == 0)
            {
                return;
            }

            try
            {
                await stream.ReadExactlyAsync(prefix.AsMemory(1), token);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            var size = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            if (size < 8 || size > MaximumBoxBytes)
            {
                throw new InvalidDataException("Unsupported or oversized video fragment. Reduce the camera keyframe interval.");
            }

            var isMovie = prefix.AsSpan(4).SequenceEqual("moov"u8);
            var isFileType = prefix.AsSpan(4).SequenceEqual("ftyp"u8);
            var isFragment = prefix.AsSpan(4).SequenceEqual("moof"u8);
            var isMedia = prefix.AsSpan(4).SequenceEqual("mdat"u8);
            if (isMedia && moof is not null && videoTrack is { } track)
            {
                byte[] combined;
                using (ProcessingMetrics.Measure("media.fragment_copy"))
                {
                    combined = GC.AllocateUninitializedArray<byte>(checked(moof.Length + (int)size));
                    moof.CopyTo(combined, 0);
                    prefix.CopyTo(combined, moof.Length);
                }
                try
                {
                    // Read the payload into its final owner; do not allocate and copy a second mdat.
                    await stream.ReadExactlyAsync(combined.AsMemory(moof.Length + 8), token);
                }
                catch (EndOfStreamException)
                {
                    return;
                }
                var duration = Duration(moof, track, out var presentationEnd);
                if (!double.IsFinite(duration) || duration <= 0 || duration > 30)
                {
                    throw new InvalidDataException("Use a camera keyframe interval shorter than 30 seconds.");
                }
                moof = null;
                var start = StartTime(combined, track);
                await fragment(new(combined, duration, start, StartsWithKeyframe(combined, track))
                {
                    PresentationEndSeconds = start + presentationEnd
                });
                continue;
            }
            if (!isMovie && !isFileType && !isFragment)
            {
                discard ??= new byte[16384];
                var remaining = (int)size - 8;
                try
                {
                    while (remaining > 0)
                    {
                        var length = Math.Min(remaining, discard.Length);
                        await stream.ReadExactlyAsync(discard.AsMemory(0, length), token);
                        remaining -= length;
                    }
                }
                catch (EndOfStreamException)
                {
                    return;
                }
                continue;
            }
            var bytes = GC.AllocateUninitializedArray<byte>((int)size);
            prefix.CopyTo(bytes, 0);
            try
            {
                await stream.ReadExactlyAsync(bytes.AsMemory(8), token);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            if (isMovie || isFileType)
            {
                if (header.Length + size > 1024 * 1024)
                {
                    throw new InvalidDataException("Oversized video initialization.");
                }
                header.Write(bytes);
                if (isMovie)
                {
                    videoTrack = ReadVideoTrack(bytes.AsMemory(8));
                    await initialized(header.ToArray());
                }
            }
            else
            {
                moof = bytes;
            }
        }
    }

    private static VideoTrack ReadVideoTrack(ReadOnlyMemory<byte> movie)
    {
        foreach (var box in Boxes(movie))
        {
            if (!IsType(box, "trak"u8))
            {
                continue;
            }

            var media = Find(box[8..], "mdia")[8..];
            var handler = Find(media, "hdlr").Span;
            if (handler.Length < 20)
            {
                throw new InvalidDataException("Truncated MP4 media handler.");
            }

            if (!handler.Slice(16, 4).SequenceEqual("vide"u8))
            {
                continue;
            }

            var trackHeader = Find(box[8..], "tkhd").Span;
            var mediaHeader = Find(media, "mdhd").Span;
            var trackId = U32(trackHeader, VersionedOffset(trackHeader));
            var timescale = U32(mediaHeader, VersionedOffset(mediaHeader));
            if (trackId == 0 || timescale == 0)
            {
                throw new InvalidDataException("Invalid video track or clock.");
            }

            var defaults = Find(movie, "mvex")[8..];
            foreach (var defaultBox in Boxes(defaults))
            {
                if (IsType(defaultBox, "trex"u8) && U32(defaultBox.Span, 12) == trackId)
                {
                    return new(trackId, timescale, U32(defaultBox.Span, 20), U32(defaultBox.Span, 28));
                }
            }

            throw new InvalidDataException("Missing video track defaults.");
        }

        throw new InvalidDataException("Missing MP4 video track.");
    }

    private static int VersionedOffset(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12 || header[8] > 1)
        {
            throw new InvalidDataException("Unsupported MP4 track header.");
        }

        return header[8] == 1 ? 28 : 20;
    }

    private static double Duration(byte[] moof, VideoTrack track, out double presentationEnd)
    {
        foreach (var box in Boxes(moof.AsMemory(8)))
        {
            if (!IsType(box, "traf"u8))
            {
                continue;
            }

            var traf = box[8..];
            var tfhd = Find(traf, "tfhd");
            if (U32(tfhd.Span, 12) == track.Id)
            {
                var duration = TrackDuration(traf, tfhd.Span, track, out presentationEnd);
                presentationEnd /= track.Timescale;
                return duration / track.Timescale;
            }
        }

        throw new InvalidDataException("Missing video track in MP4 fragment.");
    }

    private static double TrackDuration(ReadOnlyMemory<byte> traf, ReadOnlySpan<byte> tfhd, VideoTrack track, out double presentationEnd)
    {
        var flags = U32(tfhd, 8) & 0xffffff;
        var offset = 16 + ((flags & 1) != 0 ? 8 : 0) + ((flags & 2) != 0 ? 4 : 0);
        var defaultDuration = (flags & 8) != 0 ? U32(tfhd, offset) : track.DefaultSampleDuration;
        double duration = 0;
        presentationEnd = double.NegativeInfinity;
        foreach (var box in Boxes(traf))
        {
            var span = box.Span;
            if (!span.Slice(4, 4).SequenceEqual("trun"u8))
            {
                continue;
            }

            flags = U32(span, 8) & 0xffffff;
            var count = U32(span, 12);
            if (count > 100000)
            {
                throw new InvalidDataException("Oversized fragment sample table.");
            }

            offset = 16 + ((flags & 1) != 0 ? 4 : 0) + ((flags & 4) != 0 ? 4 : 0);
            for (var i = 0; i < count; i++)
            {
                var sampleDuration = (flags & 0x100) != 0 ? U32(span, offset) : defaultDuration;
                var compositionOffset = offset + ((flags & 0x100) != 0 ? 4 : 0) +
                    ((flags & 0x200) != 0 ? 4 : 0) + ((flags & 0x400) != 0 ? 4 : 0);
                var composition = (flags & 0x800) == 0 ? 0 : span[8] == 1
                    ? unchecked((int)U32(span, compositionOffset)) : (double)U32(span, compositionOffset);
                presentationEnd = Math.Max(presentationEnd, duration + composition + sampleDuration);
                duration += sampleDuration;
                offset += ((flags & 0x100) != 0 ? 4 : 0) + ((flags & 0x200) != 0 ? 4 : 0) + ((flags & 0x400) != 0 ? 4 : 0) + ((flags & 0x800) != 0 ? 4 : 0);
                if (offset > span.Length)
                {
                    throw new InvalidDataException("Truncated sample table.");
                }
            }
        }
        return duration;
    }

    private static double StartTime(byte[] fragment, VideoTrack track)
    {
        var movieFragment = Find(fragment, "moof")[8..];
        foreach (var box in Boxes(movieFragment))
        {
            if (!IsType(box, "traf"u8) || U32(Find(box[8..], "tfhd").Span, 12) != track.Id)
            {
                continue;
            }

            var decodeTime = Find(box[8..], "tfdt").Span;
            if (decodeTime.Length < 16 || decodeTime[8] > 1 || decodeTime[8] == 1 && decodeTime.Length < 20)
            {
                throw new InvalidDataException("Invalid MP4 decode timestamp.");
            }

            return (decodeTime[8] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(decodeTime[12..]) : U32(decodeTime, 12)) / (double)track.Timescale;
        }

        throw new InvalidDataException("Missing video decode timestamp.");
    }

    private static bool StartsWithKeyframe(byte[] fragment, VideoTrack track)
    {
        foreach (var box in Boxes(Find(fragment, "moof")[8..]))
        {
            if (!IsType(box, "traf"u8))
            {
                continue;
            }
            var traf = box[8..];
            var header = Find(traf, "tfhd").Span;
            if (U32(header, 12) != track.Id)
            {
                continue;
            }

            var flags = U32(header, 8) & 0xffffff;
            var offset = 16 + ((flags & 1) != 0 ? 8 : 0) + ((flags & 2) != 0 ? 4 : 0) +
                ((flags & 8) != 0 ? 4 : 0) + ((flags & 16) != 0 ? 4 : 0);
            var defaults = (flags & 32) != 0 ? U32(header, offset) : track.DefaultSampleFlags;
            foreach (var run in Boxes(traf))
            {
                if (!IsType(run, "trun"u8) || U32(run.Span, 12) == 0)
                {
                    continue;
                }
                var samples = run.Span;
                flags = U32(samples, 8) & 0xffffff;
                offset = 16 + ((flags & 1) != 0 ? 4 : 0);
                var firstFlags = (flags & 4) != 0 ? U32(samples, offset) :
                    (flags & 0x400) != 0 ? U32(samples, offset + ((flags & 0x100) != 0 ? 4 : 0) + ((flags & 0x200) != 0 ? 4 : 0)) : defaults;
                return (firstFlags & 0x10000) == 0 && (firstFlags & 0x03000000) != 0x01000000;
            }
            throw new InvalidDataException("Missing video samples in MP4 fragment.");
        }
        throw new InvalidDataException("Missing video track in MP4 fragment.");
    }

    private static bool IsType(ReadOnlyMemory<byte> box, ReadOnlySpan<byte> type) =>
        box.Span.Slice(4, 4).SequenceEqual(type);

    private readonly record struct VideoTrack(uint Id, uint Timescale, uint DefaultSampleDuration, uint DefaultSampleFlags);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
        {
            throw new InvalidDataException("Truncated MP4 box.");
        }

        return BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
    }
    private static ReadOnlyMemory<byte> Find(ReadOnlyMemory<byte> bytes, string type) => Boxes(bytes).FirstOrDefault(b => Encoding.ASCII.GetString(b.Span.Slice(4, 4)) == type) is var found && found.Length >= 8
        ? found : throw new InvalidDataException("Missing MP4 metadata.");
    private static IEnumerable<ReadOnlyMemory<byte>> Boxes(ReadOnlyMemory<byte> bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var size = U32(bytes.Span, offset);
            if (size < 8 || size > bytes.Length - offset)
            {
                throw new InvalidDataException("Invalid MP4 box length.");
            }

            yield return bytes.Slice(offset, (int)size);
            offset += (int)size;
        }
    }
}
