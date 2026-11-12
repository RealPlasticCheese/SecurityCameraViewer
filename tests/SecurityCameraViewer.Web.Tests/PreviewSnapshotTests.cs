using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Web;

internal static class PreviewSnapshotTests
{
    public static async Task RunAsync()
    {
        var folder = Path.GetFullPath(Path.Combine("artifacts", "preview-snapshot-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        var pause = Path.Combine(folder, "pause");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var fixture = new Process
        {
            StartInfo = new(OperatingSystem.IsWindows() ? "python" : "python3")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        fixture.StartInfo.ArgumentList.Add("tests/rtsp_fixture.py");
        fixture.StartInfo.Environment["RTSP_FIXTURE_PORT"] = port.ToString();
        fixture.StartInfo.Environment["RTSP_FIXTURE_PAUSE_FILE"] = pause;
        fixture.Start();
        var stdout = fixture.StandardOutput.ReadToEndAsync();
        var stderr = fixture.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var settings = new ViewerSettings { Host = "127.0.0.1", RtspPort = port, ChannelCount = 1, Username = "viewer" };
        try
        {
            while (!(await RtspProbe.DescribeAsync(settings, "local-only-test", 1, deadline.Token)).Success)
            {
                await Task.Delay(50, deadline.Token);
            }
            using var demand = new PreviewDemand();
            await using var camera = new WebCamera(settings, "local-only-test", 1, "ffmpeg", 7, previewDemand: demand);
            var frame = await camera.SnapshotAsync(deadline.Token, refresh: true);
            LivePerformanceTests.Check(frame!.Jpeg.AsSpan().StartsWith(new byte[] { 0xff, 0xd8 }) && camera.Generation == 7,
                "A first snapshot receives a real decoded JPEG from on-demand capture");
            while (camera.State.Status != "idle")
            {
                await Task.Delay(10, deadline.Token);
            }
            LivePerformanceTests.Check(!demand.IsActive && ReferenceEquals(frame, await camera.SnapshotAsync(deadline.Token)),
                "A retained snapshot can be returned without starting another decoder");
            LivePerformanceTests.Check(camera.VideoFrame is not null, "Recent preview frames remain valid for browser delivery");
            camera.CheckNow();

            var requestedAt = Stopwatch.GetTimestamp();
            var refreshed = await camera.SnapshotAsync(deadline.Token, refresh: true);
            LivePerformanceTests.Check(refreshed is not null && refreshed.Timestamp > requestedAt && !ReferenceEquals(frame, refreshed),
                "Refreshing a retained snapshot resumes preview capture and returns newly decoded video");
            await WaitForIdleAsync(camera, deadline.Token);
            LivePerformanceTests.Check(!demand.IsActive, "A successful refresh releases its temporary preview demand");

            using (var activePreview = camera.SubscribePreview())
            {
                requestedAt = Stopwatch.GetTimestamp();
                var concurrent = await Task.WhenAll(camera.SnapshotAsync(deadline.Token, refresh: true),
                    camera.SnapshotAsync(deadline.Token, refresh: true));
                LivePerformanceTests.Check(concurrent.All(snapshot => snapshot is not null && snapshot.Timestamp > requestedAt),
                    "Concurrent refreshes on an active preview each wait for a new frame");
                LivePerformanceTests.Check(demand.IsActive, "Refreshing does not stop another preview consumer");
            }
            await WaitForIdleAsync(camera, deadline.Token);

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ExpectCancellationAsync(camera.SnapshotAsync(cancelled.Token, refresh: true));
                LivePerformanceTests.Check(!demand.IsActive, "An already cancelled refresh does not start capture");
            }

            await File.WriteAllTextAsync(pause, "", deadline.Token);
            var retained = camera.Frame;
            using (var cancelled = new CancellationTokenSource())
            {
                var refreshing = camera.SnapshotAsync(cancelled.Token, refresh: true);
                await Task.Delay(250, deadline.Token);
                LivePerformanceTests.Check(!refreshing.IsCompleted && demand.IsActive,
                    "A stalled refresh waits for new video instead of immediately returning the retained JPEG");
                cancelled.Cancel();
                await ExpectCancellationAsync(refreshing);
                LivePerformanceTests.Check(!demand.IsActive, "Cancelling a refresh releases its preview demand");
            }
            await WaitForIdleAsync(camera, deadline.Token);

            LivePerformanceTests.Check(await camera.SnapshotAsync(deadline.Token, refresh: true) is null && !demand.IsActive,
                "A stalled refresh times out without returning stale video or leaving capture running");
            await WaitForIdleAsync(camera, deadline.Token);
            LivePerformanceTests.Check(ReferenceEquals(retained, await camera.SnapshotAsync(deadline.Token)),
                "An unsuccessful refresh preserves the retained picture for ordinary snapshot consumers");

            var shuttingDown = camera.SnapshotAsync(deadline.Token, refresh: true);
            await Task.Delay(250, deadline.Token);
            await camera.DisposeAsync();
            LivePerformanceTests.Check(await shuttingDown is null && !demand.IsActive,
                "Camera shutdown releases a pending refresh without returning its retained JPEG");
            LivePerformanceTests.Check(await camera.SnapshotAsync(deadline.Token, refresh: true) is null && !demand.IsActive,
                "A stopped camera cannot refresh a retained picture or restart capture");

            await using var blocked = new WebCamera(settings, "local-only-test", 1, "ffmpeg", 8, onDemand: true);
            var waiting = blocked.SnapshotAsync(deadline.Token);
            await Task.Delay(250, deadline.Token);
            await blocked.DisposeAsync();
            LivePerformanceTests.Check(await waiting is null, "Shutdown releases a first snapshot waiting on stalled input");

            await using var timeout = new WebCamera(settings, "local-only-test", 1, "ffmpeg", 9, onDemand: true);
            LivePerformanceTests.Check(await timeout.SnapshotAsync(deadline.Token) is null,
                "The existing startup watchdog bounds a first snapshot when RTSP connects without video");
        }
        finally
        {
            if (!fixture.HasExited)
            {
                fixture.Kill(entireProcessTree: true);
            }
            await fixture.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    private static async Task WaitForIdleAsync(WebCamera camera, CancellationToken token)
    {
        while (camera.State.Status != "idle")
        {
            await Task.Delay(10, token);
        }
    }

    private static async Task ExpectCancellationAsync(Task<CameraFrame?> snapshot)
    {
        try
        {
            await snapshot;
            throw new Exception("Expected snapshot cancellation.");
        }
        catch (OperationCanceledException)
        {
            LivePerformanceTests.Check(true, "Snapshot refresh respects caller cancellation");
        }
    }
}
