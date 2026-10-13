using GameUpdater.Launcher;
using GameUpdater.Shared;
using GameUpdater.Shared.Models;

// No namespace: matches Program.cs in this project (global namespace).

/// <summary>Launcher differential check (UpdateChecker): no network, no NuGet.</summary>
internal static class UpdateCheckerTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("TestUpdateDiff:");
        await TestMissingFileIsDownloadedAsync();
        await TestSizeMismatchIsDownloadedAsync();
        await TestHashMismatchIsDownloadedAsync();
        await TestMatchingFileIsSkippedAsync();

        Console.WriteLine("TestDeleteList:");
        await TestObsoleteFilesAreDeletedAsync();
        await TestExcludeFromDeleteAsync();

        Console.WriteLine("TestLocalVersion:");
        await TestLocalVersionRoundTripAsync();
    }

    // --------------------------------------------------------------- tests

    private static async Task TestMissingFileIsDownloadedAsync()
    {
        var root = MakeTempDir();
        try
        {
            WriteFile(root, "a.bin", "hello");
            var manifest = new Manifest { Version = "1.0.0", Files = { await EntryFor(root, "a.bin") } };

            // Empty game folder: the file is missing locally → must be downloaded.
            var emptyGame = MakeTempDir();
            try
            {
                var plan = await UpdateChecker.ComputeDiffAsync(manifest, emptyGame);
                TestAsserts.SequenceEqual(new[] { "a.bin" }, plan.ToDownload.Select(f => f.Path),
                    "missing file goes to download");
                TestAsserts.Equal(0, plan.ToDelete.Count, "nothing to delete in an empty folder");
            }
            finally
            {
                Cleanup(emptyGame);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task TestSizeMismatchIsDownloadedAsync()
    {
        var root = MakeTempDir();
        try
        {
            WriteFile(root, "b.bin", "ab"); // 2 bytes locally
            var entry = await EntryFor(root, "b.bin");
            entry.Size += 1; // manifest claims a different size (hash is irrelevant then)

            var plan = await UpdateChecker.ComputeDiffAsync(new Manifest { Files = { entry } }, root);
            TestAsserts.SequenceEqual(new[] { "b.bin" }, plan.ToDownload.Select(f => f.Path),
                "size mismatch goes to download");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task TestHashMismatchIsDownloadedAsync()
    {
        var root = MakeTempDir();
        try
        {
            WriteFile(root, "c.bin", "xx"); // same size as the manifest content...
            var entry = await EntryFor(root, "c.bin");

            // ...but different content → MD5 mismatch.
            WriteFile(root, "other", "yy");
            entry.Hash = (await HashCalculator.ComputeMd5Async(Path.Combine(root, "other"))).ToLowerInvariant();

            var plan = await UpdateChecker.ComputeDiffAsync(new Manifest { Files = { entry } }, root);
            TestAsserts.SequenceEqual(new[] { "c.bin" }, plan.ToDownload.Select(f => f.Path),
                "hash mismatch (same size) goes to download");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task TestMatchingFileIsSkippedAsync()
    {
        var root = MakeTempDir();
        try
        {
            WriteFile(root, "d.bin", "identical content");
            var manifest = new Manifest { Version = "1.0.0", Files = { await EntryFor(root, "d.bin") } };

            var plan = await UpdateChecker.ComputeDiffAsync(manifest, root);
            TestAsserts.Equal(0, plan.ToDownload.Count, "matching file (size + MD5) is not downloaded");
            TestAsserts.Equal(0, plan.ToDelete.Count, "manifest files are never deleted");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task TestObsoleteFilesAreDeletedAsync()
    {
        var root = MakeTempDir();
        try
        {
            WriteFile(root, "keep.txt", "in manifest");
            WriteFile(root, "extra1.txt", "obsolete");
            WriteFile(root, "sub/extra2.txt", "obsolete nested");

            var manifest = new Manifest { Files = { await EntryFor(root, "keep.txt") } };
            var plan = await UpdateChecker.ComputeDiffAsync(manifest, root);

            TestAsserts.SequenceEqual(new[] { "extra1.txt", "sub/extra2.txt" },
                plan.ToDelete.OrderBy(p => p), "local files absent from the manifest go to delete");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task TestExcludeFromDeleteAsync()
    {
        var root = MakeTempDir();
        try
        {
            WriteFile(root, UpdateChecker.LocalVersionFileName, "{}"); // always excluded
            WriteFile(root, "user.cfg", "save");                       // exact name
            WriteFile(root, "logs/a.log", "log1");                     // direct child of logs/
            WriteFile(root, "logs/sub/b.log", "log2");                 // nested under logs/
            WriteFile(root, "data/deep/x.bin", "d");                   // covered by data/**
            WriteFile(root, "junk.txt", "delete me");

            var manifest = new Manifest(); // empty: everything local is obsolete unless excluded
            var excludes = new[] { "user.cfg", "logs/*", "data/**" };
            var plan = await UpdateChecker.ComputeDiffAsync(manifest, root, excludes);
            var deleted = plan.ToDelete.OrderBy(p => p).ToList();

            TestAsserts.True(!deleted.Contains(UpdateChecker.LocalVersionFileName),
                "local_version.json is never deleted");
            TestAsserts.True(!deleted.Contains("user.cfg"), "exact-name exclusion works");
            TestAsserts.True(!deleted.Contains("logs/a.log"), "'logs/*' excludes direct children");
            TestAsserts.True(deleted.Contains("logs/sub/b.log"), "'logs/*' does not cover nested files");
            TestAsserts.True(!deleted.Contains("data/deep/x.bin"), "'data/**' covers the whole subtree");
            TestAsserts.SequenceEqual(new[] { "junk.txt", "logs/sub/b.log" }, deleted,
                "only non-excluded obsolete files remain in the delete list");

            // A plain directory name excludes its entire subtree.
            var plan2 = await UpdateChecker.ComputeDiffAsync(manifest, root, new[] { "user.cfg", "logs", "data/**" });
            TestAsserts.True(!plan2.ToDelete.Contains("logs/sub/b.log"),
                "plain dir name 'logs' excludes the whole subtree");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task TestLocalVersionRoundTripAsync()
    {
        var root = MakeTempDir();
        try
        {
            TestAsserts.Equal(null, UpdateChecker.ReadLocalVersion(root), "no file → null version");

            UpdateChecker.WriteLocalVersion(root, "1.2.3");
            TestAsserts.Equal("1.2.3", UpdateChecker.ReadLocalVersion(root), "write/read round trip");
            TestAsserts.True(!File.Exists(Path.Combine(root, UpdateChecker.LocalVersionFileName + ".tmp")),
                "no .tmp left after write");

            File.WriteAllText(Path.Combine(root, UpdateChecker.LocalVersionFileName), "{corrupt json");
            TestAsserts.Equal(null, UpdateChecker.ReadLocalVersion(root), "corrupt file → null (full update)");

            TestAsserts.True(UpdateChecker.IsUpToDate("1.0.5", "1.0.5"), "equal versions → up to date");
            TestAsserts.True(!UpdateChecker.IsUpToDate("1.0.4", "1.0.5"), "different versions → not up to date");
            TestAsserts.True(!UpdateChecker.IsUpToDate(null, "1.0.5"), "no local version → not up to date");

            var threw = false;
            try
            {
                UpdateChecker.ValidateRelativePath("../evil");
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            TestAsserts.True(threw, "manifest path with '..' is rejected");
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ------------------------------------------------------------- helpers

    private static async Task<FileEntry> EntryFor(string root, string relativeUnixPath)
    {
        var full = Path.Combine(root, relativeUnixPath.Replace('/', Path.DirectorySeparatorChar));
        return new FileEntry
        {
            Path = relativeUnixPath,
            Hash = await HashCalculator.ComputeMd5Async(full),
            Size = new FileInfo(full).Length,
        };
    }

    private static string MakeTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "gameup-launcher-tests-" + Guid.NewGuid().ToString("N"));
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
