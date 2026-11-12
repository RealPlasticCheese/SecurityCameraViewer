using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

public sealed record TriggerRequest(string CameraId, string Source, string Reason, int DurationSeconds = 20, bool OverrideSchedule = false, string[]? ZoneIds = null);
public sealed record TriggerReceipt(string Id, string Fingerprint, string CameraId, DateTimeOffset CreatedAt, string Status);

/// <summary>Durable idempotency receipts keep retries from creating a second event, including after restart.</summary>
public sealed class RecordingTriggers(IntegrationSettings settings, RecordingService recording)
{
    public static bool IsCameraId(string? id) => id is not null && (Channel(id) > 0 ||
        id.StartsWith("camera-", StringComparison.Ordinal) && Guid.TryParseExact(id[7..], "N", out _));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string DirectoryPath => Path.Combine(settings.DirectoryPath, "requests");
    public static int Channel(string id)
    {
        return id is not null && id.StartsWith("camera-", StringComparison.Ordinal) &&
            int.TryParse(id[7..], out var channel) && channel >= 1 && id == $"camera-{channel}"
            ? channel : 0;
    }
    public async Task<TriggerReceipt> Trigger(TriggerRequest request, string key, string tokenId, CancellationToken token)
    {
        var channel = recording.ChannelForId(request.CameraId);
        if (channel == 0 || string.IsNullOrWhiteSpace(request.Source) || request.Source.Length > 80 ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 256 || request.DurationSeconds is < 1 or > 3600 ||
            string.IsNullOrWhiteSpace(key) || key.Length > 128)
        {
            throw new ArgumentException("Supply a camera ID, source, reason, duration (1–3600 seconds) and Idempotency-Key header (1–128 characters).");
        }

        var zones = recording.Settings.Camera(channel).Zones;
        if (request.ZoneIds is { } ids && (ids.Length > 32 || ids.Any(id => !zones.Any(z => z.Id == id && !z.Exclude))))
        {
            throw new ArgumentException("Unknown detection zone ID.");
        }

        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(recording.Connection.ConfigurationId + ":" + tokenId + ":" + key)));
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        await _gate.WaitAsync(token);
        try
        {
            var path = Path.Combine(DirectoryPath, id + ".json");
            if (Find(id) is { } previous)
            {
                if (previous.Fingerprint != fingerprint)
                {
                    throw new InvalidOperationException("This idempotency key was already used with a different request.");
                }

                return previous;
            }
            var camera = recording.Cameras.FirstOrDefault(c => c.State.Channel == channel);
            if (camera is null || !camera.CanAcceptTrigger || camera.State.Status is "retrying" or "stopped" || !request.OverrideSchedule &&
                (camera.Arming.EffectiveMode == "disarmed" || !camera.ScheduleActive))
            {
                throw new InvalidOperationException("Camera is disabled, unavailable, disarmed or outside its recording schedule. Event mode needs a filled RAM buffer; other modes need active recording.");
            }

            Directory.CreateDirectory(DirectoryPath);
            foreach (var old in Directory.EnumerateFiles(DirectoryPath, "*.json").Where(p => File.GetLastWriteTimeUtc(p) < DateTime.UtcNow.AddDays(-1)))
            {
                File.Delete(old);
            }

            if (Directory.EnumerateFiles(DirectoryPath, "*.json").Take(10000).Count() >= 10000)
            {
                throw new InvalidOperationException("Trigger receipt capacity reached. Retry after older receipts expire.");
            }

            var receipt = new TriggerReceipt(id, fingerprint, request.CameraId, DateTimeOffset.UtcNow, "accepted");
            // Save before admission. A crash in this narrow window leaves an uncertain receipt, never duplicate footage.
            IntegrationSettings.WritePrivate(path, JsonSerializer.Serialize(receipt));
            var trigger = new RecordingTrigger(request.Source, request.Reason, id, [.. zones.Where(z => request.ZoneIds?.Contains(z.Id) == true).Select(z => new EventZone(z.Id, z.Name))]);
            if (!camera.TryTrigger(trigger, request.DurationSeconds, request.OverrideSchedule))
            {
                receipt = receipt with
                {
                    Status = "rejected"
                };
                IntegrationSettings.WritePrivate(path, JsonSerializer.Serialize(receipt));
            }
            return receipt;
        }
        finally
        {
            _gate.Release();
        }
    }
    public TriggerReceipt? Find(string id) => id.Length == 64 && id.All(Uri.IsHexDigit) && File.Exists(Path.Combine(DirectoryPath, id + ".json"))
        ? JsonSerializer.Deserialize<TriggerReceipt>(File.ReadAllText(Path.Combine(DirectoryPath, id + ".json"))) : null;
}
