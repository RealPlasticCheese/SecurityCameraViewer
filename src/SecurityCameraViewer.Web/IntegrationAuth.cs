using System.Collections.Concurrent;

namespace SecurityCameraViewer.Web;

public sealed class IntegrationAuth(IntegrationSettings settings)
{
    private const string Cookie = "scv-session";
    private readonly ConcurrentDictionary<string, (string TokenId, DateTimeOffset Expires)> _sessions = new();
    public ApiToken? Identity(HttpContext context)
    {
        if (context.Request.Headers.Authorization.Count > 0)
        {
            var header = context.Request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? settings.Authenticate(header[7..]) : null;
        }
        return context.Request.Cookies.TryGetValue(Cookie, out var session) && _sessions.TryGetValue(session, out var entry) &&
            entry.Expires > DateTimeOffset.UtcNow ? settings.FindToken(entry.TokenId) : null;
    }
    public bool Allowed(HttpContext context, string scope) => !settings.Current.Enabled && !context.Request.Path.StartsWithSegments("/api/v1") ||
        Identity(context) is { } token && (token.Scopes.Contains("admin") || token.Scopes.Contains(scope));
    public void SignIn(HttpContext context, ApiToken token)
    {
        foreach (var expired in _sessions.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow))
        {
            _sessions.TryRemove(expired.Key, out _);
        }

        if (_sessions.Count >= 256)
        {
            throw new InvalidOperationException("Too many browser sessions. Wait for an existing session to expire.");
        }

        var session = IntegrationSettings.NewSecret();
        _sessions[session] = (token.Id, DateTimeOffset.UtcNow.AddHours(12));
        context.Response.Cookies.Append(Cookie, session, new()
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            MaxAge = TimeSpan.FromHours(12),
            Path = "/"
        });
    }
    public void SignOut(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(Cookie, out var session))
        {
            _sessions.TryRemove(session, out _);
        }

        context.Response.Cookies.Delete(Cookie);
    }
    public async Task Authorize(HttpContext context, RequestDelegate next)
    {
        var path = (context.Request.Path.Value ?? "").TrimEnd('/');
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        var scope = path.Equals("/api/recordings/search", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/nvr/search", StringComparison.OrdinalIgnoreCase) ? "read" :
            path.StartsWith("/api/cameras/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/talk", StringComparison.OrdinalIgnoreCase) ? "control" :
            path.Equals("/api/v1/recordings/trigger", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/playback/recordings", StringComparison.OrdinalIgnoreCase) ? "trigger" :
            path.EndsWith("/arming", StringComparison.OrdinalIgnoreCase) && !HttpMethods.IsGet(context.Request.Method) ? "control" :
            path.StartsWith("/api/integrations", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/streaming", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/setup", StringComparison.OrdinalIgnoreCase) || path.Contains("/settings", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/recording", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/recording/detection", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/state", StringComparison.OrdinalIgnoreCase) ? "admin" :
            HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsConnect(context.Request.Method) ? "read" : "admin";
        if (Allowed(context, scope))
        {
            await next(context);
            return;
        }
        context.Response.StatusCode = Identity(context) is null ? 401 : 403;
        await context.Response.WriteAsJsonAsync(new
        {
            message = context.Response.StatusCode == 401 ? "Sign in with an API token." : $"This action requires the {scope} scope."
        });
    }
}
