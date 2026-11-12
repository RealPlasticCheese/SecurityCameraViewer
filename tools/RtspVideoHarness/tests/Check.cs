namespace RtspVideoHarness.Tests;

internal static class Check
{
    public static int Count
    {
        get; private set;
    }

    public static void That(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAIL: " + message);
        }

        Count++;
        Console.WriteLine("PASS: " + message);
    }

    public static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            That(true, message);
            return;
        }

        throw new InvalidOperationException($"FAIL: expected {typeof(T).Name}: {message}");
    }

    public static Task ThrowsAsync<T>(Action action, string message) where T : Exception => ThrowsAsync<T>(() =>
    {
        action();
        return Task.CompletedTask;
    }, message);
}
