using System.Text.Json;
using GameUpdater.Exporter;
using GameUpdater.Shared;
using GameUpdater.Shared.Models;

// Simple test runner without any NuGet packages: own Assert helpers, exit code 0/1.

internal static class Program
{
    // --------------------------------------------------------------- main

    private static async Task<int> Main()
    {
        Console.WriteLine("GameUpdater.Tests — ManifestBuilder / Shared / UpdateChecker (no NuGet)\n");

        await TestScanAndIgnorePatternsAsync();
        await TestSortingAndUnixPathsAsync();
        await TestMd5KnownVectorsAsync();
        await TestManifestJsonShapeAsync();
        TestPathNormalizer();
        await UpdateCheckerTests.RunAsync();

        Console.WriteLine($"\nResult: {TestAsserts.Passed} passed, {TestAsserts.Failures.Count} failed.");
        foreach (var failure in TestAsserts.Failures)
            Console.WriteLine($"  FAILED: {failure}");

        return TestAsserts.Failures.Count == 0 ? 0 : 1;
    }

    // --------------------------------------------------------------- tests

    private static async Task TestScanAndIgnorePatternsAsync()
    {
        Console.WriteLine("TestScanAndIgnorePatterns:");
        var root = MakeTempDir();
        try
        {
            WriteFile(root, "client.exe", "exe");
            WriteFile(root, "build.log", "log");                       // ignored by *.log
            WriteFile(root, ".DS_Store", "junk");                        // built-in ignore
            WriteFile(root, "local_version.json", "{}");                 // config pattern
            WriteFile(root, ManifestBuilder.ManifestFileName, "{}");     // auto-ignored at root
            WriteFile(root, ManifestBuilder.VersionFileName, "{}");      // auto-ignored at root
            WriteFile(root, "data/level1.pak", "pak1");
            WriteFile(root, "data/level2.pak", "pak2");
            WriteFile(root, "assets/old/discard.bin", "old");            // dir ignored by pattern
            WriteFile(root, "sub/nested/deep.txt", "deep");

            var patterns = new[] { "*.log", "local_version.json", "assets/old" };
            var manifest = await ManifestBuilder.BuildAsync(
                root, "9.9.9", patterns, progress: null, verbose: false, CancellationToken.None);

            TestAsserts.SequenceEqual(
                new[] { "client.exe", "data/level1.pak", "data/level2.pak", "sub/nested/deep.txt" },
                manifest.Files.Select(f => f.Path),
                "scan applies ignore patterns (files and directories)");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task TestSortingAndUnixPathsAsync()
    {
        Console.WriteLine("TestSortingAndUnixPaths:");
        var root = MakeTempDir();
        try
        {
            // Create in deliberately unsorted order.
            WriteFile(root, "zeta.bin", "z");
            WriteFile(root, "mid/gamma.bin", "g");
            WriteFile(root, "alpha.bin", "a");
            WriteFile(root, "mid/beta.bin", "b");

            var manifest = await ManifestBuilder.BuildAsync(
                root, "1.2.3", Array.Empty<string>(), progress: null, verbose: false, CancellationToken.None);

            TestAsserts.SequenceEqual(
                new[] { "alpha.bin", "mid/beta.bin", "mid/gamma.bin", "zeta.bin" },
                manifest.Files.Select(f => f.Path),
                "manifest entries sorted by path (ordinal)");

            TestAsserts.True(manifest.Files.All(f => !f.Path.Contains('\\')),
                "all manifest paths use forward slashes");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task TestMd5KnownVectorsAsync()
    {
        Console.WriteLine("TestMd5KnownVectors:");
        var root = MakeTempDir();
        try
        {
            var emptyPath = Path.Combine(root, "empty.bin");
            File.WriteAllBytes(emptyPath, Array.Empty<byte>());
            TestAsserts.Equal(
                "d41d8cd98f00b204e9800998ecf8427e",
                await HashCalculator.ComputeMd5Async(emptyPath),
                "MD5 of empty file");

            var abcPath = Path.Combine(root, "abc.txt");
            File.WriteAllBytes(abcPath, System.Text.Encoding.ASCII.GetBytes("abc"));
            TestAsserts.Equal(
                "900150983cd24fb0d6963f7d28e17f72",
                await HashCalculator.ComputeMd5Async(abcPath),
                "MD5 of 'abc'");

            // A larger file to exercise the streaming path.
            var bigPath = Path.Combine(root, "big.bin");
            await using (var stream = File.Create(bigPath))
            {
                var chunk = new byte[64 * 1024];
                for (var i = 0; i < 8; i++)
                    await stream.WriteAsync(chunk);
            }
            var bigHash = await HashCalculator.ComputeMd5Async(bigPath);
            TestAsserts.Equal(32, bigHash.Length, "MD5 hex length is 32");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task TestManifestJsonShapeAsync()
    {
        Console.WriteLine("TestManifestJsonShape:");
        var root = MakeTempDir();
        try
        {
            WriteFile(root, "client.exe", "exe");
            WriteFile(root, "data/level1.pak", "pak");

            var manifest = await ManifestBuilder.BuildAsync(
                root, "1.0.5", Array.Empty<string>(), progress: null, verbose: false, CancellationToken.None);

            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            using var doc = JsonDocument.Parse(json);
            var rootEl = doc.RootElement;

            TestAsserts.Equal("1.0.5", rootEl.GetProperty("version").GetString(), "json: version");
            TestAsserts.Equal("MD5", rootEl.GetProperty("algorithm").GetString(), "json: algorithm == MD5");

            var files = rootEl.GetProperty("files").EnumerateArray().ToList();
            TestAsserts.Equal(2, files.Count, "json: files count");
            foreach (var file in files)
            {
                TestAsserts.True(file.TryGetProperty("path", out _) && file.TryGetProperty("hash", out _) &&
                           file.TryGetProperty("size", out _),
                    $"json: entry has path/hash/size ({file.GetProperty("path").GetString()})");
            }

            var versionInfo = new VersionInfo { Version = "1.0.5", BuildDate = "2026-10-13T12:00:00Z" };
            using var vdoc = JsonDocument.Parse(JsonSerializer.Serialize(versionInfo));
            TestAsserts.Equal("1.0.5", vdoc.RootElement.GetProperty("version").GetString(), "json: versionInfo.version");
            TestAsserts.Equal("1.0.0", vdoc.RootElement.GetProperty("minLauncherVersion").GetString(),
                "json: versionInfo.minLauncherVersion default");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void TestPathNormalizer()
    {
        Console.WriteLine("TestPathNormalizer:");
        TestAsserts.Equal("a/b/c", PathNormalizer.ToUnix(@"a\b\c"), "ToUnix converts backslashes");
        TestAsserts.Equal("a/b/c", PathNormalizer.ToUnix("a/b/c"), "ToUnix keeps forward slashes");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        TestAsserts.True(PathNormalizer.ExpandHome("~/key").StartsWith(home), "ExpandHome expands ~ to profile dir");
    }

    // ------------------------------------------------------------- helpers

    private static string MakeTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "gameup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string root, string relativeUnixPath, string content)
    {
        var full = Path.Combine(root, relativeUnixPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
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
