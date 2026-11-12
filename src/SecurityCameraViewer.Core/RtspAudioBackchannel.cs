using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SecurityCameraViewer.Core;

public sealed record TalkbackCapability(bool Supported, string Status, string Message, string? Codec = null);

public interface ITalkbackSession : IAsyncDisposable
{
    string Codec
    {
        get;
    }
    Task SendAsync(ReadOnlyMemory<byte> pcm, CancellationToken token);
    Task KeepAliveAsync(CancellationToken token);
}

public interface ITalkbackSource
{
    Task<TalkbackCapability> ProbeAsync(ViewerSettings settings, string password, int channel, CancellationToken token);
    Task<ITalkbackSession> OpenAsync(ViewerSettings settings, string password, int channel, CancellationToken token);
}

/// <summary>Negotiates explicit ONVIF speaker tracks; never treats a microphone or HTTP-style success as talkback.</summary>
public sealed partial class RtspAudioBackchannel : ITalkbackSource
{
    public async Task<TalkbackCapability> ProbeAsync(ViewerSettings settings, string password, int channel, CancellationToken token)
    {
        try
        {
            await using var session = await OpenCoreAsync(settings, password, channel, play: false, token);
            return new(true, "supported", "Hold to talk to this camera.", session.Codec);
        }
        catch (BackchannelException ex)
        {
            return new(false, ex.Status, ex.Message);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(false, "unavailable", "Camera audio backchannel check timed out.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or ArgumentException or FormatException)
        {
            return new(false, "unavailable", "Could not verify this camera's audio backchannel.");
        }
    }

    public async Task<ITalkbackSession> OpenAsync(ViewerSettings settings, string password, int channel, CancellationToken token)
        => await OpenCoreAsync(settings, password, channel, play: true, token);

    private static async Task<Session> OpenCoreAsync(ViewerSettings settings, string password, int channel, bool play, CancellationToken token)
    {
        settings = settings with
        {
            Substream = false
        };
        var address = settings.StreamUri(channel);
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(token);
        opening.CancelAfter(TimeSpan.FromSeconds(7));
        var connection = await RtspControlConnection.ConnectAsync(address, settings.CameraUsername(channel),
            settings.CameraPassword(channel, password), opening.Token);
        var session = new Session(connection, address);
        try
        {
            var description = await connection.RequestAsync("DESCRIBE", address, null, null, opening.Token);
            EnsureSuccess(description);
            var selection = SelectTrack(address, description.Headers.GetValueOrDefault("Content-Base"), description.Body);
            session.SetTrack(selection);
            var setup = await connection.RequestAsync("SETUP", selection.Control, null,
                "RTP/AVP/TCP;unicast;interleaved=0-1", opening.Token);
            EnsureSuccess(setup);
            session.SetSession(setup);
            if (play)
            {
                EnsureSuccess(await connection.RequestAsync("PLAY", selection.Aggregate, session.Id, null, opening.Token));
            }
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static void EnsureSuccess(RtspResponse response)
    {
        if (response.Status != 200)
        {
            throw response.Status switch
            {
                404 => new BackchannelException("waiting", "Camera stream is unavailable; try again when it is active."),
                401 or 403 => new BackchannelException("denied", "Camera rejected audio backchannel access."),
                551 or 461 => new BackchannelException("unsupported", "Camera does not support this RTSP audio backchannel transport."),
                _ => new BackchannelException("unavailable", "Camera could not start an audio backchannel.")
            };
        }
    }

    private sealed record Track(Uri Control, Uri Aggregate, string Codec, byte Payload);

    private static Track SelectTrack(Uri address, string? contentBase, string sdp)
    {
        var baseAddress = contentBase is not null ? new Uri(address, contentBase) : new UriBuilder(address)
        {
            Path = address.AbsolutePath.TrimEnd('/') + "/"
        }.Uri;
        RtspControlConnection.EnsureAuthority(address, baseAddress);
        var blocks = sdp.Replace("\r", "").Split("\nm=", StringSplitOptions.None);
        var sessionLines = blocks[0].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).ToArray();
        var aggregateControl = sessionLines.FirstOrDefault(line => line.StartsWith("a=control:", StringComparison.Ordinal))?[10..];
        var aggregate = aggregateControl is null or "*" ? contentBase is null ? address : baseAddress : new Uri(baseAddress, aggregateControl);
        RtspControlConnection.EnsureAuthority(address, aggregate);
        var sessionSendOnly = sessionLines.Contains("a=sendonly");
        foreach (var block in blocks.Skip(1))
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).ToArray();
            var media = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (media.Length < 4 || media[0] != "audio" || media[2] is not ("RTP/AVP" or "RTP/AVP/TCP"))
            {
                continue;
            }
            var direction = lines.FirstOrDefault(line => line is "a=sendonly" or "a=recvonly" or "a=sendrecv" or "a=inactive");
            if (direction != "a=sendonly" && !(direction is null && sessionSendOnly))
            {
                continue;
            }
            var control = lines.FirstOrDefault(line => line.StartsWith("a=control:", StringComparison.Ordinal))?[10..];
            if (string.IsNullOrWhiteSpace(control) || control == "*")
            {
                continue;
            }
            var target = new Uri(baseAddress, control);
            RtspControlConnection.EnsureAuthority(address, target);
            foreach (var payloadText in media.Skip(3))
            {
                if (!byte.TryParse(payloadText, out var payload) || payload > 127)
                {
                    continue;
                }
                var mapping = lines.FirstOrDefault(line => line.StartsWith($"a=rtpmap:{payload} ", StringComparison.OrdinalIgnoreCase));
                var codec = mapping is null ? payload == 0 ? "PCMU" : payload == 8 ? "PCMA" : null : CodecPattern().Match(mapping).Groups[1].Value.ToUpperInvariant();
                if (codec is not ("PCMU" or "PCMA") || payload < 96 && !(payload == 0 && codec == "PCMU" || payload == 8 && codec == "PCMA"))
                {
                    continue;
                }
                return new(target, aggregate, codec, payload);
            }
        }
        throw new BackchannelException("unsupported", "No supported G.711 audio speaker backchannel was advertised.");
    }

    [GeneratedRegex(@"^a=rtpmap:\d+\s+(PCMU|PCMA)/8000(?:/1)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CodecPattern();

    private sealed class BackchannelException(string status, string message) : IOException(message)
    {
        public string Status { get; } = status;
    }

    private sealed partial class Session(RtspControlConnection connection, Uri address) : ITalkbackSession
    {
        private readonly SemaphoreSlim _audio = new(1, 1);
        private readonly byte[] _identity = RandomNumberGenerator.GetBytes(10);
        private Track? _track;
        private byte _interleaved;
        private ushort _sequence;
        private uint _timestamp;
        private long _started;
        private int _packets;
        public string? Id
        {
            get; private set;
        }
        public string Codec => _track!.Codec;

        public void SetTrack(Track track)
        {
            _track = track;
            _sequence = BinaryPrimitives.ReadUInt16BigEndian(_identity);
            _timestamp = BinaryPrimitives.ReadUInt32BigEndian(_identity.AsSpan(2));
        }

        public void SetSession(RtspResponse setup)
        {
            var id = setup.Headers.GetValueOrDefault("Session", "").Split(';')[0].Trim();
            if (id.Length is < 1 or > 128 || id.Any(character => character <= ' ' || character >= 127))
            {
                throw new InvalidDataException("Invalid RTSP backchannel session.");
            }
            Id = id;
            var transport = setup.Headers.GetValueOrDefault("Transport", "");
            var match = InterleavedPattern().Match(transport);
            if (!transport.StartsWith("RTP/AVP/TCP;", StringComparison.OrdinalIgnoreCase) || !match.Success ||
                !byte.TryParse(match.Groups[1].Value, out _interleaved) ||
                !byte.TryParse(match.Groups[2].Value, out var rtcp) || rtcp != _interleaved + 1 ||
                !transport.Split(';').Any(value => value.Trim().Equals("unicast", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Unsupported backchannel transport negotiation.");
            }
        }

        public async Task SendAsync(ReadOnlyMemory<byte> pcm, CancellationToken token)
        {
            if (pcm.Length != 320)
            {
                throw new InvalidDataException("Talkback requires one 20 ms PCM frame.");
            }
            await _audio.WaitAsync(token);
            try
            {
                if (_packets == 0)
                {
                    _started = Stopwatch.GetTimestamp();
                }
                var delay = _packets * .02 - Stopwatch.GetElapsedTime(_started).TotalSeconds;
                if (delay < -.3)
                {
                    throw new IOException("Talkback audio fell behind.");
                }
                if (delay > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), token);
                }
                // Only one packet is retained. A stalled write cancels instead of queuing old speech.
                var packet = new byte[176];
                packet[0] = (byte)'$';
                packet[1] = _interleaved;
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 172);
                packet[4] = 0x80;
                packet[5] = (byte)(_track!.Payload | (_packets == 0 ? 0x80 : 0));
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), _sequence++);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), _timestamp);
                _identity.AsSpan(6, 4).CopyTo(packet.AsSpan(12));
                for (var index = 0; index < 160; index++)
                {
                    var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.Span.Slice(index * 2, 2));
                    packet[index + 16] = G711.Encode(sample, Codec == "PCMA");
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
                await connection.WriteAsync(packet, deadline.Token);
                _timestamp += 160;
                _packets++;
            }
            finally
            {
                _audio.Release();
            }
        }

        public async Task KeepAliveAsync(CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            if (_packets > 0)
            {
                await _audio.WaitAsync(deadline.Token);
                try
                {
                    var report = new byte[32];
                    report[0] = (byte)'$';
                    report[1] = (byte)(_interleaved + 1);
                    BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(2), 28);
                    report[4] = 0x80;
                    report[5] = 200;
                    BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(6), 6);
                    _identity.AsSpan(6, 4).CopyTo(report.AsSpan(8));
                    var utcTicks = DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
                    BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(12), (uint)(utcTicks / TimeSpan.TicksPerSecond + 2208988800));
                    BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(16), (uint)((ulong)(utcTicks % TimeSpan.TicksPerSecond) * (1UL << 32) / TimeSpan.TicksPerSecond));
                    BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(20), _timestamp);
                    BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(24), (uint)_packets);
                    BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(28), (uint)_packets * 160);
                    await connection.WriteAsync(report, deadline.Token);
                }
                finally
                {
                    _audio.Release();
                }
            }
            var response = await connection.RequestAsync("GET_PARAMETER", _track!.Aggregate, Id, null, deadline.Token);
            if (response.Status is 405 or 501)
            {
                response = await connection.RequestAsync("OPTIONS", _track.Aggregate, Id, null, deadline.Token);
            }
            EnsureSuccess(response);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Id is not null)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await connection.RequestAsync("TEARDOWN", _track?.Aggregate ?? address, Id, null, deadline.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or OperationCanceledException or ArgumentException or FormatException)
            {
            }
            finally
            {
                connection.Dispose();
            }
        }

        [GeneratedRegex(@"(?:^|;)\s*interleaved=(\d+)-(\d+)(?:;|$)", RegexOptions.IgnoreCase)]
        private static partial Regex InterleavedPattern();
    }
}
