using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SecurityCameraViewer.Web;

internal sealed record DriveHealthReport(DateTimeOffset CheckedAt, DriveHealthReading[] Drives, string? Message = null);

internal sealed record DriveHealthReading(string Device, string? Model, string Source, string Status,
    long? TemperatureCelsius = null, long? PowerOnHours = null, long? WearPercent = null,
    long? MediaErrors = null, long? ReallocatedSectors = null, long? PendingSectors = null, long? UncorrectableSectors = null,
    long? UncorrectedReadErrors = null, long? UncorrectedWriteErrors = null, string? Message = null);

internal sealed record DriveHealthCommandResult(int ExitCode, string Output);
internal delegate Task<DriveHealthCommandResult> DriveHealthCommand(string executable, string[] arguments, CancellationToken token);

internal sealed class DriveHealthProbe(DriveHealthCommand run)
{
    public async Task<DriveHealthReport> ReadAsync(string directory, CancellationToken token)
    {
        try
        {
            return OperatingSystem.IsWindows() ? await ReadWindowsAsync(directory, token, FindWindowsSmartctl()) :
                OperatingSystem.IsLinux() ? await ReadLinuxAsync(directory, token) :
                Unavailable("Drive diagnostics are unavailable on this operating system.");
        }
        catch (DriveHealthReadException ex)
        {
            return Unavailable(ex.Message);
        }
        catch (JsonException)
        {
            return Unavailable("The drive diagnostics tool returned unreadable data.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unavailable("Drive diagnostics could not be read by the server account.");
        }
    }

    internal async Task<DriveHealthReport> ReadLinuxAsync(string directory, CancellationToken token)
    {
        var mount = await run("findmnt", ["--list", "--direction", "forward", "--json", "--output", "MAJ:MIN,FSTYPE", "--target", directory], token);
        if (mount.ExitCode != 0)
        {
            return Unavailable("The recording folder's filesystem could not be identified.");
        }

        using var mountJson = JsonDocument.Parse(mount.Output);
        // With systemd automounts, findmnt lists the autofs trigger before the filesystem mounted over it.
        var filesystem = Items(mountJson.RootElement, "filesystems").LastOrDefault();
        var deviceNumber = String(filesystem, "maj:min");
        if (String(filesystem, "fstype") is "btrfs" or "bcachefs" or "zfs" ||
            deviceNumber?.Split(':') is not { Length: 2 } numbers ||
            !uint.TryParse(numbers[0], out var major) || major == 0 || !uint.TryParse(numbers[1], out var minor))
        {
            return Unavailable("The recording filesystem could not be mapped to local physical drives.");
        }

        var topology = await run("lsblk", ["--inverse", "--tree", "--json", "--output", "NAME,PATH,TYPE", $"/dev/block/{major}:{minor}"], token);
        if (topology.ExitCode != 0)
        {
            return Unavailable("The recording filesystem could not be mapped to local physical drives.");
        }

        using var topologyJson = JsonDocument.Parse(topology.Output);
        var devices = PhysicalDisks(Items(topologyJson.RootElement, "blockdevices")).Distinct(StringComparer.Ordinal).ToArray();
        if (devices.Length == 0)
        {
            return Unavailable("The recording filesystem could not be mapped to local physical drives.");
        }

        var drives = new List<DriveHealthReading>(devices.Length);
        foreach (var device in devices)
        {
            drives.Add(await ReadSmartAsync("smartctl", device, device, token));
        }

        return new(DateTimeOffset.UtcNow, [.. drives]);
    }

    internal async Task<DriveHealthReport> ReadWindowsAsync(string directory, CancellationToken token, string? smartctl = null)
    {
        // Encode the folder separately so it can never become part of the PowerShell program.
        var directory64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(directory));
        var script = WindowsStorageScript.Replace("RECORDING_DIRECTORY_BASE64", directory64, StringComparison.Ordinal);
        var command = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await run(executable, ["-NoProfile", "-NonInteractive", "-EncodedCommand", command], token);
        if (result.ExitCode != 0)
        {
            return Unavailable("Windows Storage could not read the recording drive. Check the server account's storage permissions.");
        }
        if (string.IsNullOrWhiteSpace(result.Output))
        {
            return Unavailable("Windows Storage could not map the recording folder to a supported local disk.");
        }

        using var json = JsonDocument.Parse(result.Output);
        var disk = json.RootElement;
        var device = Number(disk, "number");
        if (device is null)
        {
            return Unavailable("Windows Storage did not identify the recording disk.");
        }

        var label = $"Disk {device}";
        if (smartctl is not null)
        {
            var smart = await ReadSmartAsync(smartctl, $"/dev/pd{device}", label, token);
            if (smart.Status != "unavailable")
            {
                return new(DateTimeOffset.UtcNow, [smart with { Model = smart.Model ?? String(disk, "model") }]);
            }
        }

        var reading = new DriveHealthReading(label, String(disk, "model"), "Windows Storage",
            String(disk, "health") switch
            {
                "Healthy" => "healthy",
                "Warning" => "warning",
                "Unhealthy" => "failing",
                _ => "unknown"
            },
            TemperatureCelsius: Number(disk, "temperature"),
            PowerOnHours: Number(disk, "hours"),
            WearPercent: Number(disk, "wear"),
            UncorrectedReadErrors: Number(disk, "readErrors"),
            UncorrectedWriteErrors: Number(disk, "writeErrors"),
            Message: Value(disk, "hasCounters").ValueKind == JsonValueKind.True ? null :
                "Windows did not expose reliability counters for this disk.");
        return new(DateTimeOffset.UtcNow, [reading]);
    }

