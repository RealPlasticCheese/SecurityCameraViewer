using System.ComponentModel;
using System.Diagnostics;

namespace RtspVideoHarness;

/// <summary>Owns one child process, drains diagnostics, and kills its process tree on disposal.</summary>
public sealed class ManagedProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task diagnostics;
    private readonly Task output;
    private readonly Queue<string> recentErrors = new();

    public ManagedProcess(string executable, IEnumerable<string> arguments, string label,
        bool binaryOutput = false, bool input = false, Action<string>? outputLine = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = input,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // The harness must not inherit FFmpeg file-reporting or MediaMTX configuration overrides.
        start.Environment.Remove("FFREPORT");
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("MTX_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }

        Label = label;
        process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new IOException($"Cannot start {label} ({executable}). Install it or supply its executable path.", exception);
        }

        Completion = process.WaitForExitAsync();
        diagnostics = DrainAsync(process.StandardError, true, outputLine);
        output = binaryOutput ? Task.CompletedTask : DrainAsync(process.StandardOutput, false, outputLine);
    }

    public string Label
    {
        get;
    }
    public Stream StandardOutput => process.StandardOutput.BaseStream;
    public Stream StandardInput => process.StandardInput.BaseStream;
    public Task Completion
    {
        get;
    }
    public int Id => process.Id;

    public async Task EnsureSuccessAsync(CancellationToken cancellationToken)
    {
        await Completion.WaitAsync(cancellationToken);
        await Task.WhenAll(diagnostics, output).WaitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw Failure();
        }
    }

    public IOException Failure()
    {
        string details;
        lock (recentErrors)
        {
            details = string.Join(Environment.NewLine, recentErrors);
        }

        return new IOException($"{Label} exited unexpectedly.{Environment.NewLine}{details}");
    }

    private async Task DrainAsync(StreamReader reader, bool error, Action<string>? callback)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (recentErrors)
            {
                recentErrors.Enqueue(line);
                // Retain recent diagnostics only; this is not a video or playlist limit.
                if (recentErrors.Count > 20)
                {
                    recentErrors.Dequeue();
                }
            }

            callback?.Invoke(line);
            if (error)
            {
                Console.Error.WriteLine($"[{Label}] {line}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await Completion;
            await Task.WhenAll(diagnostics, output);
        }
        finally
        {
            process.Dispose();
        }
    }
}
