using System.Diagnostics;
using System.Text;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;

internal static class RecordingDriveHealthTests
{
    private const string Mount = """{"filesystems":[{"maj:min":"253:1","fstype":"ext4"}]}""";
    private const string Ata = """
        {"model_name":"Archive HDD","smart_status":{"passed":false},"temperature":{"current":38},
         "power_on_time":{"hours":1850},"ata_smart_attributes":{"table":[
          {"id":5,"raw":{"value":0}},{"id":197,"raw":{"value":3}},{"id":198,"raw":{"value":2}}]}}
        """;
    private const string Nvme = """
        {"model_name":"Archive NVMe","smart_status":{"passed":false},"nvme_smart_health_information_log":{
         "critical_warning":2,"temperature":55,"power_on_hours":90,"percentage_used":102,"media_errors":4}}
        """;
    private const string WindowsDisk = """
        {"number":2,"model":"Archive SSD","health":"Warning","hasCounters":true,
         "temperature":45,"hours":80,"wear":25,"readErrors":0,"writeErrors":12}
        """;

    public static async Task RunAsync()
    {
        await LinuxStorageAsync();
        await LinuxUnavailableAsync();
        await WindowsStorageAsync();
        await LinuxDetailsAsync();
        await WindowsDetailsAsync();
        await MonitorAsync();
        await CommandFailureAsync();
    }

    private static async Task LinuxStorageAsync()
    {
        const string directory = "/media/archive folder [2]";
        List<string> smartDevices = [];
        var probe = new DriveHealthProbe((tool, args, _) =>
        {
            if (tool == "findmnt")
            {
                Check(args[^1] == directory && args[^2] == "--target", "Linux passes the recording path as a literal argument");
                return Result(0, Mount);
            }
            if (tool == "lsblk")
            {
                Check(args[^1] == "/dev/block/253:1" && args.Contains("--inverse"), "Linux looks up the matching filesystem's backing devices");
                return Result(0, """
                    {"blockdevices":[{"path":"/dev/dm-0","type":"crypt","children":[
                     {"path":"/dev/md0","type":"raid1","children":[
                      {"path":"/dev/sda1","type":"part","children":[{"path":"/dev/sda","type":"disk"}]},
                      {"path":"/dev/nvme0n1","type":"disk"},{"path":"/dev/sda","type":"disk"},{"path":"/dev/sdb","type":"disk"}]}]}]}
                    """);
            }

            Check(tool == "smartctl" && args.Contains("--health") && args.Contains("--attributes"), "Linux requests read-only SMART diagnostics");
            smartDevices.Add(args[^1]);
            return args[^1] switch
            {
                "/dev/sda" => Result(8, Ata),
                "/dev/nvme0n1" => Result(8, Nvme),
                _ => Result(2, """{"smartctl":{"exit_status":2}}""")
            };
        });

        var report = await probe.ReadLinuxAsync(directory, default);
        Check(smartDevices.SequenceEqual(["/dev/sda", "/dev/nvme0n1", "/dev/sdb"]), "SMART probes physical members once each");
        Check(report.Drives[0] is
            { Status: "failing", Model: "Archive HDD", TemperatureCelsius: 38, PowerOnHours: 1850, ReallocatedSectors: 0, PendingSectors: 3, UncorrectableSectors: 2 },
            "ATA failure and sector counters survive smartctl's nonzero health exit status");
        Check(report.Drives[1] is { Source: "NVMe SMART", Status: "warning", TemperatureCelsius: 55, PowerOnHours: 90, WearPercent: 102, MediaErrors: 4 },
            "NVMe exposes critical warnings, endurance and media errors");
        Check(report.Drives[2] is { Device: "/dev/sdb", Status: "unavailable", Message: not null },
            "An inaccessible RAID member leaves the other devices' readings visible");
    }

