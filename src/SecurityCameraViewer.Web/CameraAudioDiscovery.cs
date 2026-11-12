using System.Diagnostics;
using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Web;

/// <summary>Discovers main-stream audio without starting playback; retries transient probe failures.</summary>
public sealed class CameraAudioDiscovery : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private ProbeResult? _result;
    private Task? _disposing;
    public ProbeResult? Result => Volatile.Read(ref _result);

    public CameraAudioDiscovery(Func<CancellationToken, Task<ProbeResult>> probe,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _worker = Task.Run(async () =>
        {
            var recovery = new CameraRecovery();
            var clock = Stopwatch.StartNew();
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    recovery.StartAttempt(clock.Elapsed);
                    try
                    {
                        var result = await probe(_stop.Token);
                        if (result.Success && result.HasAudio is not null)
                        {
                            Volatile.Write(ref _result, result);
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // Unreachable cameras and routing errors leave audio unknown, never falsely absent.
                    }
                    recovery.Failed(clock.Elapsed);
                    var wait = recovery.RetryAt!.Value - clock.Elapsed;
                    await (delay ?? Task.Delay)(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        lock (_stop)
        {
            return new(_disposing ??= StopAsync());
        }
    }

    private async Task StopAsync()
    {
        await _stop.CancelAsync();
        await _worker;
        _stop.Dispose();
    }
}
