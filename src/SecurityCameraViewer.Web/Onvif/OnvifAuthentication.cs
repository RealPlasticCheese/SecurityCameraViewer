using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SecurityCameraViewer.Web.Streaming;

namespace SecurityCameraViewer.Web.Onvif;

/// <summary>Read-only client authentication for ONVIF and snapshots, independent of browser/API tokens.</summary>
public sealed partial class OnvifAuthentication(StreamingSettingsStore settings, TimeProvider time)
{
    public const string Realm = "SecurityCameraViewer";
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, long> _replays = new();
    private readonly Lock _gate = new();
    private static readonly XNamespace Security = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private static readonly XNamespace Utility = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";

    public bool Authenticate(HttpContext context, OnvifRequest? soap = null)
    {
        var (configuration, account) = settings.Snapshot();
        if (!configuration.Enabled || !configuration.OnvifEnabled)
        {
            return false;
        }

        var header = context.Request.Headers.Authorization.ToString();
        try
        {
            if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                return Equal(Encoding.UTF8.GetString(Convert.FromBase64String(header[6..])), $"{account.Username}:{account.Password}");
            }

            if (header.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
            {
                return AuthenticateDigest(context, header[7..], account);
            }

            if (header.Length > 0)
            {
                return false;
            }

            return soap is not null && AuthenticateToken(soap, account);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    public void Challenge(HttpContext context)
    {
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var payload = $"{timestamp}:{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";
        var nonce = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{payload}:{Sign(payload)}"));
        context.Response.Headers.Append("WWW-Authenticate", $"Digest realm=\"{Realm}\", nonce=\"{nonce}\", algorithm=MD5, qop=\"auth\", charset=UTF-8");
        context.Response.Headers.Append("WWW-Authenticate", $"Basic realm=\"{Realm}\", charset=\"UTF-8\"");
    }

    private bool AuthenticateDigest(HttpContext context, string header, StreamingAccount account)
    {
        if (header.Length > 4096)
        {
            return false;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in DigestFields().Matches(header))
        {
            if (!fields.TryAdd(match.Groups[1].Value, match.Groups[2].Success ? Regex.Unescape(match.Groups[2].Value) : match.Groups[3].Value))
            {
                return false;
            }
        }

        var uri = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        if (!Equal(fields.GetValueOrDefault("username", ""), account.Username) || fields.GetValueOrDefault("realm") != Realm ||
            fields.GetValueOrDefault("uri") != uri || fields.GetValueOrDefault("algorithm", "MD5") != "MD5" ||
            fields.GetValueOrDefault("qop") is not (null or "auth") || !ValidNonce(fields.GetValueOrDefault("nonce", "")))
        {
            return false;
        }

        var nonce = fields["nonce"];
        var ha1 = Md5($"{account.Username}:{Realm}:{account.Password}");
        var ha2 = Md5($"{context.Request.Method}:{uri}");
        var qop = fields.GetValueOrDefault("qop");
        var nc = fields.GetValueOrDefault("nc", "");
        var cnonce = fields.GetValueOrDefault("cnonce", "");
        var expected = qop is null ? Md5($"{ha1}:{nonce}:{ha2}") : Md5($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}");
        if (!Equal(fields.GetValueOrDefault("response", "").ToLowerInvariant(), expected))
        {
            return false;
        }

        if (qop is null)
        {
            // Legacy RFC 2069 clients cannot supply a nonce count.
            return true;
        }

        if (nc.Length != 8 || !uint.TryParse(nc, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var count) || count == 0 || cnonce.Length is < 1 or > 256)
        {
            return false;
        }

        return Remember($"http/{nonce}/{cnonce}", count);
    }

    private bool AuthenticateToken(OnvifRequest request, StreamingAccount account)
    {
        var tokens = request.Document.Root?.Element(request.Soap + "Header")?.Elements(Security + "Security")
            .SelectMany(s => s.Elements(Security + "UsernameToken")).ToArray() ?? [];
        if (tokens.Length != 1)
        {
            return false;
        }

        var token = tokens[0];
        var password = token.Element(Security + "Password");
        var created = token.Element(Utility + "Created")?.Value;
        if (!Equal(token.Element(Security + "Username")?.Value ?? "", account.Username) || password is null)
        {
            return false;
        }

        if (created is not null && (!DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ||
            timestamp < time.GetUtcNow().AddMinutes(-5) || timestamp > time.GetUtcNow().AddMinutes(1)))
        {
            return false;
        }

        var type = password.Attribute("Type")?.Value;
        if (type is null || type.EndsWith("#PasswordText", StringComparison.Ordinal))
        {
            return Equal(password.Value, account.Password);
        }

        if (!type.EndsWith("#PasswordDigest", StringComparison.Ordinal) || created is null)
        {
            return false;
        }

        var nonceText = token.Element(Security + "Nonce")?.Value ?? "";
        var nonce = Convert.FromBase64String(nonceText);
        if (nonce.Length is < 8 or > 256)
        {
            return false;
        }

        var digest = SHA1.HashData([.. nonce, .. Encoding.UTF8.GetBytes(created), .. Encoding.UTF8.GetBytes(account.Password)]);
        return Equal(password.Value, Convert.ToBase64String(digest)) && Remember($"soap/{nonceText}/{created}", 1);
    }

    private bool ValidNonce(string nonce)
    {
        var parts = Encoding.ASCII.GetString(Convert.FromBase64String(nonce)).Split(':');
        return parts.Length == 3 && long.TryParse(parts[0], out var stamp) &&
            stamp <= time.GetUtcNow().ToUnixTimeSeconds() && stamp >= time.GetUtcNow().AddMinutes(-5).ToUnixTimeSeconds() &&
            Equal(parts[2], Sign($"{parts[0]}:{parts[1]}"));
    }

    private bool Remember(string key, long count)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow().ToUnixTimeSeconds();
            foreach (var item in _replays.Where(p => p.Key.StartsWith("expiry/", StringComparison.Ordinal) && p.Value < now).ToArray())
            {
                _replays.TryRemove(item.Key, out _);
                _replays.TryRemove(item.Key[7..], out _);
            }

            if (_replays.TryGetValue(key, out var previous) && count <= previous || _replays.Count >= 8192)
            {
                return false;
            }

            _replays[key] = count;
            _replays[$"expiry/{key}"] = now + 360;
            return true;
        }
    }

    private string Sign(string value) => Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(value)));
    private static string Md5(string value) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Equal(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    [GeneratedRegex("([a-zA-Z]+)\\s*=\\s*(?:\"((?:\\\\.|[^\"])*)\"|([^,\\s]+))")]
    private static partial Regex DigestFields();
}