    private static async Task LinuxUnavailableAsync()
    {
        var network = new DriveHealthProbe((tool, _, _) => tool == "findmnt"
            ? Result(0, """{"filesystems":[{"maj:min":"0:39","fstype":"cifs"}]}""") : throw new Exception($"Unexpected probe of {tool}."));
        Check(await network.ReadLinuxAsync("/share", default) is { Drives.Length: 0, Message: not null }, "Network storage has no local drive report");

        foreach (var (exit, output, status) in new[]
        {
            (0, """{"smart_status":{"passed":true}}""", "healthy"),
            (16, """{"smart_status":{"passed":true}}""", "warning"),
            (4, """{"temperature":{"current":39}}""", "unknown"),
            (0, """{"power_mode":"STANDBY"}""", "unavailable"),
            (0, "bad json", "unavailable")
        })
        {
            var probe = new DriveHealthProbe((tool, _, _) => tool switch
            {
                "findmnt" => Result(0, Mount),
                "lsblk" => Result(0, """{"blockdevices":[{"path":"/dev/sda","type":"disk"}]}"""),
                _ => Result(exit, output)
            });
            var report = await probe.ReadLinuxAsync("/archive", default);
            Check(report.Drives.Single().Status == status, $"SMART exit {exit} with {output} produces {status}");
        }

        var missing = new DriveHealthProbe((tool, _, _) => tool switch
        {
            "findmnt" => Result(0, Mount),
            "lsblk" => Result(0, """{"blockdevices":[{"path":"/dev/sda","type":"disk"}]}"""),
            _ => throw new DriveHealthReadException("Could not start smartctl.")
        });
        Check((await missing.ReadLinuxAsync("/archive", default)).Drives.Single() is { Status: "unavailable", Message: "Could not start smartctl." },
            "Missing smartmontools produces an actionable unavailable report");
    }

    private static async Task WindowsStorageAsync()
    {
        const string directory = """C:\Recovery\camera's recordings & exports""";
        var native = new DriveHealthProbe((tool, args, _) =>
        {
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
            Check(Path.GetFileName(tool) == "powershell.exe" && !script.Contains(directory, StringComparison.Ordinal) &&
                script.Contains(Convert.ToBase64String(Encoding.Unicode.GetBytes(directory)), StringComparison.Ordinal),
                "Windows passes the directory without interpolating user content into PowerShell");
            return Result(0, WindowsDisk);
        });
        var windows = await native.ReadWindowsAsync(directory, default);
        Check(windows.Drives.Single() is { Device: "Disk 2", Source: "Windows Storage", Status: "warning",
            WearPercent: 25, UncorrectedReadErrors: 0, UncorrectedWriteErrors: 12 }, "Windows Storage exposes health, wear and error counters");

        var smart = new DriveHealthProbe((tool, args, _) =>
        {
            if (Path.GetFileName(tool) == "powershell.exe")
            {
                return Result(0, WindowsDisk);
            }
            Check(args[^1] == "/dev/pd2", "Windows SMART targets the mapped physical disk number");
            return Result(8, Nvme);
        });
        var preferred = await smart.ReadWindowsAsync(directory, default, "smartctl.exe");
        Check(preferred.Drives.Single() is { Device: "Disk 2", Source: "NVMe SMART", MediaErrors: 4 }, "Windows prefers available SMART readings");

        var fallback = new DriveHealthProbe((tool, _, _) => Path.GetFileName(tool) == "powershell.exe"
            ? Result(0, """{"number":2,"health":"Healthy","hasCounters":false}""") : Result(2, ""));
        Check((await fallback.ReadWindowsAsync(directory, default, "smartctl.exe")).Drives.Single() is
            { Source: "Windows Storage", Status: "healthy", WearPercent: null, Message: not null },
            "Windows falls back to the provider's status and leaves missing SMART counters empty");

        var noDisk = new DriveHealthProbe((_, _, _) => Result(0, ""));
        Check(await noDisk.ReadWindowsAsync(directory, default) is { Drives.Length: 0, Message: not null },
            "Windows unmapped volumes produce an unavailable report");
    }

    private static async Task LinuxDetailsAsync()
    {
        const string output = "Raw SMART attributes\nID# ATTRIBUTE_NAME\nSMART Error Log\nSMART Self-test log";
        var probe = new DriveHealthProbe((tool, args, _) => tool switch
        {
            "findmnt" => Result(0, Mount),
            "lsblk" => Result(0, """
                {"blockdevices":[{"path":"/dev/sda","type":"disk"},{"path":"/dev/sdb","type":"disk"}]}
                """),
            "smartctl" when args.SequenceEqual(["--xall", "--nocheck=standby,0", "--", "/dev/sda"]) => Result(8 | 64 | 128, output + "\n"),
            "smartctl" when args.SequenceEqual(["--xall", "--nocheck=standby,0", "--", "/dev/sdb"]) =>
                throw new DriveHealthReadException("smartctl did not respond in time."),
            _ => throw new Exception($"Unexpected command {tool} {string.Join(' ', args)}.")
        });

        var report = await probe.ReadLinuxDetailsAsync("/archive", default);
        Check(report.Drives[0] is { Device: "/dev/sda", Output: output, Message: null },
            "A full SMART report retains attributes and logs even when smartctl reports a failed health check");
        Check(report.Drives[1] is { Device: "/dev/sdb", Output: null, Message: "smartctl did not respond in time." },
            "One disk timing out keeps the other physical disks' details available");

        var inaccessible = new DriveHealthProbe((tool, _, _) => tool switch
        {
            "findmnt" => Result(0, Mount),
            "lsblk" => Result(0, """{"blockdevices":[{"path":"/dev/sdc","type":"disk"}]}"""),
            _ => Result(2, "Permission denied")
        });
        Check((await inaccessible.ReadLinuxDetailsAsync("/archive", default)).Drives.Single() is
            { Device: "/dev/sdc", Output: "Permission denied", Message: not null },
            "The report keeps useful tool output when a disk is inaccessible");
    }

