using System.Collections.Concurrent;
using System.Threading.Channels;
using SecurityCameraViewer.Nvr;

namespace SecurityCameraViewer.Web.Nvr;

public sealed record NvrExportJob(string Id, NvrRecordingRequest Recording, string State, string Message, DateTimeOffset Created,
    int RetrievalPercent = 0, long RetrievedBytes = 0);

public sealed class NvrExportService(INvrClient client, NvrSettingsStore settings, NvrExportStore store, INvrMediaConverter converter) : BackgroundService
{
    private sealed record Work(NvrExportJob Job, NvrConnection Connection, CancellationTokenSource Cancellation);
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(4)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly ConcurrentDictionary<string, NvrExportJob> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();

    public IReadOnlyList<NvrExportJob> Jobs => _jobs.Values.OrderByDescending(j => j.Created).ToArray();

    public NvrExportJob Start(NvrRecordingRequest request)
    {
        request.Validate(TimeSpan.FromHours(1));
        var connection = settings.GetConnection();
        var job = new NvrExportJob(Guid.NewGuid().ToString("N"), request, "Queued", "Waiting for the NVR transfer.", DateTimeOffset.UtcNow);
        var cancellation = new CancellationTokenSource();
        _jobs[job.Id] = job;
        _cancellations[job.Id] = cancellation;
        if (!_queue.Writer.TryWrite(new(job, connection, cancellation)))
        {
            _jobs.TryRemove(job.Id, out _);
            _cancellations.TryRemove(job.Id, out _);
            cancellation.Dispose();
            throw new NvrException("The export queue is full. Wait for an export to finish.");
        }

        foreach (var old in Jobs.Skip(100).Where(j => j.State is "Ready" or "Failed" or "Cancelled"))
        {
            _jobs.TryRemove(old.Id, out _);
        }

        return job;
    }

    public bool Cancel(string id)
    {
        if (!_cancellations.TryGetValue(id, out var cancellation))
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public bool DeleteMessage(string id)
    {
        return _jobs.TryGetValue(id, out var job) && job.State is "Failed" or "Cancelled" &&
            _jobs.TryRemove(id, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.CleanupAbandonedTransfers();
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var cancellation = work.Cancellation;
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cancellation.Token);
                lifetime.CancelAfter(TimeSpan.FromMinutes(20));
                var token = lifetime.Token;
                try
                {
                    token.ThrowIfCancellationRequested();
                    Update(work, "Retrieving", "Retrieving recorded video from the NVR.");
                    var input = store.TemporaryPath(work.Job.Id, "mkv");
                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Options = FileOptions.Asynchronous
                    };
                    if (!OperatingSystem.IsWindows())
                    {
                        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    }

                    await using (var stream = new FileStream(input, options))
                    {
                        await H264MatroskaWriter.WriteAsync(stream, client.ReadRecordingAsync(work.Connection, work.Job.Recording, token),
                            progress => ReportRetrievalProgress(work, progress), token);
                    }

                    Update(work, "Converting", "Preparing MP4 playback and download.", 100);
                    await converter.ConvertAsync(input, store.TemporaryPath(work.Job.Id, "mp4"), token);
                    token.ThrowIfCancellationRequested();
                    store.Commit(work.Job.Id, work.Job.Recording);
                    Update(work, "Ready", "MP4 saved on the server. Video only; audio is not included.");
                }
                catch (OperationCanceledException)
                {
                    Update(work, "Cancelled", cancellation.IsCancellationRequested ? "Export cancelled." : "Export stopped or exceeded the 20-minute transfer limit.");
                }
                catch (Exception ex) when (ex is NvrException or ArgumentException or IOException or UnauthorizedAccessException)
                {
                    Update(work, "Failed", ex is NvrException or ArgumentException ? ex.Message : "Could not save the export. Check server disk space and folder permissions.");
                }
                finally
                {
                    _cancellations.TryRemove(work.Job.Id, out _);
                    store.Cleanup(work.Job.Id);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out var remaining))
            {
                _cancellations.TryRemove(remaining.Job.Id, out _);
                remaining.Cancellation.Dispose();
            }
        }
    }

    private void ReportRetrievalProgress(Work work, NvrCaptureProgress progress)
    {
        // This is a recording-time estimate: variable bitrate and gaps prevent a byte-total percentage.
        // The explicit end-of-transfer marker is required before retrieval can reach 100%.
        var duration = work.Job.Recording.End - work.Job.Recording.Begin;
        var percentage = (int)Math.Clamp(progress.RecordedDuration.TotalSeconds / duration.TotalSeconds * 100, 0, 99);
        var current = _jobs[work.Job.Id];
        _jobs[work.Job.Id] = current with
        {
            RetrievalPercent = Math.Max(current.RetrievalPercent, percentage),
            RetrievedBytes = Math.Max(current.RetrievedBytes, progress.VideoBytes)
        };
    }

    private void Update(Work work, string state, string message, int? retrievalPercent = null)
    {
        var current = _jobs[work.Job.Id];
        _jobs[work.Job.Id] = current with
        {
            State = state,
            Message = message,
            RetrievalPercent = retrievalPercent ?? current.RetrievalPercent
        };
    }
}
