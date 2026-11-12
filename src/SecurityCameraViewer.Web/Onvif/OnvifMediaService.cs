using System.Xml.Linq;
using SecurityCameraViewer.Web.Streaming;
using static SecurityCameraViewer.Web.Onvif.OnvifSoap;

namespace SecurityCameraViewer.Web.Onvif;

/// <summary>Immutable Media/Media2 views; changing camera encoders belongs to the upstream device.</summary>
public sealed class OnvifMediaService(StreamingCameraCatalog catalog)
{
    public XElement Handle(XElement operation, string? cameraId)
    {
        var ns = operation.Name.Namespace;
        var isMedia2 = ns == Media2;
        var profiles = catalog.Profiles(cameraId).Where(p => isMedia2 || p.Media.Codec != "H265").ToArray();
        var response = new XElement(ns + (operation.Name.LocalName + "Response"));
        switch (operation.Name.LocalName)
        {
            case "GetServiceCapabilities":
                response.Add(Capabilities(ns, profiles.Length));
                break;
            case "GetProfiles":
                var requested = Value(operation, "Token");
                var types = operation.Elements(ns + "Type").Select(e => e.Value).ToArray();
                if (requested is not null && !profiles.Any(p => p.Token == requested))
                {
                    throw new OnvifFault("ter:InvalidArgVal", "ter:NoProfile", "Unknown media profile.");
                }

                response.Add(profiles.Where(p => requested is null || p.Token == requested).Select(p => Profile(ns, "Profiles", p, types)));
                break;
            case "GetProfile" when !isMedia2:
                response.Add(Profile(ns, "Profile", RequireProfile(operation, cameraId), []));
                break;
            case "GetStreamUri":
                var profile = RequireProfile(operation, cameraId);
                var protocol = isMedia2 ? Value(operation, "Protocol") : operation.Descendants(Schema + "Protocol").FirstOrDefault()?.Value;
                if (protocol is not (null or "RTSP" or "UDP") || operation.Descendants(Schema + "Stream").Any(s => s.Value != "RTP-Unicast"))
                {
                    throw new OnvifFault("ter:InvalidArgVal", "ter:InvalidStreamSetup", "Use RTP-Unicast with RTSP (TCP or UDP transport).");
                }

                response.Add(isMedia2 ? new XElement(ns + "Uri", catalog.StreamUri(profile).AbsoluteUri) : MediaUri(ns, "MediaUri", catalog.StreamUri(profile)));
                break;
            case "GetSnapshotUri":
                var snapshot = catalog.SnapshotUri(RequireProfile(operation, cameraId));
                response.Add(isMedia2 ? new XElement(ns + "Uri", snapshot.AbsoluteUri) : MediaUri(ns, "MediaUri", snapshot));
                break;
            case "GetVideoSources" when !isMedia2:
                response.Add(profiles.GroupBy(p => p.Camera.Id).Select(g => new XElement(ns + "VideoSources", new XAttribute("token", g.First().SourceToken),
                    new XElement(Schema + "Framerate", g.First().Media.FrameRate), Resolution(g.First().Media))));
                break;
            case "GetVideoSourceConfigurations":
            case "GetCompatibleVideoSourceConfigurations" when !isMedia2:
                response.Add(FilterConfigurations(operation, profiles, p => p.SourceConfigurationToken).GroupBy(p => p.Camera.Id)
                    .Select(g => VideoSource(ns + "Configurations", g.First(), profiles)));
                break;
            case "GetVideoSourceConfiguration" when !isMedia2:
                response.Add(VideoSource(ns + "Configuration", RequireConfiguration(operation, profiles, p => p.SourceConfigurationToken), profiles));
                break;
            case "GetVideoEncoderConfigurations":
            case "GetCompatibleVideoEncoderConfigurations" when !isMedia2:
                response.Add(FilterConfigurations(operation, profiles, p => p.EncoderToken).Select(p => VideoEncoder(ns + "Configurations", p, isMedia2)));
                break;
            case "GetVideoEncoderConfiguration" when !isMedia2:
                response.Add(VideoEncoder(ns + "Configuration", RequireConfiguration(operation, profiles, p => p.EncoderToken), false));
                break;
            case "GetVideoSourceConfigurationOptions":
                var source = OptionProfile(operation, profiles);
                response.Add(new XElement(ns + "Options", new XElement(Schema + "BoundsRange",
                    Range("XRange", 0, 0), Range("YRange", 0, 0), Range("WidthRange", source.Media.Width, source.Media.Width),
                    Range("HeightRange", source.Media.Height, source.Media.Height)),
                    new XElement(Schema + "VideoSourceTokensAvailable", source.SourceToken)));
                break;
            case "GetVideoEncoderConfigurationOptions":
                var options = OptionProfiles(operation, profiles).ToArray();
                response.Add((isMedia2 ? options : options.Take(1)).Select(p => EncoderOptions(ns, p, isMedia2)));
                break;
            case "GetGuaranteedNumberOfVideoEncoderInstances" when !isMedia2:
                response.Add(new XElement(ns + "TotalNumber", profiles.Length));
                break;
            case "GetAudioSources" when !isMedia2:
                response.Add(profiles.Where(HasAudio).GroupBy(p => p.Camera.Id).Select(g => new XElement(ns + "AudioSources",
                    new XAttribute("token", $"audio_source_{g.Key}"), new XElement(Schema + "Channels", 1))));
                break;
            case "GetAudioSourceConfigurations":
            case "GetCompatibleAudioSourceConfigurations" when !isMedia2:
                response.Add(FilterConfigurations(operation, profiles.Where(HasAudio).ToArray(), p => $"audio_{p.Camera.Id}")
                    .GroupBy(p => p.Camera.Id).Select(g => AudioSource(ns + "Configurations", g.First())));
                break;
            case "GetAudioEncoderConfigurations":
            case "GetCompatibleAudioEncoderConfigurations" when !isMedia2:
                response.Add(FilterConfigurations(operation, profiles.Where(HasAudio).ToArray(), p => $"audio_encoder_{p.Token}")
                    .Select(p => AudioEncoder(ns + "Configurations", p, isMedia2)));
                break;
            case "GetAudioSourceConfigurationOptions":
                var audioSources = FilterConfigurations(operation, profiles.Where(HasAudio).ToArray(), p => $"audio_{p.Camera.Id}").ToArray();
                if (audioSources.Length == 0)
                {
                    throw new OnvifFault("ter:InvalidArgVal", "ter:NoConfig", "No audio source.");
                }

                response.Add(new XElement(ns + "Options", audioSources.Select(p => $"audio_source_{p.Camera.Id}").Distinct()
                    .Select(token => new XElement(Schema + "InputTokensAvailable", token))));
                break;
            case "GetAudioEncoderConfigurationOptions":
                var audioOptions = FilterConfigurations(operation, profiles.Where(HasAudio).ToArray(), p => $"audio_encoder_{p.Token}")
                    .Select(p => AudioEncoderOptions(isMedia2 ? ns + "Options" : Schema + "Options", p, isMedia2)).ToArray();
                response.Add(isMedia2 ? audioOptions : [new XElement(ns + "Options", audioOptions)]);
                break;
            case "GetMetadataConfigurations":
            case "GetVideoAnalyticsConfigurations" when !isMedia2:
                break;
            case "GetAudioSourceConfiguration" when !isMedia2:
                response.Add(AudioSource(ns + "Configuration", RequireConfiguration(operation, profiles.Where(HasAudio).ToArray(), p => $"audio_{p.Camera.Id}")));
                break;
            case "GetAudioEncoderConfiguration" when !isMedia2:
                response.Add(AudioEncoder(ns + "Configuration", RequireConfiguration(operation, profiles.Where(HasAudio).ToArray(), p => $"audio_encoder_{p.Token}"), false));
                break;
            default:
                throw Unsupported();
        }

        return response;
    }

