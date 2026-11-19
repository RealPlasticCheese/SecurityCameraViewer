using System.Collections.Concurrent;
using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

internal sealed class RecordingDriveHealthMonitor(RecordingService recording, DriveHealthProbe probe) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private Cache? _cache;

    public DriveHealthReport? GetReport(RecordingStoragePool pool, RecordingLocationStatus location)
    {
        var cache = Volatile.Read(ref _cache);
        return cache?.Pool == pool && location.Status is "ready" or "full" or "catching-up"
            ? cache.Reports.GetValueOrDefault(location.Directory) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            await RefreshAsync(recording.DriveHealthStorage, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RefreshAsync(RecordingStoragePool? pool, CancellationToken token)
    {
        if (pool is null)
        {
            Volatile.Write(ref _cache, null);
            return;
        }

        var cache = Volatile.Read(ref _cache);
        if (cache?.Pool != pool)
        {
            cache = new(pool);
            Volatile.Write(ref _cache, cache);
        }

        var targets = pool.DriveHealthTargets();
        foreach (var directory in cache.Reports.Keys)
        {
            if (!targets.ContainsKey(directory))
            {
                cache.Reports.TryRemove(directory, out _);
            }
        }

        foreach (var (directory, archiveDirectory) in targets)
        {
            token.ThrowIfCancellationRequested();
            if (!cache.Reports.TryGetValue(directory, out var report) || DateTimeOffset.UtcNow - report.CheckedAt >= RefreshInterval)
            {
                cache.Reports[directory] = await probe.ReadAsync(archiveDirectory, token);
            }
        }
    }

    private sealed record Cache(RecordingStoragePool Pool)
    {
        public ConcurrentDictionary<string, DriveHealthReport> Reports { get; } = new(StringComparer.Ordinal);
    }
}
