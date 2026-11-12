using System.Text.Json;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Streaming;
using static SecurityCameraViewer.Streaming.Tests.TestSupport;
using SecurityCameraViewer.Web.Settings;

namespace SecurityCameraViewer.Streaming.Tests;

internal static class StreamingSettingsTests
{
    public static async Task RunAsync()
    {
        var defaults = new StreamingConfiguration();
        Check(!defaults.Enabled && defaults.Validate() is null && defaults.OnvifEnabled && defaults.DiscoveryMode == "individual",
            "Existing installations have no new listener until the service is enabled");
        var fixture = Create();
        var valid = fixture.Store.Current;
        foreach (var invalid in new[]
        {
            valid with { RtspPort = 80 }, valid with { RtspPort = valid.ApiPort }, valid with { RtpPort = 65536 },
            valid with { RtpPort = 8003 }, valid with { RtcpPort = 8003 },
            valid with { OnvifBaseUrl = "ftp://192.0.2.20/" }, valid with { OnvifBaseUrl = "http://user:pass@192.0.2.20/" },
            valid with { OnvifBaseUrl = "http://192.0.2.20/path" }, valid with { OnvifBaseUrl = "http://192.0.2.20/?secret=x" },
            valid with { AdvertisedHost = "192.0.2.20:8554" }, valid with { AdvertisedHost = "" },
            valid with { DiscoveryMode = "unknown" }, valid with { Username = "any" }, valid with { Username = "bad\nname" },
            valid with { Username = "scv-internal" }, valid with { Username = "viewer name" }, valid with { Username = "viewér" },
            valid with { MediaMtxExecutable = "bad\ncommand" }, valid with { Profiles = null! }
        })
        {
            Check(invalid.Validate() is not null, "Invalid service addresses, ports, users and configuration are rejected");
        }

        Check((valid with { AdvertisedHost = "2001:db8::1", OnvifBaseUrl = "http://[2001:db8::1]:5080/" }).Validate() is null,
            "Manual IPv6 RTSP/ONVIF addresses can be configured");
        Check((valid with { OnvifEnabled = false, OnvifBaseUrl = "" }).Validate() is null, "RTSP can be enabled without ONVIF");
        Check((valid with { UdpEnabled = false, RtpPort = 8003 }).Validate() is null,
            "TCP-only streaming does not require a UDP port pair");
        var id = fixture.Catalog.Cameras[0].Id;
        var metadata = new StreamingProfileOverride(id, false, "H265", 2560, 1440, 25, 6000);
        Check(metadata.IsValid() && !(metadata with { Width = 0 }).IsValid() && !(metadata with { FrameRate = double.NaN }).IsValid(),
            "Profile metadata has finite bounded dimensions, frame rates and known codecs");
        Check((valid with { Profiles = [metadata, metadata] }).Validate() is not null, "Duplicate profile overrides are rejected");
        await ThrowsAsync<ArgumentException>(() => fixture.Store.Save(new(valid, "short")), "Enabling requires a client password of at least eight characters");
        await ThrowsAsync<ArgumentException>(() => fixture.Store.Save(new(valid, "bad\npassword")), "Control characters in client credentials are rejected");
        foreach (var password in new[] { "has space", "unicodeé", "sha256:abcd", "question?" })
        {
            await ThrowsAsync<ArgumentException>(() => fixture.Store.Save(new(valid, password)), "Unsupported relay credential characters are rejected before saving");
        }

        Check(StreamingAccount.IsSupportedCredential("Test!$()*+.;<=>[]^_-{}@#&12"), "All supported MediaMTX plaintext credential symbols are accepted");
        fixture.Store.Save(new(valid with { DeviceId = Guid.NewGuid(), Profiles = [metadata] }, null));
        Check(fixture.Store.Current.DeviceId == valid.DeviceId && fixture.Store.Account.Password == "client-secret",
            "Saving preserves device identity and keeps a password when no replacement is supplied");
        var restarted = new StreamingSettingsStore(new JsonAppSettingsStore(fixture.Configuration));
        Check(restarted.Current == fixture.Store.Current || restarted.Current.DeviceId == valid.DeviceId && restarted.Account.Password == "client-secret",
            "Settings and client authentication survive a server restart");
        Check(!JsonSerializer.Serialize(fixture.Store.Current).Contains("client-secret"), "Public configuration serialization excludes client passwords");
        fixture.Store.Save(new(valid with { Enabled = false }, null));
        Check(!fixture.Store.Current.Enabled && fixture.Store.Account.Password.Length > 0, "Disabling preserves credentials for re-enabling");
        await File.WriteAllTextAsync(fixture.Store.FilePath, "{}");
        await ThrowsAsync<InvalidDataException>(() => new StreamingSettingsStore(new JsonAppSettingsStore(fixture.Configuration)),
            "Malformed persisted settings fail closed");

        var legacy = new CameraProvider(new() { ChannelCount = 2, Host = "192.0.2.1", PathTemplate = "/ip{channel:00}/{stream}" }, "legacy-secret");
        var catalog = new StreamingCameraCatalog(legacy, restarted);
        var tokens = catalog.Profiles().Select(p => p.Token).ToArray();
        Check(tokens.Length == 4 && catalog.Profiles().Select(p => p.Token).SequenceEqual(tokens),
            "Recorder templates expose stable main and substream profile tokens");

        var camera = fixture.Catalog.Cameras[0];
        Check(fixture.Catalog.Profiles().Length == 3 && fixture.Catalog.Profiles(camera.Id).Length == 2,
            "Disabled cameras are excluded and substream profiles exist only when configured");
        Check(fixture.Catalog.DeviceId(null) != fixture.Catalog.DeviceId(camera.Id) && fixture.Catalog.DeviceId(camera.Id) == fixture.Catalog.DeviceId(camera.Id),
            "Recorder and virtual cameras have unique stable discovery identities");
        Check(!fixture.Catalog.StreamUri(fixture.Catalog.Profiles()[0]).AbsoluteUri.Contains("upstream"),
            "Advertised RTSP URLs never expose upstream credentials or query tokens");
        Check(fixture.Catalog.Profiles(camera.Id)[0].Media.Codec == "H264", "Removing an override restores underlying stream metadata");

        var probed = StreamingMediaProbe.Parse("""
            {"streams":[{"codec_type":"video","codec_name":"hevc","width":2560,"height":1440,"avg_frame_rate":"30000/1001","bit_rate":"6000000"},
              {"codec_type":"audio","codec_name":"pcm_mulaw","sample_rate":"8000","bit_rate":"64000"}]}
            """);
        Check(probed is { Codec: "H265", Width: 2560, AudioCodec: "G711", AudioSampleRate: 8000 } && Math.Abs(probed.FrameRate - 29.97003) < 0.001,
            "Probe metadata accurately reads H.265, fractional frame rates and G.711 audio");
        Check(StreamingMediaProbe.Parse("{\"streams\":[{\"codec_type\":\"audio\",\"codec_name\":\"aac\"}]}") is null,
            "Audio-only inputs cannot create a video media profile");
        Check(probed!.AudioMedia2Encoding == "PCMU", "Probe preserves the Media2 audio MIME subtype");
    }
}