    public static XElement Capabilities(XNamespace ns, int count)
    {
        return ns == Media2
            ? new XElement(ns + "Capabilities", new XAttribute("SnapshotUri", true), new XAttribute("Rotation", false), new XAttribute("OSD", false),
                new XElement(ns + "ProfileCapabilities", new XAttribute("MaximumNumberOfProfiles", count),
                    new XAttribute("ConfigurationsSupported", "VideoSource VideoEncoder AudioSource AudioEncoder")),
                new XElement(ns + "StreamingCapabilities", new XAttribute("RTSPStreaming", true), new XAttribute("RTPMulticast", false),
                    new XAttribute("RTP_RTSP_TCP", true), new XAttribute("NonAggregateControl", false)))
            : new XElement(ns + "Capabilities", new XAttribute("SnapshotUri", true), new XAttribute("Rotation", false),
                new XAttribute("VideoSourceMode", false), new XAttribute("OSD", false),
                new XElement(ns + "ProfileCapabilities", new XAttribute("MaximumNumberOfProfiles", count)),
                new XElement(ns + "StreamingCapabilities", new XAttribute("RTPMulticast", false), new XAttribute("RTP_TCP", false),
                    new XAttribute("RTP_RTSP_TCP", true), new XAttribute("NonAggregateControl", false)));
    }

    private StreamingProfile RequireProfile(XElement operation, string? cameraId) => catalog.RequireProfile(Value(operation, "ProfileToken"), cameraId);

