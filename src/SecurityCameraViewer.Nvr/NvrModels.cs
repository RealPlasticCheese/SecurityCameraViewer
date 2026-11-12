using System.Text.Json.Serialization;

namespace SecurityCameraViewer.Nvr;

public sealed record NvrConnection(string Host, int Port, string Username, [property: JsonIgnore] string Password)
{
    // Credentials must never appear in diagnostics or generated record ToString output.
    public override string ToString() => $"{Host}:{Port}";
}

public sealed record NvrDeviceInfo(string Name, string Firmware, int ChannelCount, bool CanPlayback, bool CanExport);

public sealed record NvrDiskInfo(int Number, ulong CapacityBytes, ulong FreeBytes);
public sealed record NvrStorageInfo(bool AutomaticOverwrite, IReadOnlyList<NvrDiskInfo> Disks);

public interface INvrStorageClient
{
    Task<NvrStorageInfo> GetStorageInfoAsync(NvrConnection connection, CancellationToken token);
}

/// <summary>Times are the recorder's wall clock, with no server timezone conversion.</summary>
public sealed record NvrRecording(int Channel, DateTime Begin, DateTime End, uint Kind, long SizeBytes);

public sealed record NvrRecordingRequest(int Channel, DateTime Begin, DateTime End)
{
    public void Validate(TimeSpan maximumDuration)
    {
        if (Channel is < 1 or > 256 || Begin.Year is < 2000 or > 2099 || End.Year is < 2000 or > 2099 ||
            Begin.Kind != DateTimeKind.Unspecified || End.Kind != DateTimeKind.Unspecified || End <= Begin || End - Begin > maximumDuration)
        {
            throw new ArgumentException($"Choose a channel and recorder-local times spanning at most {maximumDuration.TotalHours:g} hours, without a timezone suffix.");
        }
    }
}

public enum NvrVideoCodec
{
    H264
}

public sealed record NvrVideoFrame(ReadOnlyMemory<byte> Data, NvrVideoCodec Codec, bool KeyFrame, ulong TimestampMicroseconds, int Width, int Height);

public sealed class NvrException(string message) : InvalidOperationException(message);

/// <summary>Provider-independent recorder access. Playback frames contain elementary video, without vendor headers.</summary>
public interface INvrClient
{
    Task<NvrDeviceInfo> GetDeviceInfoAsync(NvrConnection connection, CancellationToken token);
    Task<IReadOnlyList<NvrRecording>> SearchAsync(NvrConnection connection, NvrRecordingRequest request, CancellationToken token);
    IAsyncEnumerable<NvrVideoFrame> ReadRecordingAsync(NvrConnection connection, NvrRecordingRequest request, CancellationToken token);
}
