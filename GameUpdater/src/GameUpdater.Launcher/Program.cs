using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using GameUpdater.Shared;
using GameUpdater.Shared.Models;

namespace GameUpdater.Launcher;

internal static class Program
{
    private const string AppVersion = "1.0.0";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Mutable state shared between Main and RunAsync (async methods cannot use ref parameters).</summary>
    private sealed class RunContext
    {
        public string? GameFolder;
    }

    private static async Task<int> Main(string[] args)
    {
        // Consistent output regardless of host locale (decimal point, etc.).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let in-flight work observe the token and clean up .tmp files
            cts.Cancel();
        };

        var context = new RunContext();
        try
        {
            return await RunAsync(args, cts.Token, context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.EndProgress();
            CleanupTmpFiles(context.GameFolder);
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

    private static async Task<int> RunAsync(string[] args, CancellationToken ct, RunContext context)
    {
        var options = CliOptions.Parse(args);
        if (options.NoColor)
            AnsiConsole.DisableColor();

        if (options.ShowHelp)
        {
            CliOptions.PrintUsage();
            return 0;
        }

        AnsiConsole.Info($"Launcher v{AppVersion}");

        // ------------------------------------------------- config
        var configPath = options.ConfigPath ?? Path.Combine(AppContext.BaseDirectory, "launcher.json");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                $"Config file not found: {configPath} (pass --config <file> or create launcher.json next to the executable).");
        }

        var config = LauncherConfig.Load(configPath);
        var gameFolder = config.ResolvedGameFolder;
        context.GameFolder = gameFolder;
        Directory.CreateDirectory(gameFolder); // fresh install: the folder may not exist yet

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // ------------------------------------------------- local version
        var localVersion = UpdateChecker.ReadLocalVersion(gameFolder);
        AnsiConsole.Info(localVersion is null ? "Local version: none" : $"Local version: {localVersion}");

        // ------------------------------------------------- remote version
        var versionInfo = await FetchJsonAsync<VersionInfo>(http, config.ServerUrl + "/version.json", ct)
            .ConfigureAwait(false);
        AnsiConsole.Info($"Remote version: {versionInfo.Version}");

        if (IsLauncherTooOld(AppVersion, versionInfo.MinLauncherVersion))
        {
            throw new InvalidOperationException(
                $"Launcher v{AppVersion} is older than the required v{versionInfo.MinLauncherVersion}. Update the launcher.");
        }

        // ------------------------------------------------- versions match → launch
        if (UpdateChecker.IsUpToDate(localVersion, versionInfo.Version))
        {
            AnsiConsole.Info("Versions match — no update needed.");
            return LaunchGame(config, gameFolder, options.NoLaunch);
        }

        AnsiConsole.Info("Update required.");

        // ------------------------------------------------- manifest + diff
        AnsiConsole.Info("Fetching manifest...");
        var manifest = await FetchJsonAsync<Manifest>(http, config.ServerUrl + "/manifest.json", ct)
            .ConfigureAwait(false);

        if (!string.Equals(manifest.Version, versionInfo.Version, StringComparison.Ordinal))
        {
            AnsiConsole.Warn(
                $"Manifest version {manifest.Version} != version.json {versionInfo.Version}; using the manifest as-is.");
        }

        var plan = await UpdateChecker.ComputeDiffAsync(manifest, gameFolder, config.ExcludeFromDelete, ct)
            .ConfigureAwait(false);
        AnsiConsole.Info(
            $"{plan.ToDownload.Count} files to download ({ByteFormat.Format(plan.TotalBytesToDownload)}), " +
            $"{plan.ToDelete.Count} files to delete");

        // ------------------------------------------------- download
        if (plan.ToDownload.Count > 0)
        {
            var downloader = new FileDownloader(http, config.ServerUrl);
            var stats = await downloader.DownloadAllAsync(plan.ToDownload, gameFolder, ct).ConfigureAwait(false);
            AnsiConsole.EndProgress();
            AnsiConsole.Ok($"Download complete in {stats.Elapsed.TotalSeconds:F1}s");
        }

        // ------------------------------------------------- delete obsolete files
        if (plan.ToDelete.Count > 0)
        {
            var deleted = 0;
            foreach (var rel in plan.ToDelete)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    File.Delete(Path.Combine(gameFolder, rel.Replace('/', Path.DirectorySeparatorChar)));
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AnsiConsole.Warn($"Cannot delete {rel}: {ex.Message}");
                }
            }

            AnsiConsole.Ok($"Deleted {deleted} obsolete files.");
        }

        // ------------------------------------------------- local version + launch
        UpdateChecker.WriteLocalVersion(gameFolder, versionInfo.Version);
        AnsiConsole.Ok($"Local version updated to {versionInfo.Version}");

        return LaunchGame(config, gameFolder, options.NoLaunch);
    }

    private static int LaunchGame(LauncherConfig config, string gameFolder, bool noLaunch)
    {
        if (noLaunch)
        {
            AnsiConsole.Info("--no-launch: skipping game start.");
            return 0;
        }

        AnsiConsole.Info("Launching game...");
        try
        {
            GameLauncher.Start(gameFolder, config.Executable);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException or Win32Exception)
        {
            throw new InvalidOperationException($"Cannot launch the game: {ex.Message}", ex);
        }

        return 0;
    }

    /// <summary>GETs a JSON document with a "Retry? [Y/n]" prompt when the server is unreachable.</summary>
    private static async Task<T> FetchJsonAsync<T>(HttpClient http, string url, CancellationToken ct) where T : class
    {
        while (true)
        {
            try
            {
                var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
                try
                {
                    return JsonSerializer.Deserialize<T>(json, JsonOpts)
                        ?? throw new InvalidOperationException($"Empty response from {url}.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException(
                        $"Invalid JSON in {Path.GetFileName(url)}: {ex.Message}", ex);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Ctrl+C — do not prompt
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or SocketException)
            {
                AnsiConsole.Error("Cannot reach server. Retry? [Y/n]");
                var answer = Console.ReadLine();
                if (answer is null || answer.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Cannot reach server: {url}", ex);
                // otherwise — retry
            }
        }
    }

    private static bool IsLauncherTooOld(string appVersion, string minVersion) =>
        Version.TryParse(appVersion, out var app) && Version.TryParse(minVersion, out var min) && app < min;

    /// <summary>Removes leftover .tmp files after Ctrl+C (best effort).</summary>
    private static void CleanupTmpFiles(string? gameFolder)
    {
        if (gameFolder is null || !Directory.Exists(gameFolder))
            return;

        try
        {
            foreach (var tmp in Directory.EnumerateFiles(gameFolder, "*.tmp", SearchOption.AllDirectories))
                File.Delete(tmp);
        }
        catch
        {
            // best effort cleanup
        }
    }
}
