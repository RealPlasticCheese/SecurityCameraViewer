using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Web.Streaming;

public interface IStreamingMediaProbe
{
    Task<StreamingMediaInfo?> ProbeAsync(StreamingProfile profile, CancellationToken token);
}

public sealed class StreamingMediaProbe(StreamingSettingsStore settings, MediaRelayService relay, IConfiguration configuration) : IStreamingMediaProbe
{
    public async Task<StreamingMediaInfo?> ProbeAsync(StreamingProfile profile, CancellationToken token)
    {
        var ffmpeg = configuration["ffmpeg"] ?? "ffmpeg";
        var ffprobe = configuration["ffprobe"] ?? (Path.GetFileNameWithoutExtension(ffmpeg).Equals("ffmpeg", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe" + Path.GetExtension(ffmpeg)) : "ffprobe");
        var info = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in new[] { "-v", "error", "-f", "concat", "-safe", "0", "-protocol_whitelist", "pipe,tcp,udp,rtp",
            "-i", "pipe:0", "-show_entries", "stream=codec_type,codec_name,profile,width,height,avg_frame_rate,bit_rate,sample_rate", "-of", "json" })
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove("FFREPORT");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return null;
        }

        using var registration = deadline.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }
        });
        var input = new ViewerSettings
        {
            Sources = [new CameraSource
            {
                Channel = 1, MainStream = $"rtsp://127.0.0.1:{settings.Current.RtspPort}/{profile.Path}",
                Username = relay.InternalAccount.Username, UseDefaultPassword = true
            }]
        };
        var errors = DrainAsync(process.StandardError);
        var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        try
        {
            await process.StandardInput.WriteAsync(FfmpegRtspInput.Create(input, relay.InternalAccount.Password, 1, true).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            var json = await output;
            await process.WaitForExitAsync(deadline.Token);
            return process.ExitCode == 0 ? Parse(json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException || ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            await process.WaitForExitAsync(CancellationToken.None);
            await errors;
            try
            {
                await output;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
            }
        }
    }

    public static StreamingMediaInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("streams", out var streamList) || streamList.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var streams = streamList.EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => Text(s, "codec_type") == "video");
        var codec = Text(video, "codec_name") switch { "h264" => "H264", "hevc" => "H265", "mjpeg" => "JPEG", _ => null };
        if (codec is null || Number(video, "width") <= 0 || Number(video, "height") <= 0)
        {
            return null;
        }

        var rate = Text(video, "avg_frame_rate")?.Split('/') ?? [];
        var fps = rate.Length == 2 && double.TryParse(rate[0], System.Globalization.CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(rate[1], System.Globalization.CultureInfo.InvariantCulture, out var denominator) && denominator > 0
            ? numerator / denominator : 15;
        var audio = streams.FirstOrDefault(s => Text(s, "codec_type") == "audio");
        var audioCodec = Text(audio, "codec_name") switch { "pcm_mulaw" or "pcm_alaw" => "G711", "aac" or "aac_latm" => "AAC", "adpcm_g726" => "G726", _ => null };
        var audioMedia2Encoding = Text(audio, "codec_name") switch
        {
            "pcm_mulaw" => "PCMU", "pcm_alaw" => "PCMA", "aac" => "mpeg4-generic", "aac_latm" => "MP4A-LATM", "adpcm_g726" => "G726", _ => null
        };
        var videoProfile = Text(video, "profile") switch { "Baseline" or "Constrained Baseline" => "Baseline", "High" => "High", _ => "Main" };
        return new(codec, Number(video, "width"), Number(video, "height"), fps is > 0 and <= 240 ? fps : 15,
            Math.Max(1, Number(video, "bit_rate", 4096000) / 1000), audioCodec, Number(audio, "sample_rate", 8000), Math.Max(1, Number(audio, "bit_rate", 64000) / 1000),
            "ffprobe", videoProfile, audioMedia2Encoding);
    }

    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field.ToString() : null;
    private static int Number(JsonElement value, string name, int fallback = 0) => int.TryParse(Text(value, name), out var result) ? result : fallback;

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (text.Length + count > 65536)
            {
                throw new IOException("Media metadata exceeds the size limit.");
            }

            text.Append(buffer, 0, count);
        }

        return text.ToString();
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer) > 0)
        {
        }
    }
}

public sealed class StreamingMetadataService(StreamingCameraCatalog catalog, MediaRelayService relay,
    IStreamingMediaProbe probe, ILogger<StreamingMetadataService>? logger = null) : BackgroundService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _checked = new();
    public void Refresh() => _checked.Clear();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (relay.Status.Running)
                {
                    var connection = catalog.Connection;
                    foreach (var profile in catalog.Profiles(connection).Where(p => !p.Camera.MotionActivated))
                    {
                        var key = $"{connection.Settings.ConfigurationId}/{profile.Token}/{(profile.Substream ? profile.Camera.SubStream : profile.Camera.MainStream)}";
                        if (_checked.TryGetValue(key, out var last) && last > DateTimeOffset.UtcNow.AddMinutes(-30))
                        {
                            continue;
                        }

                        StreamingMediaInfo? result = null;
                        try
                        {
                            result = await probe.ProbeAsync(profile, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // A codec/metadata failure belongs to this camera, not the host.
                            logger?.LogWarning("Camera metadata probe failed ({ErrorType}); retrying later.", ex.GetType().Name);
                        }
                        _checked[key] = DateTimeOffset.UtcNow.AddMinutes(result is null ? -29 : 0);
                        if (result is not null)
                        {
                            catalog.SetMediaInfo(profile, result, connection);
                        }
                    }
                }

                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
