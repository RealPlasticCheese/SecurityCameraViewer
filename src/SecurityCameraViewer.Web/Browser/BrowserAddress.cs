namespace SecurityCameraViewer.Web.Browser;

internal static class BrowserAddress
{
    public static Uri Parse(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.Port == 0)
        {
            throw new ArgumentException("The browser address must be an absolute HTTP or HTTPS URL with a host, a nonzero port, and no embedded credentials.");
        }
        return uri;
    }

    public static Uri ForServer(IEnumerable<string> addresses)
    {
        foreach (var address in addresses)
        {
            // Kestrel wildcard bindings are listen addresses, not browser destinations.
            var normalized = address.Replace("://*:", "://localhost:", StringComparison.Ordinal)
                .Replace("://+:", "://localhost:", StringComparison.Ordinal);
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Port == 0)
            {
                continue;
            }
            var target = new UriBuilder(uri);
            if (uri.Host is "0.0.0.0" or "[::]" or "::")
            {
                target.Host = "localhost";
            }
            return Parse(target.Uri.AbsoluteUri);
        }
        throw new ArgumentException("No HTTP or HTTPS listening address is available to open in the browser.");
    }
}
