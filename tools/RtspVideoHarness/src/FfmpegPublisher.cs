using System.Net;
using System.Net.Sockets;

namespace RtspVideoHarness;

/// <summary>Feeds one persistent encoder with raw video and an optional local PCM audio pipe.</summary>
public sealed class FfmpegPublisher : IAsyncDisposable
{
    private readonly HarnessOptions options;
    private readonly ManagedProcess process;
    private readonly CancellationTokenSource stop = new();
    private readonly TcpListener? audioListener;
    private readonly Task<TcpClient>? audioConnection;

    public FfmpegPublisher(HarnessOptions options)
    {
        this.options = options;
        try
        {
            string? audioInput = null;
            if (options.AudioEnabled)
            {
                // An OS-assigned loopback port avoids conflicts between harness instances on both hosts.
                audioListener = new TcpListener(IPAddress.Loopback, 0);
                audioListener.Start();
                audioConnection = audioListener.AcceptTcpClientAsync(stop.Token).AsTask();
                audioInput = $"tcp://127.0.0.1:{((IPEndPoint)audioListener.LocalEndpoint).Port}";
            }

            process = new ManagedProcess(options.FfmpegExecutable, FfmpegCommands.Publisher(options, audioInput), "FFmpeg publisher", input: true);
        }
        catch
        {
            stop.Cancel();
            audioListener?.Stop();
            stop.Dispose();
            throw;
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> video, ReadOnlyMemory<byte> audio, CancellationToken cancellationToken)
    {
        if (process.Completion.IsCompleted)
        {
            await process.EnsureSuccessAsync(cancellationToken);
            throw process.Failure();
        }

        // Both inputs must progress together: FFmpeg may probe either before reading the other.
        var write = Task.WhenAll(process.StandardInput.WriteAsync(video, cancellationToken).AsTask(),
            audioConnection == null ? Task.CompletedTask : WriteAudioAsync(audio, cancellationToken));
        if (await Task.WhenAny(write, process.Completion).WaitAsync(cancellationToken) == process.Completion)
        {
            await process.EnsureSuccessAsync(cancellationToken);
            throw process.Failure();
        }

        await write;
    }

    private async Task WriteAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken)
    {
        var client = await audioConnection!.WaitAsync(options.ProcessTimeout, cancellationToken);
        await client.GetStream().WriteAsync(audio, cancellationToken);
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        process.StandardInput.Close();
        if (audioConnection is { IsCompletedSuccessfully: true })
        {
            audioConnection.Result.Client.Shutdown(SocketShutdown.Send);
        }

        await process.EnsureSuccessAsync(cancellationToken).WaitAsync(options.ProcessTimeout, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        audioListener?.Stop();
        try
        {
            if (audioConnection != null)
            {
                try
                {
                    using var client = await audioConnection;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            try
            {
                await process.DisposeAsync();
            }
            finally
            {
                stop.Dispose();
            }
        }
    }
}
