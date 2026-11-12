using RtspVideoHarness.Tests;

if (args.Contains("--dump-single-json"))
{
    var mode = Environment.GetEnvironmentVariable("RTSP_YTDLP_TEST_MODE");
    if (mode == "hang")
    {
        await Task.Delay(Timeout.Infinite);
    }
    if (mode == "unsupported")
    {
        Console.Error.WriteLine("yt-dlp: error: no such option: --no-plugin-dirs");
        return 2;
    }
    if (mode == "failure")
    {
        Console.Error.WriteLine("Fixture extractor: livestream unavailable.");
        return 9;
    }
    Console.WriteLine(Environment.GetEnvironmentVariable("RTSP_YTDLP_TEST_JSON") ?? "{}");
    return 0;
}

try
{
    await UnitTests.RunAsync();
    await YouTubeTests.RunAsync();
    await RunScriptTests.RunAsync();
    if (args.Contains("--integration"))
    {
        await IntegrationTests.RunAsync(args);
    }

    Console.WriteLine($"RTSP harness tests passed: {Check.Count} checks.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
