using System.Diagnostics;
using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Web;

/// <summary>Transient capabilities and exclusive speaker leases, scoped to the current viewer connection.</summary>
public sealed class TalkbackHub(ITalkbackSource source)
{
    private readonly object _sync = new();
    private readonly Dictionary<int, (long Started, Task<TalkbackCapability> Probe)> _capabilities = [];
    private readonly Dictionary<int, TalkbackLease> _speakers = [];
    private CancellationTokenSource _generation = new();

    public Task<TalkbackCapability> ProbeAsync(ViewerSettings settings, string password, int channel, CancellationToken token)
    {
        Task<TalkbackCapability> result;
        lock (_sync)
        {
            if (_speakers.ContainsKey(channel))
            {
                return Task.FromResult(new TalkbackCapability(true, "busy", "Another browser is using this camera's speaker."));
            }
            if (!_capabilities.TryGetValue(channel, out var cached) ||
                cached.Probe.IsCompleted && Stopwatch.GetElapsedTime(cached.Started).TotalSeconds > 15)
            {
                var generation = _generation.Token;
                cached = (Stopwatch.GetTimestamp(), Task.Run(async () =>
                {
                    try
                    {
                        return await source.ProbeAsync(settings, password, channel, generation);
                    }
                    catch (OperationCanceledException) when (!generation.IsCancellationRequested)
                    {
                        return new(false, "unavailable", "Camera audio backchannel check timed out.");
                    }
                }));
                _capabilities[channel] = cached;
            }
            result = cached.Probe;
        }
        return result.WaitAsync(token);
    }

    public TalkbackLease Reserve(ViewerSettings settings, string password, int channel, CancellationToken token)
    {
        lock (_sync)
        {
            if (_speakers.ContainsKey(channel))
            {
                throw new InvalidOperationException("Another browser is using this camera's speaker.");
            }
            if (!_capabilities.TryGetValue(channel, out var capability) || !capability.Probe.IsCompletedSuccessfully ||
                !capability.Probe.Result.Supported || Stopwatch.GetElapsedTime(capability.Started).TotalSeconds > 60)
            {
                throw new ArgumentException("Verify a supported camera audio backchannel before talking.");
            }
            TalkbackLease? lease = null;
            lease = new(source, settings, password, channel, _generation.Token, token, () =>
            {
                lock (_sync)
                {
                    if (_speakers.GetValueOrDefault(channel) == lease)
                    {
                        _speakers.Remove(channel);
                    }
                }
            });
            _speakers.Add(channel, lease);
            return lease;
        }
    }

    public async Task StopAsync()
    {
        TalkbackLease[] speakers;
        Task<TalkbackCapability>[] probes;
        CancellationTokenSource generation;
        lock (_sync)
        {
            speakers = [.. _speakers.Values];
            probes = [.. _capabilities.Values.Select(value => value.Probe)];
            _speakers.Clear();
            _capabilities.Clear();
            generation = _generation;
            _generation = new();
        }
        await generation.CancelAsync();
        await Task.WhenAll(speakers.Select(speaker => speaker.StopAsync()));
        try
        {
            await Task.WhenAll(probes);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            generation.Dispose();
        }
    }
}

public sealed class TalkbackLease : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly Task<ITalkbackSession> _opening;
    private readonly CancellationToken _token;
    private readonly Action _release;
    private Task? _stopping;
    public CancellationToken Token => _token;
    public Task<ITalkbackSession> Session => _opening;

    internal TalkbackLease(ITalkbackSource source, ViewerSettings settings, string password, int channel,
        CancellationToken generation, CancellationToken request, Action release)
    {
        _release = release;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(generation, request);
        _token = _lifetime.Token;
        _lifetime.CancelAfter(TimeSpan.FromSeconds(30));
        _opening = source.OpenAsync(settings, password, channel, _lifetime.Token);
    }

    public Task StopAsync()
    {
        lock (_sync)
        {
            return _stopping ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        await _lifetime.CancelAsync();
        try
        {
            var session = await _opening;
            await session.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException or FormatException)
        {
        }
        finally
        {
            _release();
            // Token can still be observed by the endpoint's finally block; dispose only its registrations.
            _lifetime.Dispose();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
