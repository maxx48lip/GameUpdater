using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GameUpdater.Exporter;
using GameUpdater.Shared;
using GameUpdater.Shared.Models;

namespace GameUpdater.Exporter;

internal static class Program
{
    private const string AppVersion = "1.0.0";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private static async Task<int> Main(string[] args)
    {
        // Consistent output regardless of host locale (decimal point, etc.).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return await RunAsync(args, cts.Token);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.EndProgress();
            AnsiConsole.Warn("Cancelled.");
            return 130;
        }
        catch (UsageException ex)
        {
            AnsiConsole.Error(ex.Message);
            Console.WriteLine();
            CliOptions.PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            AnsiConsole.EndProgress();
            AnsiConsole.Error(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var options = CliOptions.Parse(args);
        if (options.NoColor)
            AnsiConsole.DisableColor();

        if (options.ShowHelp)
        {
            CliOptions.PrintUsage();
            return 0;
        }

        AnsiConsole.Info($"Exporter v{AppVersion}");
        AnsiConsole.Info($"Version: {options.Version}");

        // Config is required for upload; optional otherwise (dry-run works without it), except
        // when the build folder must be taken from "buildFolder" in the config.
        bool willUpload = !options.DryRun && !options.SkipUpload;
        ExporterConfig? config = null;
        if (willUpload || options.ConfigPath is not null || string.IsNullOrWhiteSpace(options.Path))
        {
            var configPath = options.ConfigPath ?? "exporter.json";
            if (!File.Exists(configPath))
                throw new FileNotFoundException(
                    $"Config file not found: {configPath} (pass --config <file> or create exporter.json).");
            config = ExporterConfig.Load(configPath);
        }

        // Build folder: CLI --path wins, then "buildFolder" from the config.
        var buildPath = options.Path;
        if (string.IsNullOrWhiteSpace(buildPath))
        {
            if (config?.BuildFolder is { Length: > 0 } configured)
                buildPath = configured;
            else
                throw new UsageException(
                    "Build folder not specified: pass --path <dir> or set \"buildFolder\" in the config.");
        }

        if (!Directory.Exists(buildPath))
            throw new DirectoryNotFoundException($"Build folder not found: {buildPath}");
        var rootDir = Path.GetFullPath(buildPath);

        if (willUpload && config is not null)
        {
            config.ValidateForUpload();
            // Fail fast: the sftp client must exist before we spend time hashing.
            await SftpClientWrapper.FindSftpAsync(ct).ConfigureAwait(false);
            if (SftpClientWrapper.SftpVersion is { Length: > 0 } version)
                AnsiConsole.Info($"sftp client: {version}");
        }

        // ------------------------------------------------ scan + hash
        AnsiConsole.Info($"Scanning {buildPath}...");
        var swScan = Stopwatch.StartNew();

        // Built-in OS junk patterns are applied by ManifestBuilder automatically.
        var ignorePatterns = new List<string>(config?.IgnorePatterns ?? Enumerable.Empty<string>());

        var progress = new Progress<(int Done, int Total, long BytesDone)>(p =>
            AnsiConsole.Progress(p.Done * 100 / Math.Max(1, p.Total), $"{p.Done}/{p.Total} | scanning"));

        var manifest = await ManifestBuilder.BuildAsync(rootDir, options.Version, ignorePatterns, progress, options.Verbose, ct)
            .ConfigureAwait(false);
        swScan.Stop();
        AnsiConsole.EndProgress();

        long totalBytes = manifest.Files.Sum(f => f.Size);
        AnsiConsole.Info($"Found {manifest.Files.Count} files ({ByteFormat.Format(totalBytes)})");
        AnsiConsole.Info($"Manifest generated: {manifest.Files.Count} entries");

        // Optional static wiki (config "wikiFolder"): only touched with --update-wiki. It never
        // enters manifest.json/version.json — the launcher does not need it; what changed is tracked
        // by a separate wiki_manifest.json on the server (delta upload). Served at /<prefix>/.
        WikiUpload? wiki = null;
        if (options.UpdateWiki)
        {
            if (config?.WikiFolder is not { Length: > 0 } wikiPath)
                throw new UsageException("--update-wiki requires \"wikiFolder\" in the config file.");

            if (!Directory.Exists(wikiPath))
                throw new DirectoryNotFoundException($"Wiki folder not found: {wikiPath}");

            var wikiRootDir = Path.GetFullPath(wikiPath);

            // A nested wiki would end up in the manifest (as game files) AND in /wiki/.
            if (wikiRootDir == rootDir
                || wikiRootDir.StartsWith(rootDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rootDir.StartsWith(wikiRootDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Wiki folder and build folder must not overlap: {wikiRootDir} vs {rootDir}.");

            AnsiConsole.Info($"Scanning + hashing wiki {wikiPath}...");
            var wikiProgress = new Progress<(int Done, int Total, long BytesDone)>(p =>
                AnsiConsole.Progress(p.Done * 100 / Math.Max(1, p.Total), $"{p.Done}/{p.Total} | scanning wiki"));
            var wikiManifest = await ManifestBuilder.BuildAsync(
                    wikiRootDir, options.Version, config.WikiIgnorePatterns ?? Enumerable.Empty<string>(),
                    wikiProgress, options.Verbose, ct)
                .ConfigureAwait(false);
            AnsiConsole.EndProgress();

            long wikiBytes = wikiManifest.Files.Sum(f => f.Size);
            AnsiConsole.Info(
                $"Wiki: {wikiManifest.Files.Count} files ({ByteFormat.Format(wikiBytes)}) → " +
                $"{config.RemoteBasePath.TrimEnd('/')}/{SftpUploader.WikiRemotePrefix}/ (delta upload)");

            if (wikiManifest.Files.Count > 0)
                wiki = new WikiUpload(wikiRootDir, wikiManifest);
        }
        else if (config?.WikiFolder is { Length: > 0 })
        {
            AnsiConsole.Info("Wiki not updated (no --update-wiki).");
        }

        var versionInfo = new VersionInfo
        {
            Version = options.Version,
            BuildDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'"),
            MinLauncherVersion = config?.MinLauncherVersion ?? "1.0.0",
        };

        string manifestJson = JsonSerializer.Serialize(manifest, JsonOpts);
        string versionJson = JsonSerializer.Serialize(versionInfo, JsonOpts);

        // ------------------------------------------- dry-run / skip-upload
        if (!willUpload)
        {
            var localManifest = Path.Combine(rootDir, ManifestBuilder.ManifestFileName);
            var localVersion = Path.Combine(rootDir, ManifestBuilder.VersionFileName);
            await File.WriteAllTextAsync(localManifest, manifestJson, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(localVersion, versionJson, ct).ConfigureAwait(false);

            AnsiConsole.Ok($"Wrote {localManifest}");
            AnsiConsole.Ok($"Wrote {localVersion}");

            if (wiki is not null)
            {
                var localWikiManifest = Path.Combine(rootDir, ManifestBuilder.WikiManifestFileName);
                await File.WriteAllTextAsync(
                    localWikiManifest, JsonSerializer.Serialize(wiki.Manifest, JsonOpts), ct).ConfigureAwait(false);
                AnsiConsole.Ok($"Wrote {localWikiManifest}");
            }

            if (options.DryRun)
                AnsiConsole.Info("Dry run: no upload performed.");
            PrintStats(manifest.Files.Count, totalBytes, swScan.Elapsed);
            return 0;
        }

        // -------------------------------------------------------- upload
        var uploader = new SftpUploader(config!);
        AnsiConsole.Info($"Uploading to {config!.SftpHost}:{config.SftpPort} as {config.SftpUser}...");

        string tmpDir = Path.Combine(Path.GetTempPath(), "gameup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var localManifest = Path.Combine(tmpDir, ManifestBuilder.ManifestFileName);
            var localVersion = Path.Combine(tmpDir, ManifestBuilder.VersionFileName);
            await File.WriteAllTextAsync(localManifest, manifestJson, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(localVersion, versionJson, ct).ConfigureAwait(false);

            var stats = await uploader.UploadAsync(rootDir, manifest, localManifest, localVersion, wiki, ct)
                .ConfigureAwait(false);

            AnsiConsole.EndProgress();
            AnsiConsole.Ok($"Upload complete in {stats.Elapsed.TotalSeconds:F1}s");
            AnsiConsole.Ok($"version.json updated to {options.Version}");
            PrintStats(stats.FileCount, stats.TotalBytes, swScan.Elapsed + stats.Elapsed);
        }
        finally
        {
            try
            {
                Directory.Delete(tmpDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        return 0;
    }

    private static void PrintStats(int fileCount, long totalBytes, TimeSpan elapsed)
    {
        AnsiConsole.Info(
            $"Total: {fileCount} files, {ByteFormat.Format(totalBytes)}, " +
            $"elapsed {elapsed.TotalSeconds:F1}s");
    }
}
