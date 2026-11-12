using System.Runtime.InteropServices;
using RtspVideoHarness;

static void PrintHelp()
{
    Console.WriteLine("""
        RTSP video playlist test harness
        Usage: RtspVideoHarness [folder|YouTube-URL] [options]

        folder                    Defaults to the caller's current directory.
        --youtube URL             YouTube video or active livestream (also accepted positionally).
        --yt-dlp PATH             yt-dlp executable (beside the DLL or on PATH).
        --youtube-format FORMAT   yt-dlp format selector (default: best video plus available audio).
        --order name|created|modified   Sort by file name (default) or filesystem UTC dates.
        --descending              Reverse the selected order.
        --recursive               Include subfolders.
        --extensions .mp4,.mkv    Override the video extensions; '*' accepts all files.
        --blank-ms N              Gap between files in milliseconds (default: 0).
        --offline-gaps            Disconnect during gaps; otherwise show black frames.
        --no-audio                Disable audio (default: source audio or silence, encoded as AAC).
        --once                    Play once and exit; by default repeat until Ctrl+C.
        --port N                  RTSP TCP port (default: 8554).
        --listen IP               Bind address (default: 127.0.0.1); 0.0.0.0 enables LAN clients.
        --path NAME               RTSP path (default: sample).
        --size WIDTHxHEIGHT       Output size, even dimensions (default: 1280x720).
        --fps N                   Output frames per second (default: 25).
        --ffmpeg PATH             FFmpeg executable (default: ffmpeg on PATH).
        --ffprobe PATH            FFprobe executable (default: beside FFmpeg, or on PATH).
        --mediamtx PATH           MediaMTX executable (default: beside the harness DLL).
        --timeout-seconds N       Process startup/shutdown and YouTube extraction timeout (default: 30).
        --folder PATH             Explicit folder option; '-- PATH' accepts names starting with '-'.

        H.264 and AAC over RTSP/TCP. Black gaps round up to the next output frame.
        Offline gaps disconnect readers; clients must reconnect to the same URL.
        Example: RtspVideoHarness ./videos --order created --blank-ms 2000 --port 8555
        Example: RtspVideoHarness "https://www.youtube.com/watch?v=VIDEO_ID" --port 8555
        """);
}

using var stop = new CancellationTokenSource();
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stop.Cancel();
});
ConsoleCancelEventHandler cancel = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.Cancel();
};
Console.CancelKeyPress += cancel;
try
{
    var options = HarnessOptions.Parse(args);
    if (options.ShowHelp)
    {
        PrintHelp();
        return 0;
    }

    IReadOnlyList<VideoFile> files;
    if (options.YouTubeUrl is { } youtube)
    {
        files = [new VideoFile(youtube.AbsoluteUri, "YouTube", DateTime.UnixEpoch, DateTime.UnixEpoch)];
        Console.WriteLine($"YouTube: {youtube}");
    }
    else
    {
        files = VideoCatalog.Discover(options);
        Console.WriteLine($"Folder: {options.Folder} ({files.Count} videos, {options.Order}{(options.Descending ? " descending" : " ascending")})");
    }
    Console.WriteLine($"RTSP: {options.StreamUri} (TCP); bind {options.ListenAddress}:{options.Port}");
    if (options.ListenAddress.Equals(System.Net.IPAddress.Any) || options.ListenAddress.Equals(System.Net.IPAddress.IPv6Any))
    {
        Console.WriteLine($"LAN: rtsp://<this-host's-IP>:{options.Port}/{options.StreamPath}");
    }

    Console.WriteLine($"Gap: {options.BlankPeriod.TotalMilliseconds:0} ms ({(options.OfflineGaps ? "offline" : "black")}); {(options.Once ? "one pass" : "looping")}. Ctrl+C to stop.");
    await using var server = await MediaMtxServer.StartAsync(options, stop.Token);
    await using var playback = new FfmpegPlayback(options, options.YouTubeUrl == null ? null : new YouTubeSourceResolver());
    using var playing = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
    var playlist = new PlaylistPlayer(playback).RunAsync(files, options, playing.Token);
    if (await Task.WhenAny(playlist, server.Completion) == server.Completion)
    {
        playing.Cancel();
        try
        {
            await playlist;
        }
        catch (OperationCanceledException) when (playing.IsCancellationRequested)
        {
        }

        throw server.Failure();
    }

    await playlist;
    await playback.DisconnectAsync(stop.Token);
    return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Console.WriteLine("Stopped.");
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or OverflowException or TimeoutException)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancel;
}
