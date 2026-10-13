using System.Diagnostics;
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

        if (!Directory.Exists(options.Path))
            throw new DirectoryNotFoundException($"Build folder not found: {options.Path}");
        var rootDir = Path.GetFullPath(options.Path);

        // Config is required for upload; optional otherwise (dry-run works without it).
        bool willUpload = !options.DryRun && !options.SkipUpload;
        ExporterConfig? config = null;
        if (willUpload || options.ConfigPath is not null)
        {
            var configPath = options.ConfigPath ?? "exporter.json";
            if (!File.Exists(configPath))
                throw new FileNotFoundException(
                    $"Config file not found: {configPath} (pass --config <file> or create exporter.json).");
            config = ExporterConfig.Load(configPath);
        }

        if (willUpload && config is not null)
        {
            config.ValidateForUpload();
            // Fail fast: the sftp client must exist before we spend time hashing.
            await SftpClientWrapper.FindSftpAsync(ct).ConfigureAwait(false);
            if (SftpClientWrapper.SftpVersion is { Length: > 0 } version)
                AnsiConsole.Info($"sftp client: {version}");
        }

        // ------------------------------------------------ scan + hash
        AnsiConsole.Info($"Scanning {options.Path}...");
        var swScan = Stopwatch.StartNew();

        var ignorePatterns = new List<string>(ManifestBuilder.BuiltInIgnorePatterns);
        if (config is not null)
            ignorePatterns.AddRange(config.IgnorePatterns);

        var progress = new Progress<(int Done, int Total, long BytesDone)>(p =>
            AnsiConsole.Progress(p.Done * 100 / Math.Max(1, p.Total), $"{p.Done}/{p.Total} | scanning"));

        var manifest = await ManifestBuilder.BuildAsync(rootDir, options.Version, ignorePatterns, progress, options.Verbose, ct)
            .ConfigureAwait(false);
        swScan.Stop();
        AnsiConsole.EndProgress();

        long totalBytes = manifest.Files.Sum(f => f.Size);
        AnsiConsole.Info($"Found {manifest.Files.Count} files ({ByteFormat.Format(totalBytes)})");
        AnsiConsole.Info($"Manifest generated: {manifest.Files.Count} entries");

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

            var stats = await uploader.UploadAsync(rootDir, manifest, localManifest, localVersion, ct)
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
