using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using SecurityCameraViewer.Web.Onvif;
using static SecurityCameraViewer.Streaming.Tests.TestSupport;
using static SecurityCameraViewer.Web.Onvif.OnvifSoap;

namespace SecurityCameraViewer.Streaming.Tests;

internal static class OnvifAuthenticationTests
{
    public static Task RunAsync()
    {
        var fixture = Create();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var auth = new OnvifAuthentication(fixture.Store, clock);
        var basic = Context();
        basic.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("viewer:client-secret"));
        Check(auth.Authenticate(basic), "HTTP Basic accepts the configured client login");
        basic.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("viewer:wrong"));
        Check(!auth.Authenticate(basic), "HTTP Basic rejects wrong credentials");
        basic.Request.Headers.Authorization = "Basic invalid~base64";
        Check(!auth.Authenticate(basic), "Malformed Basic authentication does not throw");
        basic.Request.Headers.Authorization = "Bearer client-secret";
        Check(!auth.Authenticate(basic), "ONVIF credentials are distinct from browser/API bearer tokens");

        var challenge = Context();
        auth.Challenge(challenge);
        var header = challenge.Response.Headers.WWWAuthenticate.First()!;
        var nonce = header.Split("nonce=\"")[1].Split('"')[0];
        var digest = Context();
        digest.Request.Headers.Authorization = Digest(nonce, digest.Request.Path, "POST", 1);
        Check(auth.Authenticate(digest), "HTTP Digest MD5 with qop=auth verifies a real challenge response");
        Check(!auth.Authenticate(digest), "Replayed HTTP Digest nonce counts are rejected");
        digest.Request.Headers.Authorization = Digest(nonce, digest.Request.Path, "POST", 2);
        Check(auth.Authenticate(digest), "Increasing Digest nonce counts remain valid for a client session");
        digest.Request.Path = "/onvif/media_service";
        Check(!auth.Authenticate(digest), "Digest authentication is bound to the actual request URI");
        digest.Request.Path = "/onvif/device_service";
        digest.Request.Headers.Authorization = Digest(nonce, digest.Request.Path, "GET", 3);
        Check(!auth.Authenticate(digest), "Digest authentication is bound to the actual HTTP method");
        digest.Request.Headers.Authorization = Digest(nonce, digest.Request.Path, "POST", 3, "other-password");
        Check(!auth.Authenticate(digest), "A bad Digest hash never consumes the valid nonce count");
        digest.Request.Headers.Authorization = Digest(nonce, digest.Request.Path, "POST", 3);
        Check(auth.Authenticate(digest), "A valid request succeeds after a bad hash with the same count");
        digest.Request.Headers.Authorization = Digest(nonce, digest.Request.Path, "POST", 4) + ", username=\"viewer\"";
        Check(!auth.Authenticate(digest), "Duplicate Digest fields are rejected");
        digest.Request.Headers.Authorization = Digest(nonce, digest.Request.Path, "POST", 4).Replace("qop=auth", "qop=auth-int");
        Check(!auth.Authenticate(digest), "Unsupported Digest qop is rejected");
        digest.Request.Headers.Authorization = Digest(nonce, digest.Request.Path, "POST", 0);
        Check(!auth.Authenticate(digest), "Zero Digest nonce counts cannot authenticate");
        digest.Request.Headers.Authorization = LegacyDigest(nonce, digest.Request.Path);
        Check(auth.Authenticate(digest), "Legacy Digest clients without qop are supported");
        clock.Now = clock.Now.AddMinutes(6);
        Check(!auth.Authenticate(digest), "Expired HTTP Digest challenges are rejected");
        clock.Now = DateTimeOffset.UtcNow;

        var message = Token(clock.Now, "client-secret");
        Check(auth.Authenticate(Context(), message), "ONVIF WS-Security PasswordDigest validates nonce + created + password");
        Check(!auth.Authenticate(Context(), message), "Replayed WS-Security PasswordDigest tokens are rejected");
        Check(!auth.Authenticate(Context(), Token(clock.Now, "wrong")), "WS-Security rejects a wrong digest");
        Check(!auth.Authenticate(Context(), Token(clock.Now.AddMinutes(-6), "client-secret")), "Stale WS-Security timestamps are rejected");
        Check(!auth.Authenticate(Context(), Token(clock.Now.AddMinutes(2), "client-secret")), "WS-Security timestamps too far in the future are rejected");
        Check(auth.Authenticate(Context(), Token(clock.Now, "client-secret", true)), "WS-Security PasswordText clients are supported");
        fixture.Store.Save(new(fixture.Store.Current, "rotated-secret"));
        Check(!auth.Authenticate(Context(), Token(clock.Now, "client-secret", true)), "Credential rotation immediately invalidates old ONVIF logins");
        fixture.Store.Save(new(fixture.Store.Current with { Enabled = false }, null));
        var disabled = Context();
        disabled.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("viewer:rotated-secret"));
        Check(!auth.Authenticate(disabled), "A disabled service cannot authenticate clients");
        return Task.CompletedTask;
    }

    internal static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/onvif/device_service";
        return context;
    }

    internal static string Digest(string nonce, string uri, string method, uint count, string password = "client-secret")
    {
        var nc = count.ToString("x8");
        var hash = Md5($"{Md5($"viewer:{OnvifAuthentication.Realm}:{password}")}:{nonce}:{nc}:test-client:auth:{Md5($"{method}:{uri}")}");
        return $"Digest username=\"viewer\", realm=\"{OnvifAuthentication.Realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{hash}\", qop=auth, nc={nc}, cnonce=\"test-client\", algorithm=MD5";
    }

    private static string LegacyDigest(string nonce, string uri)
    {
        var hash = Md5($"{Md5($"viewer:{OnvifAuthentication.Realm}:client-secret")}:{nonce}:{Md5($"POST:{uri}")}");
        return $"Digest username=\"viewer\", realm=\"{OnvifAuthentication.Realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{hash}\"";
    }

    internal static OnvifRequest Token(DateTimeOffset now, string secret, bool text = false)
    {
        XNamespace security = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        XNamespace utility = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
        var nonce = RandomNumberGenerator.GetBytes(16);
        var created = now.ToString("O");
        var digest = Convert.ToBase64String(SHA1.HashData([.. nonce, .. Encoding.UTF8.GetBytes(created), .. Encoding.UTF8.GetBytes(secret)]));
        var operation = new XElement(Media + "GetProfiles");
        var document = new XDocument(new XElement(Soap12 + "Envelope", new XElement(Soap12 + "Header",
            new XElement(security + "Security", new XElement(security + "UsernameToken", new XElement(security + "Username", "viewer"),
                new XElement(security + "Password", new XAttribute("Type", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#" + (text ? "PasswordText" : "PasswordDigest")), text ? secret : digest),
                new XElement(security + "Nonce", Convert.ToBase64String(nonce)), new XElement(utility + "Created", created)))),
            new XElement(Soap12 + "Body", operation)));
        return new(document, operation, Soap12, Addressing, null);
    }

    private static string Md5(string value) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(value)));
}
