using System.Text.Json;
using GameUpdater.Launcher;

// No namespace: matches Program.cs in this project (global namespace).

/// <summary>servers.json parsing + ServerHealthCheck skip paths. No network.</summary>
internal static class ServersHealthCheckTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("TestServersFileParsing:");
        TestValidJsonParses();
        TestMissingServerFieldIsNull();

        Console.WriteLine("TestServerHealthCheckSkipPaths:");
        await TestNoFileSkipsAsync();
        await TestEmptyServerSkipsAsync();
        await TestBrokenJsonSkipsAsync();
    }

    // --------------------------------------------------------------- tests

    private static void TestValidJsonParses()
    {
        var typeInfo = LauncherJsonContext.Default.GetTypeInfo(typeof(ServersFile))!;
        var parsed = (ServersFile?)JsonSerializer.Deserialize(
            "{\"server\":\"https://game.example.com\"}", typeInfo);
        TestAsserts.Equal("https://game.example.com", parsed?.Server,
            "servers.json server field is parsed");
    }

    private static void TestMissingServerFieldIsNull()
    {
        var typeInfo = LauncherJsonContext.Default.GetTypeInfo(typeof(ServersFile))!;
        var parsed = (ServersFile?)JsonSerializer.Deserialize("{}", typeInfo);
        TestAsserts.True(parsed?.Server is null, "missing server field → null");
    }

    private static async Task TestNoFileSkipsAsync()
    {
        var dir = MakeTempDir();
        try
        {
            var outcome = await ServerHealthCheck.CheckAsync(dir);
            TestAsserts.True(outcome.Skipped && outcome.Available, "no servers.json → check skipped, launch allowed");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestEmptyServerSkipsAsync()
    {
        var dir = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ServerHealthCheck.ServersFileName), "{\"server\":\"\"}");
            var outcome = await ServerHealthCheck.CheckAsync(dir);
            TestAsserts.True(outcome.Skipped && outcome.Available, "empty server → check skipped");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestBrokenJsonSkipsAsync()
    {
        var dir = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ServerHealthCheck.ServersFileName), "not json");
            var outcome = await ServerHealthCheck.CheckAsync(dir);
            TestAsserts.True(outcome.Skipped && outcome.Available, "broken servers.json → check skipped (no crash)");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------- helpers

    private static string MakeTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "gameup-launcher-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }
}
