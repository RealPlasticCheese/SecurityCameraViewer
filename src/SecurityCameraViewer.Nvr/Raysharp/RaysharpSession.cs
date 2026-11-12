using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace SecurityCameraViewer.Nvr.Raysharp;

internal sealed record RaysharpMessage(ushort Type, ushort Subtype, ushort Acknowledgement, byte[] Payload);

internal sealed class RaysharpSession : IDisposable
{
    private readonly TcpClient _client = new();
    private uint _sequence;
    public Stream Stream => _client.GetStream();

    public async Task ConnectAsync(NvrConnection connection, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(connection.Host) || connection.Host.Length > 253 || connection.Port is < 1 or > 65535)
        {
            throw new ArgumentException("Enter a recorder host and a port from 1 to 65535.");
        }

        using var deadline = Deadline(token);
        try
        {
            await _client.ConnectAsync(connection.Host, connection.Port, deadline.Token);
            _client.NoDelay = true;
        }
        catch (SocketException)
        {
            throw new NvrException("Could not connect to the NVR. Check its address, proprietary port, and network access.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new NvrException("The NVR connection timed out.");
        }
    }

    public async Task<(uint Key, NvrDeviceInfo Info)> LoginAsync(NvrConnection connection, CancellationToken token)
    {
        var payload = new byte[520];
        WriteCredential(connection.Username, payload.AsSpan(0, 256));
        WriteCredential(connection.Password, payload.AsSpan(256, 256));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(516), 2u << 24);
        try
        {
            await SendAsync(31, 621, payload, token);
        }
        finally
        {
            Array.Clear(payload);
        }

        var response = await ReceiveAsync(31, 621, token);
        RequireSuccess(response);
        var data = response.Payload;
        if (data.Length < 175)
        {
            throw new NvrException("The NVR returned an unsupported login response.");
        }

        var count = data[166];
        if (count == 0)
        {
            throw new NvrException("The NVR did not report any channels.");
        }

        return (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(160)), new(
            ReadText(data.AsSpan(44, 36)), ReadText(data.AsSpan(8, 36)), count, data[141] != 0, data[142] != 0));
    }

    public async Task SendAsync(ushort type, ushort subtype, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        var header = new byte[16];
        header[0] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), type);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), subtype);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), _sequence++);
        YsfCipher.Transform(header.AsSpan(2), false).CopyTo(header, 2);
        var encoded = YsfCipher.Transform(payload.Span, false);
        using var deadline = Deadline(token);
        try
        {
            await Stream.WriteAsync(header, deadline.Token);
            await Stream.WriteAsync(encoded, deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new NvrException("The NVR stopped accepting requests.");
        }
        catch (IOException)
        {
            throw new NvrException("The NVR connection closed. Try again.");
        }
        finally
        {
            Array.Clear(encoded);
        }
    }

    public async Task<RaysharpMessage> ReceiveAsync(ushort expectedType, ushort expectedSubtype, CancellationToken token)
    {
        // Bound unsolicited reports and payload allocation; a recording search needs only one day.
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var prefix = new byte[16];
            await ReadExactlyAsync(prefix, token);
            var version = prefix[0] & 3;
            if (version is not (0 or 2))
            {
                throw new NvrException("The NVR uses an unsupported message version.");
            }

            var header = new byte[16 + (prefix[0] >> 2) * 4];
            prefix.CopyTo(header, 0);
            await ReadExactlyAsync(header.AsMemory(16), token);
            if (version == 2)
            {
                YsfCipher.Transform(header.AsSpan(2), true).CopyTo(header, 2);
            }

            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            if (size > 1024 * 1024)
            {
                throw new NvrException("The NVR message exceeds the supported size.");
            }

            var payload = new byte[size];
            await ReadExactlyAsync(payload, token);
            if (version == 2)
            {
                payload = YsfCipher.Transform(payload, true);
            }

            var message = new RaysharpMessage(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)),
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)), payload);
            if (message.Type == expectedType && message.Subtype == expectedSubtype)
            {
                return message;
            }
        }

        throw new NvrException("The NVR did not return the requested response.");
    }

    public async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken token)
    {
        using var deadline = Deadline(token);
        try
        {
            await Stream.ReadExactlyAsync(buffer, deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new NvrException("The NVR stopped sending data. Try a shorter or earlier recording.");
        }
        catch (IOException)
        {
            throw new NvrException("The NVR connection closed before the transfer completed.");
        }
    }

    public static void RequireSuccess(RaysharpMessage message)
    {
        if (message.Acknowledgement != 0)
        {
            throw new NvrException(message.Acknowledgement switch
            {
                2 => "This NVR firmware does not support the requested operation.",
                3 or 10 => "The NVR account is already in use. Close another recorder session and retry.",
                11 or 12 => "The NVR rejected the username or password.",
                13 or 14 or 18 or 19 => "The NVR denied access to this account or server.",
                _ => $"The NVR rejected the request (code {message.Acknowledgement})."
            });
        }
    }

    public void Dispose() => _client.Dispose();

    private static CancellationTokenSource Deadline(CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        return deadline;
    }

    private static void WriteCredential(string value, Span<byte> destination)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\0') || Encoding.UTF8.GetByteCount(value) >= destination.Length)
        {
            throw new ArgumentException("The NVR username and password must contain 1–255 UTF-8 bytes.");
        }

        Encoding.UTF8.GetBytes(value, destination);
    }

    private static string ReadText(ReadOnlySpan<byte> value)
    {
        var end = value.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? value : value[..end]);
    }
}
