using SecurityCameraViewer.Core;
using SecurityCameraViewer.Web;

internal static class PreviewDemandTests
{
    public static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var demand = new PreviewDemand();
        var waiting = demand.WaitAsync(deadline.Token);
        LivePerformanceTests.Check(!waiting.IsCompleted && !demand.IsActive, "Preview work waits until a consumer exists");
        var first = demand.Acquire();
        var second = demand.Acquire();
        var active = await waiting;
        LivePerformanceTests.Check(demand.IsActive && !active.IsCancellationRequested && await demand.WaitAsync(deadline.Token) == active,
            "Concurrent browsers share a single active preview lifetime");
        first.Dispose();
        first.Dispose();
        LivePerformanceTests.Check(demand.IsActive && !active.IsCancellationRequested, "Releasing one browser twice cannot stop another browser");
        second.Dispose();
        LivePerformanceTests.Check(!demand.IsActive && active.IsCancellationRequested, "The final browser cancels active preview work");
        var next = demand.WaitAsync(deadline.Token);
        LivePerformanceTests.Check(!next.IsCompleted, "Idle work waits again after the last browser leaves");
        using var resumed = demand.Acquire();
        var renewed = await next;
        LivePerformanceTests.Check(renewed != active && !renewed.IsCancellationRequested, "A later viewer receives a fresh preview lifetime");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var idle = new PreviewDemand();
        try
        {
            await idle.WaitAsync(cancelled.Token);
            throw new Exception("Expected a cancelled wait.");
        }
        catch (OperationCanceledException)
        {
            LivePerformanceTests.Check(!idle.IsActive, "Cancelling one waiter cannot create preview demand");
        }
        var stopped = idle.WaitAsync(deadline.Token);
        idle.Dispose();
        LivePerformanceTests.Check(!idle.IsActive, "Disposed preview demand is inactive");
        try
        {
            await stopped;
            throw new Exception("Expected disposal to release idle waits.");
        }
        catch (OperationCanceledException)
        {
            LivePerformanceTests.Check(true, "Shutdown releases idle waits");
        }
        try
        {
            idle.Acquire();
            throw new Exception("Expected disposed demand to reject consumers.");
        }
        catch (ObjectDisposedException)
        {
            LivePerformanceTests.Check(true, "A disposed provider cannot start another decoder");
        }
        try
        {
            await idle.WaitAsync(deadline.Token);
            throw new Exception("Expected disposed wait rejection.");
        }
        catch (ObjectDisposedException)
        {
            LivePerformanceTests.Check(true, "A disposed provider rejects new capture waits");
        }
        using var disposingActive = new PreviewDemand();
        using var disappearing = disposingActive.Acquire();
        var disappearingToken = await disposingActive.WaitAsync(deadline.Token);
        disposingActive.Dispose();
        disappearing.Dispose();
        LivePerformanceTests.Check(disappearingToken.IsCancellationRequested && !disposingActive.IsActive,
            "Shutdown cancels active preview work and later consumer disposal is harmless");

        using var eager = new PreviewDemand(false);
        LivePerformanceTests.Check(eager.IsActive && await eager.WaitAsync(deadline.Token) == CancellationToken.None,
            "The compatibility setting retains continuous preview decoding");
        using var eagerConsumer = eager.Acquire();
        eagerConsumer.Dispose();
        LivePerformanceTests.Check(eager.IsActive, "Continuous previews are independent of consumer lifetimes");

        using var shared = new PreviewDemand();
        var leases = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(shared.Acquire)));
        var lifetime = await shared.WaitAsync(deadline.Token);
        await Task.WhenAll(leases.Select(lease => Task.Run(lease.Dispose)));
        LivePerformanceTests.Check(!shared.IsActive && lifetime.IsCancellationRequested,
            "Concurrent subscription and release maintain the final-consumer boundary");
        await CameraAsync(deadline.Token);
        await SubscriptionsAsync(deadline.Token);
    }

    private static async Task CameraAsync(CancellationToken token)
    {
        using var demand = new PreviewDemand();
        var router = new CountingRouter();
        await using var camera = new WebCamera(new(), "", 1, "missing-preview-test-ffmpeg", 0,
            connectionRouter: router, previewDemand: demand);
        await UntilAsync(() => camera.State.Status == "idle", token);
        LivePerformanceTests.Check(router.Attempts == 0 && camera.Frame is null && !camera.State.Live,
            "A connected headless camera launches no JPEG decoder and advertises paused preview state");
        using (demand.Acquire())
        {
            await UntilAsync(() => router.Attempts > 0 && camera.State.Status == "retrying", token);
            LivePerformanceTests.Check(true, "The first browser starts independent camera capture and normal failure recovery");
        }
        await UntilAsync(() => camera.State.Status == "idle", token);
        var attempts = router.Attempts;
        await Task.Delay(100, token);
        LivePerformanceTests.Check(router.Attempts == attempts, "Releasing the last browser stops retries as well as decoding");
        using (demand.Acquire())
        {
            await UntilAsync(() => router.Attempts > attempts, token);
            LivePerformanceTests.Check(true, "A new browser restarts preview recovery without reconnecting the recording service");
        }
        await UntilAsync(() => camera.State.Status == "idle", token);
        LivePerformanceTests.Check(await camera.SnapshotAsync(token) is null, "An offline first snapshot returns no picture without hanging");
        await UntilAsync(() => !demand.IsActive && camera.State.Status == "idle", token);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await camera.SnapshotAsync(cancelled.Token);
            throw new Exception("Expected snapshot cancellation.");
        }
        catch (OperationCanceledException)
        {
            LivePerformanceTests.Check(!demand.IsActive, "Cancelled snapshots cannot start preview work");
        }
    }

    private static async Task SubscriptionsAsync(CancellationToken token)
    {
        using var oldDemand = new PreviewDemand();
        using var newDemand = new PreviewDemand();
        await using var oldCamera = new WebCamera(new(), "", 1, "missing-test-ffmpeg", 1,
            connectionRouter: new CountingRouter(), previewDemand: oldDemand);
        await using var newCamera = new WebCamera(new(), "", 1, "missing-test-ffmpeg", 2,
            connectionRouter: new CountingRouter(), previewDemand: newDemand);
        using var first = new CameraPreviewSubscription();
        using var second = new CameraPreviewSubscription();
        first.Update([oldCamera, oldCamera]);
        first.Update([oldCamera]);
        second.Update([oldCamera]);
        first.Update([newCamera]);
        LivePerformanceTests.Check(oldDemand.IsActive && newDemand.IsActive,
            "Changing camera generations releases only that browser's previous lease");
        second.Dispose();
        LivePerformanceTests.Check(!oldDemand.IsActive && newDemand.IsActive,
            "Duplicate cameras do not leak preview leases");
        first.Update([]);
        first.Dispose();
        LivePerformanceTests.Check(!newDemand.IsActive, "An empty camera selection releases all preview leases");
        await newCamera.DisposeAsync();
        using var stopped = newCamera.SubscribePreview();
        LivePerformanceTests.Check(!newDemand.IsActive && await newCamera.SnapshotAsync(token) is null,
            "A stopped camera cannot be reactivated by a browser or snapshot");
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(10, token);
        }
    }

    private sealed class CountingRouter : ICameraConnectionRouter
    {
        public int Attempts;
        public CameraConnection Resolve(ViewerSettings settings, string password, int channel)
        {
            Interlocked.Increment(ref Attempts);
            throw new InvalidOperationException("Test routing failure");
        }
    }
}
