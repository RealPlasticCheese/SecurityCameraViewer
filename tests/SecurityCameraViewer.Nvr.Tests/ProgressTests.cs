using System.Runtime.CompilerServices;
using SecurityCameraViewer.Nvr;
using SecurityCameraViewer.Web.Nvr;

internal static class ProgressTests
{
    public static async Task RunAsync(NvrSettingsStore settings, NvrExportStore store, NvrRecordingRequest request, CancellationToken token)
    {
        foreach (var outcome in new[] { "Ready", "Cancelled", "Failed" })
        {
            var source = new ControlledSource(outcome == "Failed");
            var converter = new ControlledConverter();
            using var service = new NvrExportService(source, settings, store, converter);
            var job = service.Start(request);
            Check(job.RetrievalPercent == 0 && job.RetrievedBytes == 0, "Queued transfer starts with zero progress");
            Check(!service.DeleteMessage(job.Id), "Queued transfer cannot be dismissed");
            await service.StartAsync(token);
            try
            {
                await source.MiddleWritten.Task.WaitAsync(token);
                var middle = service.Jobs.Single();
                Check(middle.State == "Retrieving" && middle.RetrievalPercent == 50 && middle.RetrievedBytes == 2 * ControlledSource.Data.Length,
                    "Retrieval reports video timestamp progress and received payload bytes");
                Check(!service.DeleteMessage(job.Id), "Active retrieval cannot be dismissed");

                if (outcome == "Cancelled")
                {
                    service.Cancel(job.Id);
                }
                else
                {
                    source.Continue.TrySetResult();
                }

                if (outcome == "Ready")
                {
                    await source.LastWritten.Task.WaitAsync(token);
                    var last = service.Jobs.Single();
                    Check(last.RetrievalPercent == 99, "Retrieval is capped below completion until the source finishes");
                    source.Finish.TrySetResult();
                    await converter.Started.Task.WaitAsync(token);
                    var preparing = service.Jobs.Single();
                    Check(preparing.State == "Converting" && preparing.RetrievalPercent == 100 && preparing.RetrievedBytes == 3 * ControlledSource.Data.Length,
                        "MP4 preparation retains completed retrieval and received bytes");
                    Check(!service.DeleteMessage(job.Id), "MP4 preparation cannot be dismissed");
                    converter.Finish.TrySetResult();
                }

                while (service.Jobs.Single().State != outcome)
                {
                    await Task.Delay(10, token);
                }

                var completed = service.Jobs.Single();
                Check(completed.RetrievalPercent == (outcome == "Ready" ? 100 : 50), $"{outcome} transfer preserves its retrieval progress");
                Check(completed.RetrievedBytes == (outcome == "Ready" ? 3 : 2) * ControlledSource.Data.Length, $"{outcome} transfer preserves received bytes");
                if (outcome != "Ready")
                {
                    Check(!converter.Started.Task.IsCompleted, "Failed or cancelled retrieval does not prepare an MP4");
                    var saved = store.List();
                    Check(service.DeleteMessage(job.Id) && service.Jobs.Count == 0, $"{outcome} message is removed from subsequent transfer lists");
                    Check(!service.DeleteMessage(job.Id), "An already removed message is not found");
                    Check(store.List().SequenceEqual(saved), "Deleting a transfer message preserves saved clips");
                }
                else
                {
                    Check(!service.DeleteMessage(job.Id), "A saved clip cannot be deleted through message dismissal");
                }
            }
            finally
            {
                await service.StopAsync(token);
            }
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }

        Console.WriteLine("PASS: " + name);
    }

    private sealed class ControlledSource(bool fail) : INvrClient
    {
        // Minimal Annex-B headers exercise packaging and reporting; video decoding is tested separately.
        public static readonly byte[] Data = [0, 0, 0, 1, 0x67, 0x42, 0, 0x1E, 0, 0, 1, 0x68, 0xCE, 0, 0, 1, 0x65, 0x88];
        public TaskCompletionSource MiddleWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LastWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<NvrDeviceInfo> GetDeviceInfoAsync(NvrConnection connection, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<NvrRecording>> SearchAsync(NvrConnection connection, NvrRecordingRequest request, CancellationToken token) => throw new NotSupportedException();

        public async IAsyncEnumerable<NvrVideoFrame> ReadRecordingAsync(NvrConnection connection, NvrRecordingRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            var durationMicroseconds = (ulong)((request.End - request.Begin).Ticks / 10);
            yield return new(Data, NvrVideoCodec.H264, true, 1_000_000, 320, 240);
            yield return new(Data, NvrVideoCodec.H264, true, 1_000_000 + durationMicroseconds / 2, 320, 240);
            MiddleWritten.TrySetResult();
            await Continue.Task.WaitAsync(token);
            if (fail)
            {
                throw new NvrException("Simulated recorder disconnection.");
            }

            yield return new(Data, NvrVideoCodec.H264, true, 1_000_000 + durationMicroseconds * 2, 320, 240);
            LastWritten.TrySetResult();
            await Finish.Task.WaitAsync(token);
        }
    }

    private sealed class ControlledConverter : INvrMediaConverter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ConvertAsync(string inputPath, string outputPath, CancellationToken token)
        {
            Started.TrySetResult();
            await Finish.Task.WaitAsync(token);
            await File.WriteAllBytesAsync(outputPath, [1, 2, 3], token);
        }
    }
}
