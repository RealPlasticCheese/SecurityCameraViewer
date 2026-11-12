namespace SecurityCameraViewer.Web.Browser;

internal sealed record BrowserLaunchOptions
{
    public bool Enabled { get; init; }
    public string Browser { get; init; } = "auto";
    public string? BrowserPath { get; init; }
    public string? Url { get; init; }
    public string? ConnectUrl { get; init; }

    public static BrowserLaunchOptions Read(IConfiguration configuration)
    {
        var options = new BrowserLaunchOptions
        {
            Enabled = configuration.GetValue<bool>("open-browser"),
            Browser = (configuration["browser"] ?? "auto").Trim().ToLowerInvariant(),
            BrowserPath = configuration["browser-path"],
            Url = configuration["browser-url"],
            ConnectUrl = configuration["connect"]
        };
        if (options.Enabled || options.ConnectUrl is not null)
        {
            if (options.Browser is not ("auto" or "edge" or "chrome" or "chromium" or "brave" or "default"))
            {
                throw new ArgumentException("--browser must be auto, edge, chrome, chromium, brave, or default.");
            }
            if (options.BrowserPath is not null && (string.IsNullOrWhiteSpace(options.BrowserPath) || options.Browser == "default"))
            {
                throw new ArgumentException("--browser-path must name a Chromium browser executable and cannot be combined with --browser default.");
            }
            if (options.Url is not null)
            {
                BrowserAddress.Parse(options.Url);
            }
            if (options.ConnectUrl is not null)
            {
                BrowserAddress.Parse(options.ConnectUrl);
            }
        }
        return options;
    }

    public static string[] NormalizeArguments(string[] arguments)
    {
        var normalized = new List<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            normalized.Add(argument);
            if ((argument is "--demo" or "--open-browser") &&
                (index + 1 == arguments.Length || !bool.TryParse(arguments[index + 1], out _)))
            {
                normalized.Add("true");
            }
        }
        return [.. normalized];
    }
}