    private static async Task WindowsDetailsAsync()
    {
        var probe = new DriveHealthProbe((tool, args, _) =>
        {
            if (Path.GetFileName(tool) == "powershell.exe")
            {
                var script = Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
                Check(script.Contains("if ($true)", StringComparison.Ordinal), "A Windows details request enables the native report");
                return Result(0, """{"number":2,"details":"Number : 2\nTemperatureMax : 55"}""");
            }

            Check(args.SequenceEqual(["--xall", "--nocheck=standby,0", "--", "/dev/pd2"]), "Windows requests the complete read-only SMART report");
            return Result(32, "SMART self-test log");
        });

        var details = await probe.ReadWindowsDetailsAsync(@"C:\Archive", "smartctl.exe", default);
        Check(details.Drives.Single() is { Device: "Disk 2", Source: "smartctl", Output: "SMART self-test log", Message: null },
            "Windows shows the complete SMART output when available");
        var fallback = await probe.ReadWindowsDetailsAsync(@"C:\Archive", null, default);
        Check(fallback.Drives.Single() is { Device: "Disk 2", Source: "Windows Storage", Message: not null,
            Output: "Number : 2\nTemperatureMax : 55" }, "Windows exposes additional native counters when smartmontools is absent");
    }

    private static async Task MonitorAsync()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "drive-health", Guid.NewGuid().ToString("N")));
        var settings = new RecordingSettings { Directory = directory };
        using var pool = new RecordingStoragePool(settings, new("ffmpeg"), NullCameraEventPublisher.Instance,
            TimeSpan.FromSeconds(1), new(), _ => { });
        using var replacement = new RecordingStoragePool(settings, new("ffmpeg"), NullCameraEventPublisher.Instance,
            TimeSpan.FromSeconds(1), new(), _ => { });
        var calls = 0;
        var probe = new DriveHealthProbe((_, _, _) =>
        {
            calls++;
            return Result(1, "");
        });
        using var monitor = new RecordingDriveHealthMonitor(null!, probe);
        var location = pool.Snapshot().Single();

        Check(monitor.GetReport(pool, location) is null, "A folder starts without a drive check");
        await monitor.RefreshAsync(pool, default);
        await monitor.RefreshAsync(pool, default);
        Check(calls == 1 && monitor.GetReport(pool, location) is { Message: not null },
            "Failed reads are cached without re-running diagnostics on each UI poll");
        Check(monitor.GetReport(pool, location with { Status = "offline" }) is null &&
            monitor.GetReport(pool, location with { Status = "wrong-folder" }) is null &&
            monitor.GetReport(replacement, location) is null, "Old readings are hidden for inaccessible or replaced recording locations");
        await monitor.RefreshAsync(replacement, default);
        Check(calls == 2 && monitor.GetReport(pool, location) is null && monitor.GetReport(replacement, location) is not null,
            "A replacement pool receives its own readings");
        await monitor.RefreshAsync(null, default);
        Check(monitor.GetReport(replacement, location) is null, "An inactive monitor exposes no drive readings");
    }

    private static async Task CommandFailureAsync()
    {
        try
        {
            await DriveHealthProbe.RunCommandAsync("scv-test-missing-executable-" + Guid.NewGuid().ToString("N"), [], default);
            throw new Exception("FAIL: Missing diagnostic program should report a read failure");
        }
        catch (DriveHealthReadException ex)
        {
            Check(ex.Message.Contains("installed and executable", StringComparison.Ordinal), "Process runner handles a missing tool");
        }

        var windows = OperatingSystem.IsWindows();
        var executable = windows
            ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : "/bin/sleep";
        string[] args = windows ? ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"] : ["60"];
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        var elapsed = Stopwatch.StartNew();
        try
        {
            await DriveHealthProbe.RunCommandAsync(executable, args, canceled.Token);
            throw new Exception("FAIL: A canceled command should not finish normally");
        }
        catch (OperationCanceledException)
        {
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(5), "Canceling a running diagnostic does not wait for the child to exit");
        }
    }

    private static Task<DriveHealthCommandResult> Result(int exit, string output) => Task.FromResult(new DriveHealthCommandResult(exit, output));

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }

        Console.WriteLine("PASS: " + name);
    }
}