    private async Task<DriveHealthReading> ReadSmartAsync(string executable, string device, string label, CancellationToken token)
    {
        try
        {
            var result = await run(executable, ["--json", "--health", "--info", "--attributes", "--nocheck=standby,0", "--", device], token);
            return ParseSmart(label, result);
        }
        catch (DriveHealthReadException ex)
        {
            return Unreadable(label, ex.Message);
        }
        catch (JsonException)
        {
            return Unreadable(label, "smartctl returned unreadable data.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable(label, "SMART readings could not be accessed by the server account.");
        }
    }

    private static DriveHealthReading ParseSmart(string device, DriveHealthCommandResult result)
    {
        // smartctl's exit status is a bitmask. A failing health check can still return valid JSON.
        if ((result.ExitCode & 3) != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            return Unreadable(device, "smartctl could not open this drive. Check device support and the server account's permissions.");
        }

        using var json = JsonDocument.Parse(result.Output);
        var data = json.RootElement;
        var nvme = Value(data, "nvme_smart_health_information_log");
        var attributes = Items(Value(data, "ata_smart_attributes"), "table").ToArray();
        long? Attribute(int id) => Number(Value(attributes.FirstOrDefault(attribute => Number(attribute, "id") == id), "raw"), "value");

        var passed = Value(Value(data, "smart_status"), "passed").ValueKind;
        var criticalWarning = Number(nvme, "critical_warning");
        var status = criticalWarning > 0 ? "warning" :
            passed == JsonValueKind.False || (result.ExitCode & 8) != 0 ? "failing" :
            (result.ExitCode & 48) != 0 ? "warning" :
            passed == JsonValueKind.True ? "healthy" : "unknown";

        var reading = new DriveHealthReading(device, String(data, "model_name") ?? String(data, "scsi_model_name"),
            nvme.ValueKind == JsonValueKind.Object ? "NVMe SMART" : "SMART", status,
            TemperatureCelsius: Number(Value(data, "temperature"), "current") ?? Number(nvme, "temperature"),
            PowerOnHours: Number(Value(data, "power_on_time"), "hours") ?? Number(nvme, "power_on_hours"),
            WearPercent: Number(nvme, "percentage_used") ?? Number(data, "scsi_percentage_used_endurance_indicator"),
            MediaErrors: Number(nvme, "media_errors"),
            ReallocatedSectors: Attribute(5),
            PendingSectors: Attribute(197),
            UncorrectableSectors: Attribute(198),
            UncorrectedReadErrors: Number(Value(Value(data, "scsi_error_counter_log"), "read"), "total_uncorrected_errors"),
            UncorrectedWriteErrors: Number(Value(Value(data, "scsi_error_counter_log"), "write"), "total_uncorrected_errors"));
        if (reading is { Status: "unknown", TemperatureCelsius: null, PowerOnHours: null, WearPercent: null,
            MediaErrors: null, ReallocatedSectors: null, PendingSectors: null, UncorrectableSectors: null,
            UncorrectedReadErrors: null, UncorrectedWriteErrors: null })
        {
            return reading with { Status = "unavailable", Message = "No SMART readings returned. The drive may be asleep or diagnostics may be inaccessible." };
        }

        return reading;
    }

    private static IEnumerable<string> PhysicalDisks(IEnumerable<JsonElement> nodes)
    {
        foreach (var node in nodes)
        {
            if (String(node, "type") == "disk" && String(node, "path") is { } path && path.StartsWith("/dev/", StringComparison.Ordinal))
            {
                yield return path;
            }

            foreach (var parent in PhysicalDisks(Items(node, "children")))
            {
                yield return parent;
            }
        }
    }

    private static JsonElement Value(JsonElement node, string key) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) ? value : default;

