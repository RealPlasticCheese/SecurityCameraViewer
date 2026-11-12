using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SecurityCameraViewer.Web.Browser;

internal static class BrowserLaunchTests
{
    public static async Task RunAsync()
    {
        Check(!Options().Enabled && Options().Browser == "auto", "Normal startup leaves browsers closed");
        var normalized = BrowserLaunchOptions.NormalizeArguments(["--open-browser", "--demo", "--urls", "http://localhost:5080"]);
        Check(normalized.SequenceEqual(["--open-browser", "true", "--demo", "true", "--urls", "http://localhost:5080"]),
            "Bare browser and demo switches preserve subsequent options");
        Check(BrowserLaunchOptions.NormalizeArguments(["--open-browser", "false", "--demo", "true"])
            .SequenceEqual(["--open-browser", "false", "--demo", "true"]), "Explicit boolean values remain intact");
        Check(BrowserLaunchOptions.NormalizeArguments(["--open-browser=false", "--open-browser"])
            .SequenceEqual(["--open-browser=false", "--open-browser", "true"]), "Equals syntax and final bare switches are supported");
        Check(Options(("open-browser", "true"), ("browser", " CHROME ")).Browser == "chrome", "Browser names ignore case and surrounding spaces");
        Check(Options(("connect", "https://viewer.example/")).ConnectUrl == "https://viewer.example/", "Connect enables browser validation without starting a host");
        Reject(() => Options(("open-browser", "true"), ("browser", "firefox")), "Unsupported browser selection is rejected");
        Reject(() => Options(("connect", "https://viewer.example/"), ("browser", "invalid")), "Connect validates browser selection");
        Reject(() => Options(("open-browser", "true"), ("browser", "default"), ("browser-path", "custom")), "Default-browser mode rejects a conflicting executable");
        Reject(() => Options(("open-browser", "true"), ("browser-path", " ")), "Empty custom executable is rejected");
        Reject(() => Options(("connect", "http://localhost:0/")), "Connect rejects an unusable port");
        Reject(() => Options(("open-browser", "true"), ("browser-url", "relative")), "Startup URL overrides are validated");
        foreach (var address in new[] { "", "relative", "file:///tmp/viewer", "javascript:alert(1)", "ftp://server/", "https://user:secret@server/", "http://server:0/" })
        {
            Reject(() => BrowserAddress.Parse(address), "Invalid or credential-bearing browser address is rejected: " + address.Split('@').Last());
        }
        foreach (var address in new[] { "http://0.0.0.0:5080", "http://*:5080", "http://+:5080", "http://[::]:5080" })
        {
            Check(BrowserAddress.ForServer([address]).AbsoluteUri == "http://localhost:5080/", "Wildcard binding opens localhost: " + address);
        }
        Check(BrowserAddress.ForServer(["https://[::1]:7443/"]).AbsoluteUri == "https://[::1]:7443/", "IPv6 loopback and HTTPS ports are preserved");
        Check(BrowserAddress.ForServer(["http://127.0.0.1:34567/"]).Port == 34567, "Actual assigned server ports are used");
        Check(BrowserAddress.ForServer(["invalid", "http://server:0/", "https://camera.example:9443/viewer/"]).AbsoluteUri ==
            "https://camera.example:9443/viewer/", "Unusable addresses are skipped while scheme, host and path are preserved");
        Reject(() => BrowserAddress.ForServer([]), "Missing listening addresses are rejected");
        Reject(() => BrowserAddress.ForServer(["invalid"]), "Malformed listening addresses are rejected");

        var addressUri = BrowserAddress.Parse("https://viewer.example:7443/cameras/?name=Front%20door&mode=live");
        var windows = new TestPlatform(windows: true);
        windows.Environment["ProgramFiles(x86)"] = Path.GetFullPath("artifacts/Browser Programs (x86)");
        var edge = Path.Combine(windows.Environment["ProgramFiles(x86)"], "Microsoft", "Edge", "Application", "msedge.exe");
        windows.Executables.Add(edge);
        var logger = new TestLogger<BrowserLauncher>();
        var launcher = new BrowserLauncher(windows, logger);
        Check(launcher.Launch(new(), addressUri), "Windows auto mode discovers Edge outside PATH");
        Check(windows.Starts.Single().FileName == edge && !windows.Starts[0].UseShellExecute &&
            windows.Starts[0].ArgumentList.SequenceEqual(["--app=" + addressUri.AbsoluteUri]),
            "App mode passes one safely separated URL argument to the executable");

        var linux = new TestPlatform();
        linux.Environment["DISPLAY"] = ":0";
        linux.Environment["PATH"] = "/missing:/browser tools";
        linux.Executables.Add(Path.Combine("/browser tools", "chromium-browser"));
        Check(new BrowserLocator(linux).Find("chromium") == Path.Combine("/browser tools", "chromium-browser"),
            "Linux discovery supports executable aliases and paths containing spaces");
        Check(new BrowserLauncher(linux, logger).Launch(new(), addressUri) && linux.Starts[0].FileName.EndsWith("chromium-browser"),
            "Linux automatic selection reaches Chromium when Chrome is absent");
        foreach (var (browser, executable) in new[] { ("edge", "microsoft-edge-stable"), ("chrome", "google-chrome-stable"), ("brave", "brave-browser") })
        {
            linux.Executables.Add(Path.Combine("/browser tools", executable));
            Check(new BrowserLocator(linux).Find(browser) == Path.Combine("/browser tools", executable), "Linux discovers " + browser);
        }
        linux.Starts.Clear();
        Check(new BrowserLauncher(linux, logger).Launch(new()
        {
            Browser = "brave"
        }, addressUri) &&
            linux.Starts[0].FileName.EndsWith("brave-browser"), "Explicit selection opens the requested browser");
        linux.Starts.Clear();
        linux.Environment.Remove("DISPLAY");
        linux.Environment["WAYLAND_DISPLAY"] = "wayland-0";
        Check(new BrowserLauncher(linux, logger).Launch(new()
        {
            Browser = "default"
        }, addressUri) &&
            linux.Starts.Single().UseShellExecute, "Wayland desktops can open the default browser");

        var fallback = new TestPlatform(windows: true);
        Check(new BrowserLauncher(fallback, logger).Launch(new(), addressUri) && fallback.Starts.Single().UseShellExecute &&
            fallback.Starts[0].FileName == addressUri.AbsoluteUri, "No app browser falls back to the default browser");
        Check(logger.Messages.Any(message => message.Contains("regular window")), "Fallback explains the regular window");
        fallback.Starts.Clear();
        Check(!new BrowserLauncher(fallback, logger).Launch(new()
        {
            Browser = "chrome"
        }, addressUri) && fallback.Starts.Count == 0,
            "Missing explicit browser reports failure without changing the selection");
        fallback.Failure = _ => new Win32Exception("No desktop browser handler");
        Check(!new BrowserLauncher(fallback, logger).Launch(new(), addressUri), "Default-browser launch failures are reported without throwing");
        Check(!new BrowserLauncher(new TestPlatform(), logger).Launch(new(), addressUri), "Headless Linux reports that a desktop session is required");

        windows.Starts.Clear();
        windows.Environment["PATH"] = Path.GetFullPath("artifacts/Browser Tools");
        var custom = Path.Combine(windows.Environment["PATH"], "custom.exe");
        windows.Executables.Add(custom);
        Check(launcher.Launch(new()
        {
            BrowserPath = "custom.exe"
        }, addressUri) && windows.Starts.Single().FileName == custom,
            "Custom executables can be resolved from PATH");
        windows.Starts.Clear();
        Check(launcher.Launch(new()
        {
            BrowserPath = custom
        }, addressUri) && windows.Starts.Single().FileName == custom,
            "Full custom paths preserve spaces without shell quoting");
        windows.Starts.Clear();
        Check(!launcher.Launch(new()
        {
            BrowserPath = "missing.exe"
        }, addressUri), "Missing custom executable reports failure");
        var chrome = Path.Combine(windows.Environment["PATH"], "chrome.exe");
        windows.Executables.Add(chrome);
        windows.Failure = info => info.FileName == edge ? new Win32Exception("Edge failed") : null;
        Check(launcher.Launch(new(), addressUri) && windows.Starts.Last().FileName == chrome, "Auto mode tries another browser after a launch failure");
        windows.Starts.Clear();
        Check(!launcher.Launch(new()
        {
            Browser = "edge"
        }, addressUri) && windows.Starts.Count == 1,
            "Explicit browser launch failure keeps the chosen browser");
        windows.Failure = info => !info.UseShellExecute ? new UnauthorizedAccessException("Browser denied") : null;
        windows.Starts.Clear();
        Check(launcher.Launch(new(), addressUri) && windows.Starts.Last().UseShellExecute, "Automatic launch failures eventually fall back to the default browser");
        windows.Failure = null;
        windows.Starts.Clear();

        var lifetime = new TestLifetime();
        var server = new TestServer();
        using (var service = new BrowserLaunchService(new()
        {
            Enabled = true
        }, launcher, server, lifetime, NullLogger<BrowserLaunchService>.Instance))
        {
            await service.StartAsync(CancellationToken.None);
            Check(windows.Starts.Count == 0, "Hosted startup does not launch before ApplicationStarted");
            server.Addresses.Add("http://0.0.0.0:34567");
            lifetime.Started.Cancel();
            Check(windows.Starts.Single().ArgumentList.Single() == "--app=http://localhost:34567/", "Ready event uses actual listening addresses");
            await service.StopAsync(CancellationToken.None);
        }
        windows.Starts.Clear();
        var overrideLifetime = new TestLifetime();
        using (var service = new BrowserLaunchService(new()
        {
            Enabled = true,
            Url = "https://viewer.example/"
        }, launcher, new TestServer(),
            overrideLifetime, NullLogger<BrowserLaunchService>.Instance))
        {
            await service.StartAsync(CancellationToken.None);
            overrideLifetime.Started.Cancel();
            Check(windows.Starts.Single().ArgumentList.Single() == "--app=https://viewer.example/", "HTTPS proxy overrides take precedence over listeners");
        }
        foreach (var scenario in new[] { "disabled", "stopped", "disposed", "no-address" })
        {
            windows.Starts.Clear();
            var testLifetime = new TestLifetime();
            using var service = new BrowserLaunchService(new()
            {
                Enabled = scenario != "disabled"
            }, launcher,
                new TestServer(), testLifetime, NullLogger<BrowserLaunchService>.Instance);
            await service.StartAsync(CancellationToken.None);
            if (scenario == "stopped")
            {
                testLifetime.Stopping.Cancel();
            }
            if (scenario == "disposed")
            {
                await service.StopAsync(CancellationToken.None);
            }
            testLifetime.Started.Cancel();
            Check(windows.Starts.Count == 0, "Hosted launch safely handles " + scenario);
        }
        Console.WriteLine("Browser launch unit checks passed.");
    }

