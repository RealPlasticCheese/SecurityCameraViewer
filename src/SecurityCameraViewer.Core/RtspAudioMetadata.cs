namespace SecurityCameraViewer.Core;

/// <summary>Audio advertised for receiving camera media, excluding talkback-only tracks.</summary>
public sealed record RtspAudioMetadata(bool HasAudio, string? Codec)
{
    public static RtspAudioMetadata Read(string sdp, bool includeReceiveOnly = false)
    {
        var sections = new List<List<string>>();
        var session = new List<string>();
        var current = session;
        using var reader = new StringReader(sdp);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.StartsWith("m=", StringComparison.OrdinalIgnoreCase))
            {
                current = [];
                sections.Add(current);
            }
            current.Add(line);
        }

        static string? Direction(IEnumerable<string> lines) => lines.LastOrDefault(line =>
            line is "a=sendonly" or "a=recvonly" or "a=sendrecv" or "a=inactive");

        foreach (var section in sections)
        {
            var media = section[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!media[0].Equals("m=audio", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var direction = Direction(section) ?? Direction(session);
            if (direction == "a=inactive" || (direction == "a=recvonly" && !includeReceiveOnly))
            {
                continue;
            }
            if (media.Length < 4)
            {
                return new(true, null);
            }
            foreach (var payload in media.Skip(3))
            {
                var prefix = $"a=rtpmap:{payload}";
                var mapping = section.Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .FirstOrDefault(parts => parts.Length >= 2 && parts[0].Equals(prefix, StringComparison.OrdinalIgnoreCase));
                var codec = mapping?[1].Split('/')[0].ToUpperInvariant() ?? payload switch
                {
                    "0" => "PCMU",
                    "3" => "GSM",
                    "8" => "PCMA",
                    "9" => "G722",
                    "10" or "11" => "PCM",
                    "14" => "MPA",
                    "18" => "G729",
                    _ => null
                };
                if (codec is "MPEG4-GENERIC" or "MP4A-LATM")
                {
                    codec = "AAC";
                }
                if (!string.IsNullOrEmpty(codec) && codec.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                {
                    return new(true, codec);
                }
            }
            // A missing or unfamiliar RTP mapping does not mean that the audio track is absent.
            return new(true, null);
        }
        return new(false, null);
    }
}
