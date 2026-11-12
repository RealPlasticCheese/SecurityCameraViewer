using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SecurityCameraViewer.Nvr.Raysharp;

/// <summary>Direct TCP implementation of the legacy RaySharp recording protocol; no native SDK dependency.</summary>
public sealed class RaysharpNvrClient : INvrClient, INvrStorageClient
{
    public async Task<NvrStorageInfo> GetStorageInfoAsync(NvrConnection connection, CancellationToken token)
    {
        using var session = new RaysharpSession();
        await session.ConnectAsync(connection, token);
        await session.LoginAsync(connection, token);
        var request = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(request, 36);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(4), 36);
        request.AsSpan(8).Fill(0xFF);
        await session.SendAsync(211, 503, request, token);
        var response = await session.ReceiveAsync(211, 503, token);
        RaysharpSession.RequireSuccess(response);
        return ParseStorage(response.Payload);
    }

    internal static NvrStorageInfo ParseStorage(byte[] data)
    {
        if (data.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(data) != data.Length - 4)
        {
            throw new NvrException("This NVR disk-information format is not supported.");
        }

        var baseSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (baseSize < 8 || baseSize > data.Length - 12)
        {
            throw new NvrException("The NVR returned invalid disk information.");
        }

        var offset = 4 + (int)baseSize;
        var listSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        offset += 4;
        if (listSize != data.Length - offset)
        {
            throw new NvrException("The NVR returned an incomplete disk list.");
        }

        var disks = new List<NvrDiskInfo>();
        var number = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 52)
            {
                throw new NvrException("The NVR returned a truncated disk entry.");
            }

            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            offset += 4;
            if (size < 48 || size > data.Length - offset || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) != size)
            {
                throw new NvrException("The NVR returned an unsupported disk entry.");
            }

            var entry = data.AsSpan(offset);
            // The legacy wrapper converts these KiB counters to MiB by dividing by 1024.
            var capacity = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            var free = BinaryPrimitives.ReadUInt64LittleEndian(entry[24..]);
            number++;
            if (capacity > 0 && capacity <= ulong.MaxValue / 1024 && free <= capacity)
            {
                disks.Add(new(number, capacity * 1024, free * 1024));
            }

            offset += (int)size;
        }

        return new(data[8] == 1, disks);
    }

    public async Task<NvrDeviceInfo> GetDeviceInfoAsync(NvrConnection connection, CancellationToken token)
    {
        using var session = new RaysharpSession();
        await session.ConnectAsync(connection, token);
        return (await session.LoginAsync(connection, token)).Info;
    }

    public async Task<IReadOnlyList<NvrRecording>> SearchAsync(NvrConnection connection, NvrRecordingRequest request, CancellationToken token)
    {
        request.Validate(TimeSpan.FromDays(1));
        using var session = new RaysharpSession();
        await session.ConnectAsync(connection, token);
        var login = await session.LoginAsync(connection, token);
        ValidateChannel(request, login.Info);
        var body = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(body, request.Channel - 1);
        WriteDate(body.AsSpan(4), request.Begin);
        WriteDate(body.AsSpan(12), request.End);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), uint.MaxValue);
        await session.SendAsync(111, 101, body, token);
        var response = await session.ReceiveAsync(111, 101, token);
        RaysharpSession.RequireSuccess(response);
        return ParseRecordings(response.Payload, request.Channel);
    }

    public async IAsyncEnumerable<NvrVideoFrame> ReadRecordingAsync(NvrConnection connection, NvrRecordingRequest request,
        [EnumeratorCancellation] CancellationToken token)
    {
        request.Validate(TimeSpan.FromHours(1));
        using var control = new RaysharpSession();
        await control.ConnectAsync(connection, token);
        var login = await control.LoginAsync(connection, token);
        ValidateChannel(request, login.Info);
        if (!login.Info.CanPlayback || !login.Info.CanExport)
        {
            throw new NvrException("The NVR account must have playback and backup permissions to retrieve saved clips.");
        }

        // The recorder requires a second TCP connection authenticated by the login's user key.
        using var playback = new RaysharpSession();
        await playback.ConnectAsync(connection, token);
        var body = new byte[68];
        BinaryPrimitives.WriteUInt32LittleEndian(body, login.Key);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), request.Channel - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), uint.MaxValue);
        WriteDate(body.AsSpan(12), request.Begin);
        WriteDate(body.AsSpan(20), request.End);
        await playback.SendAsync(121, 0, body, token);
        RaysharpSession.RequireSuccess(await playback.ReceiveAsync(121, 0, token));
        await playback.SendAsync(122, 121, ReadOnlyMemory<byte>.Empty, token);
        var header = new byte[12];
        using var assembly = new MemoryStream();
        uint frameNumber = 0;
        ushort expectedPacket = 0;
        ushort expectedCount = 0;
        var heartbeat = Environment.TickCount64;
        var feedback = heartbeat;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await playback.ReadExactlyAsync(header, token);
            var version = header[0] & 3;
            var type = header[1];
            var count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
            var number = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            var index = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
            var size = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
            if (version is not (0 or 1) || size > 8192)
            {
                throw new NvrException("The NVR uses an unsupported video packet format.");
            }

            var data = new byte[(header[0] >> 2) * 4 + size];
            await playback.ReadExactlyAsync(data, token);
            if (type == 2)
            {
                if (expectedPacket != 0)
                {
                    throw new NvrException("The NVR ended playback during an incomplete frame.");
                }

                yield break;
            }

            if (type != 0 || count == 0 || index >= count)
            {
                throw new NvrException("The NVR sent an unexpected video packet.");
            }

            if (index == 0)
            {
                if (expectedPacket != 0)
                {
                    throw new NvrException("The NVR interrupted a video frame.");
                }

                assembly.SetLength(0);
                frameNumber = number;
                expectedCount = count;
            }

            if (index != expectedPacket || number != frameNumber || count != expectedCount || assembly.Length + size > 8 * 1024 * 1024)
            {
                throw new NvrException("The NVR returned an incomplete or oversized video frame.");
            }

            assembly.Write(data.AsSpan(data.Length - size));
            expectedPacket++;
            if (expectedPacket == count)
            {
                expectedPacket = 0;
                var frame = ParseFrame(assembly.ToArray());
                if (frame is not null)
                {
                    yield return frame;
                }
            }

            var now = Environment.TickCount64;
            if (now - feedback >= 500)
            {
                await playback.SendAsync(122, 121, ReadOnlyMemory<byte>.Empty, token);
                feedback = now;
            }

            if (now - heartbeat >= 5000)
            {
                await control.SendAsync(32, 0, ReadOnlyMemory<byte>.Empty, token);
                heartbeat = now;
            }
        }
    }

    internal static IReadOnlyList<NvrRecording> ParseRecordings(byte[] data, int channel)
    {
        var recordings = new List<NvrRecording>();
        for (var offset = 0; offset < data.Length;)
        {
            if (data.Length - offset < 28)
            {
                throw new NvrException("The NVR returned a truncated recording list.");
            }

            var item = data.AsSpan(offset);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(item);
            if (size < 28 || size > item.Length)
            {
                throw new NvrException("The NVR returned an unsupported recording list.");
            }

            var begin = ReadDate(item[4..]);
            var end = ReadDate(item[12..]);
            if (end <= begin)
            {
                throw new NvrException("The NVR returned an invalid recording time range.");
            }

            recordings.Add(new(channel, begin, end, BinaryPrimitives.ReadUInt32LittleEndian(item[20..]), BinaryPrimitives.ReadUInt32LittleEndian(item[24..])));
            offset += (int)size;
        }

        return recordings.OrderBy(r => r.Begin).ToArray();
    }

    internal static NvrVideoFrame? ParseFrame(byte[] data)
    {
        if (data.Length < 40 || !data.AsSpan(0, 4).SequenceEqual("RSFm"u8))
        {
            throw new NvrException("The NVR returned an unsupported frame header.");
        }

        var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        // Legacy firmware aligns a complete frame to eight bytes. Padding is not codec data.
        if (payloadSize > data.Length - 40 || data.Length - 40 - payloadSize > 7)
        {
            throw new NvrException("The NVR returned an invalid frame length.");
        }

        if (data[4] is not (0x49 or 0x50))
        {
            // Audio and analytics are separate frames. This provider currently exports video only.
            return null;
        }

        if (data[5] != 2 || (data[6] & 0x80) != 0)
        {
            throw new NvrException("Only unencrypted H.264 recordings are currently supported. Select H.264 on the recorder.");
        }

        return new(data.AsMemory(40, (int)payloadSize), NvrVideoCodec.H264, data[4] == 0x49,
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(24)), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(32)),
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(34)));
    }

    private static void ValidateChannel(NvrRecordingRequest request, NvrDeviceInfo info)
    {
        if (request.Channel > info.ChannelCount)
        {
            throw new ArgumentException("That channel is not available on this NVR.");
        }
    }

    private static void WriteDate(Span<byte> buffer, DateTime value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)value.Year);
        buffer[2] = (byte)value.Month;
        buffer[3] = (byte)value.Day;
        buffer[4] = (byte)value.Hour;
        buffer[5] = (byte)value.Minute;
        buffer[6] = (byte)value.Second;
    }

    private static DateTime ReadDate(ReadOnlySpan<byte> data)
    {
        try
        {
            return new(BinaryPrimitives.ReadUInt16LittleEndian(data), data[2], data[3], data[4], data[5], data[6], DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new NvrException("The NVR returned an invalid recording date.");
        }
    }
}
