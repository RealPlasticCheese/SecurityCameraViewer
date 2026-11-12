using System.Buffers.Binary;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

public sealed record LiveMediaInitialization(byte[] Bytes, string MimeType, bool HasAudio, byte? H264Profile = null);

public interface ILiveMediaSource
{
    Task RunAsync(ViewerSettings settings, string password, int channel,
        Func<LiveMediaInitialization, Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token);
}

/// <summary>Produces a browser-compatible, synchronized live stream without recording files.</summary>
public sealed class FfmpegLiveMediaSource(IFfmpegProcess process, ICameraConnectionRouter? connectionRouter = null,
    LiveMediaOptions? options = null) : ILiveMediaSource
{
    public async Task RunAsync(ViewerSettings settings, string password, int channel,
        Func<LiveMediaInitialization, Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token)
    {
        var budgets = options ?? new LiveMediaOptions();
        budgets.Validate();
        token.ThrowIfCancellationRequested();
        var connection = connectionRouter?.Resolve(settings with
        {
            Substream = false
        }, password, channel) ?? new CameraConnection(settings, password);
        var input = FfmpegRtspInput.Create(connection.Settings with
        {
            Substream = false
        }, connection.Password, channel, includeAudio: true);
        var published = false;
        if (!budgets.ForceTranscoding)
        {
            try
            {
                await RunAsync(false);
                return;
            }
            catch (IncompatibleLiveMediaException)
            {
                // A new initialization starts a clean epoch if a resource incompatibility was discovered later.
            }
            catch (VideoSourceException) when (!published && !token.IsCancellationRequested)
            {
                // Some source codecs cannot be placed in MP4. Keep the existing software compatibility path.
            }
        }

        token.ThrowIfCancellationRequested();
        await RunAsync(true);

        Task RunAsync(bool transcode)
        {
            string[] inputArguments =
            [
                "-threads", "2", "-f", "concat", "-safe", "0", "-protocol_whitelist", "pipe,tcp,udp,rtp", "-i", "pipe:0",
                "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn"
            ];
            string[] videoArguments = transcode ?
            [
                "-filter_threads", "1",
                "-vf", "select='isnan(prev_selected_t)+gte(t-prev_selected_t,0.066)',scale=w='min(1280,iw)':h='min(720,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2",
                "-fps_mode", "vfr", "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
                "-profile:v", "baseline", "-level:v", "3.1", "-pix_fmt", "yuv420p", "-threads:v", "2",
                "-force_key_frames", "expr:gte(t,n_forced*1)", "-maxrate", "2M", "-bufsize", "4M"
            ] : ["-c:v", "copy"];
            string[] outputArguments =
            [
                "-c:a", "aac", "-profile:a", "aac_low", "-b:a", "64k", "-ar", "48000", "-ac", "1", "-threads:a", "1",
                "-movflags", "+empty_moov+frag_keyframe+default_base_moof+negative_cts_offsets",
                "-frag_duration", "1000000", "-flush_packets", "1", "-f", "mp4", "pipe:1"
            ];
            return process.RunAsync(inputArguments.Concat(videoArguments).Concat(outputArguments), input,
                (stream, ct) => FragmentedMp4.ReadAsync(stream,
                    async bytes =>
                    {
                        LiveMediaInitialization description;
                        try
                        {
                            description = Describe(bytes);
                        }
                        catch (InvalidDataException) when (!transcode)
                        {
                            throw new IncompatibleLiveMediaException();
                        }
                        if (!transcode && description.H264Profile is { } profile && profile is not (66 or 77 or 100))
                        {
                            // Only H.264 uses this profile allowlist; HEVC keeps its own codec metadata.
                            // High 10/4:2:2/4:4:4 H.264 profiles need the compatibility path.
                            throw new IncompatibleLiveMediaException();
                        }
                        published = true;
                        await initialized(description);
                    }, item =>
                    {
                        if (!transcode && item.Bytes.Length > budgets.MaximumQueuedBytes)
                        {
                            throw new IncompatibleLiveMediaException();
                        }
                        return fragment(item);
                    }, ct), token, transcode ? "live.transcode" : "live.copy", channel);
        }
    }

    private sealed class IncompatibleLiveMediaException : Exception;

    public static LiveMediaInitialization Describe(byte[] initialization)
    {
        var movie = Find(initialization, "moov"u8)[8..];
        string? videoCodec = null;
        byte? h264Profile = null;
        var hasAudio = false;
        foreach (var track in Boxes(movie))
        {
            if (!IsType(track, "trak"u8))
            {
                continue;
            }

            var media = Find(track[8..], "mdia"u8)[8..];
            var handler = Find(media, "hdlr"u8);
            if (handler.Length < 20)
            {
                throw new InvalidDataException("Truncated live media handler.");
            }

            hasAudio |= handler.Span.Slice(16, 4).SequenceEqual("soun"u8);
            if (!handler.Span.Slice(16, 4).SequenceEqual("vide"u8))
            {
                continue;
            }

            var table = Find(Find(media, "minf"u8)[8..], "stbl"u8)[8..];
            var description = Find(table, "stsd"u8);
            if (description.Length < 16)
            {
                throw new InvalidDataException("Truncated live video description.");
            }

            var sample = Boxes(description[16..]).FirstOrDefault(box =>
                IsType(box, "avc1"u8) || IsType(box, "avc3"u8) || IsType(box, "hvc1"u8) || IsType(box, "hev1"u8));
            if (sample.Length < 86)
            {
                throw new InvalidDataException("Truncated live video sample entry.");
            }

            var hevc = IsType(sample, "hvc1"u8) || IsType(sample, "hev1"u8);
            var configuration = Find(sample[86..], hevc ? "hvcC"u8 : "avcC"u8);
            if (hevc)
            {
                if (configuration.Length < 31)
                {
                    throw new InvalidDataException("Truncated live H.265 codec.");
                }
                var data = configuration.Span[8..];
                var compatibility = BinaryPrimitives.ReadUInt32BigEndian(data[2..]);
                uint reversed = 0;
                for (var bit = 0; bit < 32; bit++)
                {
                    reversed = (reversed << 1) | ((compatibility >> bit) & 1);
                }
                var constraintLength = 6;
                while (constraintLength > 0 && data[5 + constraintLength] == 0)
                {
                    constraintLength--;
                }
                var constraints = string.Join('.', data.Slice(6, constraintLength).ToArray().Select(value => value.ToString("X2")));
                var space = (data[1] >> 6) == 0 ? "" : ((char)('A' + (data[1] >> 6) - 1)).ToString();
                var sampleType = System.Text.Encoding.ASCII.GetString(sample.Span.Slice(4, 4));
                videoCodec = $"{sampleType}.{space}{data[1] & 31}.{reversed:X}.{((data[1] & 32) == 0 ? 'L' : 'H')}{data[12]}" +
                    (constraints.Length > 0 ? "." + constraints : "");
                continue;
            }
            if (configuration.Length < 12)
            {
                throw new InvalidDataException("Truncated live video codec.");
            }

            videoCodec = System.Text.Encoding.ASCII.GetString(sample.Span.Slice(4, 4)) + "." + Convert.ToHexString(configuration.Span.Slice(9, 3));
            h264Profile = configuration.Span[9];
        }

        if (videoCodec is null)
        {
            throw new InvalidDataException("Missing live H.264/H.265 video track.");
        }

        var codecs = hasAudio ? videoCodec + ",mp4a.40.2" : videoCodec;
        return new(initialization, $"video/mp4; codecs=\"{codecs}\"", hasAudio, h264Profile);
    }

    private static ReadOnlyMemory<byte> Find(ReadOnlyMemory<byte> bytes, ReadOnlySpan<byte> type)
    {
        foreach (var box in Boxes(bytes))
        {
            if (IsType(box, type))
            {
                return box;
            }
        }

        throw new InvalidDataException("Missing live MP4 metadata.");
    }

    private static bool IsType(ReadOnlyMemory<byte> box, ReadOnlySpan<byte> type) =>
        box.Span.Slice(4, 4).SequenceEqual(type);

    private static IEnumerable<ReadOnlyMemory<byte>> Boxes(ReadOnlyMemory<byte> bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8)
            {
                throw new InvalidDataException("Truncated MP4 box header.");
            }
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.Span[offset..]);
            if (size < 8 || size > bytes.Length - offset)
            {
                throw new InvalidDataException("Invalid live MP4 box size.");
            }

            yield return bytes.Slice(offset, (int)size);
            offset += (int)size;
        }
    }
}