    private static IEnumerable<JsonElement> Items(JsonElement node, string key) =>
        Value(node, key) is { ValueKind: JsonValueKind.Array } values ? values.EnumerateArray() : [];

    private static string? String(JsonElement node, string key) =>
        Value(node, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static long? Number(JsonElement node, string key) =>
        Value(node, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;

    private static DriveHealthReport Unavailable(string message) => new(DateTimeOffset.UtcNow, [], message);
    private static DriveHealthReading Unreadable(string device, string message) => new(device, null, "SMART", "unavailable", Message: message);

    private static string? FindWindowsSmartctl()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var executable = Path.Combine(programFiles, "smartmontools", "bin", "smartctl.exe");
        return Path.IsPathFullyQualified(executable) && File.Exists(executable) ? executable : null;
    }

    private const string WindowsStorageScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $directory = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('RECORDING_DIRECTORY_BASE64'))
        Add-Type -Namespace Scv -Name Volume -MemberDefinition @'
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern bool GetVolumePathName(string path, System.Text.StringBuilder volume, uint capacity);
        '@
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { return }
        $root = [Text.StringBuilder]::new(32768)
        if (-not [Scv.Volume]::GetVolumePathName($directory, $root, $root.Capacity)) { return }
        $partitions = @(Get-Partition | Where-Object { $_.AccessPaths -contains $root.ToString() })
        if ($partitions.Count -ne 1) { return }
        $disk = Get-Disk -Partition $partitions[0]
        if ([string]$disk.BusType -notin @('ATA', 'SATA', 'SAS', 'NVMe', 'USB', 'SCSI')) { return }
        $counters = Get-StorageReliabilityCounter -Disk $disk -ErrorAction SilentlyContinue
        [ordered]@{
            number = $disk.Number
            model = $disk.FriendlyName
            health = [string]$disk.HealthStatus
            hasCounters = ($null -ne $counters)
            temperature = $counters.Temperature
            hours = $counters.PowerOnHours
            wear = $counters.Wear
            readErrors = $counters.ReadErrorsUncorrected
            writeErrors = $counters.WriteErrorsUncorrected
        } | ConvertTo-Json -Compress
        """;

    internal static async Task<DriveHealthCommandResult> RunCommandAsync(string executable, string[] arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process
        {
            StartInfo = new(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        token.ThrowIfCancellationRequested();
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            throw new DriveHealthReadException($"Could not start {Path.GetFileName(executable)}. Check that it is installed and executable by the server account.");
        }

        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, stderr).WaitAsync(timeout.Token);
            return new(process.ExitCode, await output);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new DriveHealthReadException($"{Path.GetFileName(executable)} did not respond in time.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // The process may have exited during cancellation.
                }
            }
        }
    }
}

internal sealed class DriveHealthReadException(string message) : IOException(message);
