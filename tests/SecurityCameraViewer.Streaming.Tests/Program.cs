using SecurityCameraViewer.Streaming.Tests;

await StreamingSettingsTests.RunAsync();
await OnvifProtocolTests.RunAsync();
await OnvifAuthenticationTests.RunAsync();
await OnvifEventTests.RunAsync();
await MediaRelayTests.RunAsync();
await RelayRoutingTests.RunAsync();
await StreamingFailureTests.RunAsync();
if (args.Contains("--schemas"))
{
    await OnvifSchemaTests.RunAsync();
}
if (args.Contains("--integration"))
{
    await StreamingIntegrationTests.RunAsync(args);
}

Console.WriteLine($"Streaming tests passed: {TestSupport.Count} checks.");