    private XElement Profile(XNamespace ns, string element, StreamingProfile profile, string[] types)
    {
        var isMedia2 = ns == Media2;
        var result = new XElement(ns + element, new XAttribute("token", profile.Token), new XAttribute("fixed", true),
            new XElement(isMedia2 ? ns + "Name" : Schema + "Name", MediaName($"{profile.Camera.Name} {(profile.Substream ? "Sub" : "Main")}")));
        var configurations = new XElement(ns + "Configurations");
        var configurationNamespace = isMedia2 ? ns : Schema;
        bool Include(string type) => types.Length == 0 || types.Contains("All") || types.Contains(type);
        if (Include("VideoSource"))
        {
            configurations.Add(VideoSource(configurationNamespace + (isMedia2 ? "VideoSource" : "VideoSourceConfiguration"), profile, catalog.Profiles(profile.Camera.Id)));
        }

        if (HasAudio(profile) && Include("AudioSource"))
        {
            configurations.Add(AudioSource(configurationNamespace + (isMedia2 ? "AudioSource" : "AudioSourceConfiguration"), profile));
        }

        if (Include("VideoEncoder"))
        {
            configurations.Add(VideoEncoder(configurationNamespace + (isMedia2 ? "VideoEncoder" : "VideoEncoderConfiguration"), profile, isMedia2));
        }

        if (HasAudio(profile))
        {
            if (Include("AudioEncoder"))
            {
                configurations.Add(AudioEncoder(configurationNamespace + (isMedia2 ? "AudioEncoder" : "AudioEncoderConfiguration"), profile, isMedia2));
            }
        }

        if (isMedia2)
        {
            result.Add(configurations);
        }
        else
        {
            result.Add(configurations.Elements());
        }

        return result;
    }

    private static XElement VideoSource(XName name, StreamingProfile profile, StreamingProfile[] profiles) => new(name,
        new XAttribute("token", profile.SourceConfigurationToken), new XElement(Schema + "Name", MediaName(profile.Camera.Name)),
        new XElement(Schema + "UseCount", profiles.Count(p => p.Camera.Id == profile.Camera.Id)), new XElement(Schema + "SourceToken", profile.SourceToken),
        new XElement(Schema + "Bounds", new XAttribute("x", 0), new XAttribute("y", 0),
            new XAttribute("width", profiles.Where(p => p.Camera.Id == profile.Camera.Id).Max(p => p.Media.Width)),
            new XAttribute("height", profiles.Where(p => p.Camera.Id == profile.Camera.Id).Max(p => p.Media.Height))));

    private static XElement VideoEncoder(XName name, StreamingProfile profile, bool media2)
    {
        var media = profile.Media;
        var result = new XElement(name, new XAttribute("token", profile.EncoderToken));
        if (media2)
        {
            result.Add(new XAttribute("GovLength", Math.Max(1, (int)media.FrameRate)), new XAttribute("Profile", media.VideoProfile));
        }

        result.Add(new XElement(Schema + "Name", MediaName($"{profile.Camera.Name} {(profile.Substream ? "Sub" : "Main")}")),
            new XElement(Schema + "UseCount", 1), new XElement(Schema + "Encoding", media.Codec), Resolution(media),
            new XElement(Schema + "Quality", 5), new XElement(Schema + "RateControl", new XElement(Schema + "FrameRateLimit", media2 ? media.FrameRate : Math.Max(1, (int)media.FrameRate)),
                media2 ? null : new XElement(Schema + "EncodingInterval", 1), new XElement(Schema + "BitrateLimit", media.BitrateKbps)));
        if (!media2)
        {
            if (media.Codec == "H264")
            {
                result.Add(new XElement(Schema + "H264", new XElement(Schema + "GovLength", Math.Max(1, (int)media.FrameRate)), new XElement(Schema + "H264Profile", media.VideoProfile)));
            }

            result.Add(Multicast(), new XElement(Schema + "SessionTimeout", "PT60S"));
        }

        return result;
    }

    private static bool HasAudio(StreamingProfile profile) => profile.Media.AudioCodec is "G711" or "AAC" or "G726";
    private static XElement AudioSource(XName name, StreamingProfile profile) => new(name, new XAttribute("token", $"audio_{profile.Camera.Id}"),
        new XElement(Schema + "Name", MediaName(profile.Camera.Name)), new XElement(Schema + "UseCount", 1), new XElement(Schema + "SourceToken", $"audio_source_{profile.Camera.Id}"));

    private static XElement AudioEncoder(XName name, StreamingProfile profile, bool media2) => new(name, new XAttribute("token", $"audio_encoder_{profile.Token}"),
        new XElement(Schema + "Name", MediaName(profile.Camera.Name)), new XElement(Schema + "UseCount", 1), new XElement(Schema + "Encoding", AudioEncoding(profile, media2)),
        new XElement(Schema + "Bitrate", profile.Media.AudioBitrateKbps), new XElement(Schema + "SampleRate", profile.Media.AudioSampleRate / 1000),
        media2 ? null : Multicast(), media2 ? null : new XElement(Schema + "SessionTimeout", "PT60S"));

