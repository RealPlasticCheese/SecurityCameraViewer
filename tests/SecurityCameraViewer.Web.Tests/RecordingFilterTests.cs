using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

internal static class RecordingFilterTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }

        Console.WriteLine("PASS: " + name);
    }

    public static async Task RunAsync()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "recording-filter-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["settings"] = Path.Combine(directory, "settings.json")
        }).Build();
        var viewerStore = new WebSettingsStore(new JsonAppSettingsStore(configuration));
        var filters = new RecordingFilterStore(viewerStore.SettingsStore);
        var priority = new RecordingZoneSelection(1, Guid.NewGuid().ToString("N"), "Priority entrance");
        var ordinary = new EventZone(Guid.NewGuid().ToString("N"), "Ordinary motion");
        var since = new DateTimeOffset(2026, 10, 3, 14, 0, 0, TimeSpan.Zero);
        var before = since.AddDays(1);
        var eventsDirectory = Path.Combine(directory, "library", "events");
        Directory.CreateDirectory(eventsDirectory);
        var settings = new RecordingSettings
        {
            Directory = Path.GetDirectoryName(eventsDirectory)!,
            Cameras =
            [
                new()
                {
                    Channel = 1,
                    Zones =
                    [
                        new(0, 0, 1, 1, Name: priority.Name)
                        {
                            Id = priority.Id
                        }
                    ]
                }
            ]
        };
        var matches = Enumerable.Range(0, 52).Select(index => new RecordingEvent
        {
            Channel = 1,
            CameraName = "Entrance",
            Protected = true,
            TriggeredAt = since.AddHours(1).AddMinutes(index),
            Trigger = new(Zones: [ordinary]),
            AdditionalTriggers = [new(Zones: [new(priority.Id, "Previous zone name")])]
        }).ToArray();
        var nonmatches = Enumerable.Range(0, 65).Select(index => new RecordingEvent
        {
            Channel = 1,
            CameraName = "Entrance",
            Protected = true,
            TriggeredAt = since.AddHours(3).AddMinutes(index),
            Trigger = new(Zones: [ordinary])
        }).ToArray();
        var otherCamera = new RecordingEvent
        {
            Channel = 2,
            CameraName = "Back yard",
            Protected = true,
            TriggeredAt = since.AddHours(5),
            Trigger = new(Zones: [new(priority.Id, "Back yard priority")])
        };
        var legacy = new RecordingEvent
        {
            Channel = 1,
            Protected = true,
            TriggeredAt = since.AddHours(6)
        };
        var boundary = matches[0] with
        {
            Id = Guid.NewGuid().ToString("N"),
            TriggeredAt = before
        };
        foreach (var item in matches.Concat(nonmatches).Concat([otherCamera, legacy, boundary]))
        {
            await File.WriteAllTextAsync(Path.Combine(eventsDirectory, item.Id + ".json"), JsonSerializer.Serialize(item));
            await File.WriteAllBytesAsync(Path.Combine(eventsDirectory, item.Id + ".mp4"), [0]);
        }

        var store = new FileRecordingStore(settings, new FfmpegProcess("missing-ffmpeg"));
        var search = new RecordingSearch
        {
            Zones = [priority],
            Since = since,
            Before = before
        };
        var first = store.Search(search);
        Check(first.Length == 50 && first.All(item => item.Channel == 1 && item.AdditionalTriggers.Length > 0),
            "Zone filtering searches additional triggers before paging and distinguishes camera channels");
        var second = store.Search(search with
        {
            Before = first[^1].TriggeredAt
        });
        Check(second.Length == 2 && !second.Any(item => first.Any(previous => previous.Id == item.Id)),
            "Older matching recordings remain available after a full filtered page");
        Check(store.Search(search with
        {
            Channel = 2
        }).Length == 0 &&
            store.Search(search with
            {
                Zones =
                [
                    priority, priority with
                    {
                        Channel = 2
                    }
                ]
            }).First().Id == otherCamera.Id,
            "Any selected zone matches and the camera filter intersects zone selections");
        Check(store.Search(new()
        {
            Channel = 1,
            Since = legacy.TriggeredAt,
            Before = before
        }).Single().Id == legacy.Id,
            "Unfiltered legacy recordings remain visible and date bounds include the start and exclude the end");
        var groups = store.ZoneGroups();
        Check(groups.Length == 2 && groups.Single(group => group.Channel == 1).Zones.Any(zone => zone.Id == priority.Id) &&
            groups.Single(group => group.Channel == 2).Zones.Single().Name == "Back yard priority",
            "Historical zone choices include all trigger metadata and stay grouped by camera");
        Check(new RecordingSearch
        {
            Zones = [priority, priority]
        }.Validate() is not null && new RecordingSearch
        {
            Since = before,
            Before = since
        }.Validate() is not null && new RecordingSearch
        {
            Zones = null!
        }.Validate() is not null,
            "Invalid date ranges and null or duplicate zone selections are rejected");

        var filter = new RecordingPlaybackFilter
        {
            Channel = 1,
            Date = new(2026, 10, 4),
            Zones = [priority]
        };
        var saved = await filters.SaveAsync("default", new(null, "  Priority activity  ", filter));
        Check(saved.Name == "Priority activity" && (await new RecordingFilterStore(viewerStore.SettingsStore).ListAsync("default")).Single().Filter.Date == filter.Date,
            "Named filters persist camera, local date and zones in backend configuration");
        var replaced = await filters.SaveAsync("default", new(saved.Id, null, new()
        {
            Zones = [priority]
        }));
        Check(replaced.Id == saved.Id && replaced.Name == saved.Name && (await filters.ListAsync("default")).Length == 1 &&
            replaced.Filter.Channel is null && replaced.Filter.Date is null,
            "Replacing a selected preset keeps its name and ID and updates all filter values");
        await RejectAsync(() => filters.SaveAsync("default", new(null, "priority activity", filter)), "Duplicate names require explicit replacement");
        await RejectAsync(() => filters.SaveAsync("default", new("missing", null, filter)), "A missing replacement cannot silently create a preset");
        await RejectAsync(() => filters.SaveAsync("default", new(null, " ", filter)), "Blank filter names are rejected");
        await RejectAsync(() => filters.SaveAsync("default", new(null, "Invalid", filter with
        {
            Zones = [priority, priority]
        })),
            "Invalid saved zone selections are rejected");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => filters.SaveAsync("default", new(null, $"Concurrent {index}", new()))));
        Check((await filters.ListAsync("default")).Length == 9, "Concurrent preset saves do not lose updates");
        var otherConfiguration = Guid.NewGuid().ToString("N");
        Check((await filters.ListAsync(otherConfiguration)).Length == 0, "Presets are isolated by camera configuration");
        viewerStore.SettingsStore.Update(current => current with
        {
            Configurations = [.. current.Configurations, new() { Connection = new() { ConfigurationId = otherConfiguration } }]
        });
        await filters.SaveAsync(otherConfiguration, new(null, saved.Name, filter));
        Check((await filters.ListAsync(otherConfiguration)).Length == 1 && (await filters.ListAsync("default")).Length == 9,
            "Configurations can use the same preset name independently");
        var validCatalog = await File.ReadAllTextAsync(viewerStore.FilePath);
        await File.WriteAllTextAsync(viewerStore.FilePath, "{invalid");
        try
        {
            using var invalidStore = new JsonAppSettingsStore(configuration);
            throw new Exception("Missing damaged document rejection");
        }
        catch (InvalidDataException)
        {
            Check(await File.ReadAllTextAsync(viewerStore.FilePath) == "{invalid", "A damaged settings document fails startup and is preserved");
        }
        await File.WriteAllTextAsync(viewerStore.FilePath, validCatalog);

        await ApiTestsAsync(directory, viewerStore, configuration, settings, search, filter);
    }

    private static async Task RejectAsync(Func<Task<SavedRecordingFilter>> action, string name)
    {
        try
        {
            await action();
            throw new Exception("Invalid filter accepted");
        }
        catch (ArgumentException)
        {
            Check(true, name);
        }
    }

    private static async Task ApiTestsAsync(string directory, WebSettingsStore viewerStore, IConfiguration configuration,
        RecordingSettings settings, RecordingSearch search, RecordingPlaybackFilter filter)
    {
        viewerStore.SettingsStore.Update(current => current with
        {
            ActiveConfigurationId = "default",
            Configurations = current.Configurations.Select(item => item.Connection.ConfigurationId == "default"
                ? item with { Connection = new() { AutoConnect = false }, Recording = settings } : item).ToArray()
        });
        var integrations = new IntegrationSettings(viewerStore.SettingsStore, configuration);
        var (_, adminSecret) = integrations.CreateToken("Filter test admin", ["admin"], null);
        var (_, readSecret) = integrations.CreateToken("Filter test reader", ["read"], null);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminSecret);
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { typeof(RecordingService).Assembly.Location, "--urls", http.BaseAddress.ToString(),
            "--settings", viewerStore.FilePath })
        {
            info.ArgumentList.Add(argument);
        }

        for (var run = 0; run < 2; run++)
        {
            using var server = Process.Start(info)!;
            var stdout = server.StandardOutput.ReadToEndAsync();
            var stderr = server.StandardError.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (true)
                {
                    try
                    {
                        using var response = await http.GetAsync("/api/recordings/filters", timeout.Token);
                        if (response.IsSuccessStatusCode)
                        {
                            break;
                        }
                    }
                    catch (HttpRequestException)
                    {
                    }

                    if (server.HasExited)
                    {
                        throw new Exception(await stderr + await stdout);
                    }
                    await Task.Delay(100, timeout.Token);
                }

                if (run == 1)
                {
                    var persisted = await http.GetFromJsonAsync<SavedRecordingFilter[]>("/api/recordings/filters");
                    Check(persisted!.Any(item => item.Name == "API preset" && item.Filter.Zones.Length == 512),
                        "Saved filters survive a web host restart");
                    continue;
                }

                using var searchResponse = await http.PostAsJsonAsync("/api/recordings/search", search);
                var found = await searchResponse.Content.ReadFromJsonAsync<RecordingEvent[]>();
                Check(searchResponse.IsSuccessStatusCode && found!.Length == 50 && found.All(item => item.Channel == 1),
                    "The web search endpoint returns filtered pages from recording metadata");
                using var badSearch = await http.PostAsJsonAsync("/api/recordings/search", search with
                {
                    Zones = null!
                });
                Check(badSearch.StatusCode == HttpStatusCode.BadRequest, "Invalid search payloads return HTTP 400");
                var largeFilter = filter with
                {
                    Zones = [.. Enumerable.Range(0, 512).Select(index => new RecordingZoneSelection(index % 16 + 1,
                        Guid.NewGuid().ToString("N"), new string('z', 80))) ]
                };
                using var savedResponse = await http.PostAsJsonAsync("/api/recordings/filters", new SaveRecordingFilterRequest(null, "API preset", largeFilter));
                Check(savedResponse.IsSuccessStatusCode, "Large presets support all 16 cameras and 32 zones per camera without the default body limit");
                var apiPreset = await savedResponse.Content.ReadFromJsonAsync<SavedRecordingFilter>();
                using var replace = await http.PostAsJsonAsync("/api/recordings/filters", new SaveRecordingFilterRequest(apiPreset!.Id, null, largeFilter));
                Check(replace.IsSuccessStatusCode, "The web API supports explicit replacement of existing presets");
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readSecret);
                using var readSearch = await http.PostAsJsonAsync("/api/recordings/search", search);
                using var readSave = await http.PostAsJsonAsync("/api/recordings/filters", new SaveRecordingFilterRequest(null, "Reader preset", filter));
                Check(readSearch.IsSuccessStatusCode && readSave.StatusCode == HttpStatusCode.Forbidden,
                    "Read tokens can search recordings while preset changes require administrator access");
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminSecret);
            }
            finally
            {
                if (!server.HasExited)
                {
                    server.Kill(true);
                }
                await server.WaitForExitAsync();
                await Task.WhenAll(stdout, stderr);
                await File.WriteAllTextAsync(Path.Combine(directory, $"server-{run}.log"), await stdout + await stderr);
            }
        }
    }
}