    public static async Task CaptureAsync(string[] arguments)
    {
        var address = arguments.Single();
        var reachable = false;
        if (Environment.GetEnvironmentVariable("SCV_BROWSER_TEST_PROBE") == "true")
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync(address["--app=".Length..] + "api/state");
            reachable = response.IsSuccessStatusCode;
        }
        var capturePath = Environment.GetEnvironmentVariable("SCV_BROWSER_TEST_CAPTURE")!;
        await File.WriteAllTextAsync(capturePath + ".tmp", JsonSerializer.Serialize(new Capture(address, reachable)));
        File.Move(capturePath + ".tmp", capturePath);
    }

    public static async Task RunIntegrationAsync(string assemblyPath)
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "browser-launch-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var testExecutable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows()
            ? "SecurityCameraViewer.Web.Tests.exe" : "SecurityCameraViewer.Web.Tests");
        var serverAssembly = Path.GetFullPath(assemblyPath);
        foreach (var scenario in new[] { "startup", "connect", "connect-invalid-settings", "failed-browser", "invalid-url" })
        {
            var output = Path.Combine(directory, scenario + ".json");
            var settings = Path.Combine(directory, scenario + "-settings.json");
            if (scenario == "connect-invalid-settings")
            {
                await File.WriteAllTextAsync(settings, "invalid settings must never be loaded");
            }
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(serverAssembly);
            if (scenario == "startup" || scenario == "failed-browser")
            {
                foreach (var argument in new[] { "--demo", "--open-browser", "--urls", "http://127.0.0.1:0" })
                {
                    start.ArgumentList.Add(argument);
                }
            }
            else
            {
                start.ArgumentList.Add("--connect");
                start.ArgumentList.Add(scenario == "invalid-url" ? "file:///tmp/site" : "https://existing.example:9443/viewer/");
            }
            start.ArgumentList.Add("--browser-path");
            start.ArgumentList.Add(scenario == "failed-browser" ? Path.Combine(directory, "missing-browser.exe") : testExecutable);
            start.ArgumentList.Add("--settings");
            start.ArgumentList.Add(settings);
            start.Environment["SCV_BROWSER_TEST_CAPTURE"] = output;
            start.Environment["SCV_BROWSER_TEST_PROBE"] = scenario == "startup" ? "true" : "false";
            using var process = Process.Start(start)!;
            var lines = new List<string>();
            var stdout = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    lock (lines)
                    {
                        lines.Add(line);
                    }
                }
                lock (lines)
                {
                    return string.Join(Environment.NewLine, lines);
                }
            });
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                if (scenario == "startup")
                {
                    while (!File.Exists(output))
                    {
                        await Task.Delay(50, deadline.Token);
                    }
                    var capture = JsonSerializer.Deserialize<Capture>(await File.ReadAllTextAsync(output, deadline.Token))!;
                    Check(capture.ServerReachable && new Uri(capture.Argument["--app=".Length..]).Port != 0,
                        "Real startup opens the bound dynamic URL only after HTTP requests succeed");
                }
                else if (scenario == "failed-browser")
                {
                    string? boundAddress = null;
                    while (boundAddress is null)
                    {
                        lock (lines)
                        {
                            boundAddress = lines.FirstOrDefault(line => line.Contains("Browse: http://127.0.0.1:"))?.Split("Browse: ")[1].Trim();
                        }
                        await Task.Delay(50, deadline.Token);
                    }
                    using var http = new HttpClient();
                    using var response = await http.GetAsync(boundAddress + "api/state", deadline.Token);
                    Check(!process.HasExited && response.IsSuccessStatusCode, "Missing browser leaves the real server serving requests");
                }
                else
                {
                    await process.WaitForExitAsync(deadline.Token);
                    Check(process.ExitCode == (scenario.StartsWith("connect", StringComparison.Ordinal) ? 0 : 1), "Connect exits with the expected status: " + scenario);
                    Check(scenario == "connect-invalid-settings" ? await File.ReadAllTextAsync(settings, deadline.Token) == "invalid settings must never be loaded" :
                        !File.Exists(settings), "Connect skips camera settings and preserves existing files: " + scenario);
                    if (scenario.StartsWith("connect", StringComparison.Ordinal))
                    {
                        while (!File.Exists(output))
                        {
                            await Task.Delay(50, deadline.Token);
                        }
                        var capture = JsonSerializer.Deserialize<Capture>(await File.ReadAllTextAsync(output, deadline.Token))!;
                        Check(capture.Argument == "--app=https://existing.example:9443/viewer/", "Connect opens the existing address exactly");
                    }
                }
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
                await process.WaitForExitAsync();
                await File.WriteAllTextAsync(Path.Combine(directory, scenario + ".log"), await stdout + await stderr);
            }
        }
        Console.WriteLine("Browser launch integration checks passed.");
    }

    private static BrowserLaunchOptions Options(params (string Key, string Value)[] values) =>
        BrowserLaunchOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(value => value.Key, value => (string?)value.Value)).Build());

    private static void Reject(Action action, string description)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            Check(true, description);
            return;
        }
        throw new Exception("FAIL: " + description);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + description);
        }
        Console.WriteLine("PASS: " + description);
    }

    private sealed record Capture(string Argument, bool ServerReachable);

    private sealed class TestPlatform(bool windows = false) : IBrowserPlatform
    {
        public bool IsWindows => windows;
        public bool IsLinux => !windows;
        public Dictionary<string, string> Environment { get; } = [];
        public HashSet<string> Executables { get; } = [];
        public List<ProcessStartInfo> Starts { get; } = [];
        public Func<ProcessStartInfo, Exception?>? Failure
        {
            get; set;
        }
        public string? GetEnvironmentVariable(string name) => Environment.GetValueOrDefault(name);
        public bool IsExecutable(string path) => Executables.Contains(path);
        public void Start(ProcessStartInfo startInfo)
        {
            Starts.Add(startInfo);
            if (Failure?.Invoke(startInfo) is { } failure)
            {
                throw failure;
            }
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationTokenSource Started { get; } = new();
        public CancellationTokenSource Stopping { get; } = new();
        public CancellationToken ApplicationStarted => Started.Token;
        public CancellationToken ApplicationStopping => Stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopping.Cancel();
    }

    private sealed class TestAddresses(ICollection<string> addresses) : IServerAddressesFeature
    {
        public ICollection<string> Addresses => addresses;
        public bool PreferHostingUrls
        {
            get; set;
        }
    }

    private sealed class TestServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public ICollection<string> Addresses { get; } = new List<string>();
        public TestServer() => Features.Set<IServerAddressesFeature>(new TestAddresses(Addresses));
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose()
        {
        }
    }
}