    private static string? AudioEncoding(StreamingProfile profile, bool media2) => media2
        ? profile.Media.AudioMedia2Encoding ?? (profile.Media.AudioCodec switch { "G711" => "PCMU", "AAC" => "mpeg4-generic", _ => profile.Media.AudioCodec })
        : profile.Media.AudioCodec;

    private static XElement AudioEncoderOptions(XName name, StreamingProfile profile, bool media2) => new(name,
        new XElement(Schema + "Encoding", AudioEncoding(profile, media2)),
        new XElement(Schema + "BitrateList", new XElement(Schema + "Items", profile.Media.AudioBitrateKbps)),
        new XElement(Schema + "SampleRateList", new XElement(Schema + "Items", profile.Media.AudioSampleRate / 1000)));

    private static XElement Multicast() => new(Schema + "Multicast", new XElement(Schema + "Address", new XElement(Schema + "Type", "IPv4"),
        new XElement(Schema + "IPv4Address", "0.0.0.0")), new XElement(Schema + "Port", 0), new XElement(Schema + "TTL", 1), new XElement(Schema + "AutoStart", false));

    private static XElement MediaUri(XNamespace ns, string name, Uri uri) => new(ns + name, new XElement(Schema + "Uri", uri.AbsoluteUri),
        new XElement(Schema + "InvalidAfterConnect", false), new XElement(Schema + "InvalidAfterReboot", false), new XElement(Schema + "Timeout", "PT0S"));

    private static XElement Resolution(StreamingMediaInfo media) => new(Schema + "Resolution", new XElement(Schema + "Width", media.Width), new XElement(Schema + "Height", media.Height));
    private static XElement Range(string name, double min, double max) => new(Schema + name, new XElement(Schema + "Min", min), new XElement(Schema + "Max", max));

    private static IEnumerable<StreamingProfile> FilterConfigurations(XElement operation, StreamingProfile[] profiles, Func<StreamingProfile, string> token)
    {
        var configuration = Value(operation, "ConfigurationToken");
        var profile = Value(operation, "ProfileToken");
        var result = profiles.Where(p => (configuration is null || token(p) == configuration) && (profile is null || p.Token == profile)).ToArray();
        if (result.Length == 0 && (configuration is not null || profile is not null))
        {
            throw new OnvifFault("ter:InvalidArgVal", "ter:NoConfig", "Unknown media configuration.");
        }

        return result;
    }

    private static StreamingProfile RequireConfiguration(XElement operation, StreamingProfile[] profiles, Func<StreamingProfile, string> token) =>
        profiles.FirstOrDefault(p => token(p) == Value(operation, "ConfigurationToken")) ?? throw new OnvifFault("ter:InvalidArgVal", "ter:NoConfig", "Unknown configuration.");

    private static IEnumerable<StreamingProfile> OptionProfiles(XElement operation, StreamingProfile[] profiles) =>
        FilterConfigurations(operation, profiles, p => p.EncoderToken);

    private static StreamingProfile OptionProfile(XElement operation, StreamingProfile[] profiles) =>
        FilterConfigurations(operation, profiles, p => p.SourceConfigurationToken).FirstOrDefault() ?? throw new OnvifFault("ter:InvalidArgVal", "ter:NoConfig", "No video source.");

    private static XElement EncoderOptions(XNamespace ns, StreamingProfile profile, bool media2)
    {
        var media = profile.Media;
        if (media2)
        {
            return new XElement(ns + "Options", new XAttribute("GovLengthRange", $"1 {Math.Max(1, (int)media.FrameRate)}"), new XAttribute("FrameRatesSupported", media.FrameRate),
                new XAttribute("ProfilesSupported", media.VideoProfile), new XElement(Schema + "Encoding", media.Codec), Range("QualityRange", 5, 5),
                new XElement(Schema + "ResolutionsAvailable", new XElement(Schema + "Width", media.Width), new XElement(Schema + "Height", media.Height)),
                Range("BitrateRange", media.BitrateKbps, media.BitrateKbps));
        }

        return new XElement(ns + "Options", Range("QualityRange", 5, 5), new XElement(Schema + media.Codec,
            new XElement(Schema + "ResolutionsAvailable", new XElement(Schema + "Width", media.Width), new XElement(Schema + "Height", media.Height)),
            media.Codec == "H264" ? Range("GovLengthRange", 1, Math.Max(1, (int)media.FrameRate)) : null,
            Range("FrameRateRange", Math.Max(1, (int)media.FrameRate), Math.Max(1, (int)media.FrameRate)), Range("EncodingIntervalRange", 1, 1),
            media.Codec == "H264" ? new XElement(Schema + "H264ProfilesSupported", media.VideoProfile) : null));
    }
}
