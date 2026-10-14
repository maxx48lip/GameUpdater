using System.Diagnostics;
using System.Text.Json;
using GameUpdater.Shared;
using GameUpdater.Shared.Models;

namespace GameUpdater.Exporter;

/// <summary>Upload statistics returned after a successful run.</summary>
public sealed record UploadStats(TimeSpan Elapsed, int FileCount, long TotalBytes);

/// <summary>Static wiki content uploaded alongside the build (never part of the game manifest).</summary>
public sealed record WikiUpload(string RootDir, Manifest Manifest);

/// <summary>
/// Uploads the build to the server via the external sftp client.
/// Order is critical: 1) optional static wiki into wiki/ (delta), 2) game files into files/,
/// 3) manifest.json, 4) version.json (last).
/// </summary>
public sealed class SftpUploader
{
    private enum Mode { PerFile, Batch }

    /// <summary>Remote subfolder for the static wiki (served at https://&lt;host&gt;/wiki/).</summary>
    public const string WikiRemotePrefix = "wiki";

    private readonly ExporterConfig _config;
    private readonly SftpOptions _options;
    private readonly int _maxRetries;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>One set of files uploaded under a single remote prefix ("files" or "wiki").</summary>
    private sealed record FileGroup(string LocalRootDir, List<(string RelPath, long Size)> Files, string RemotePrefix);

    public SftpUploader(ExporterConfig config)
    {
        _config = config;
        _options = new SftpOptions
        {
            Host = config.SftpHost,
            Port = config.SftpPort,
            User = config.SftpUser,
            KeyPath = string.IsNullOrWhiteSpace(config.SftpKeyPath)
                ? null
                : PathNormalizer.ExpandHome(config.SftpKeyPath),
        };
        _maxRetries = Math.Max(1, config.MaxRetries);
    }

    private sealed class State
    {
        public long UploadedBytes;
        public int DoneFiles;
    }

    // ------------------------------------------------------------- upload

    /// <summary>Runs the full upload: directories → wiki/ (delta) → files/ → manifest.json → version.json.</summary>
    public async Task<UploadStats> UploadAsync(
        string rootDir, Manifest manifest, string localManifestPath, string localVersionPath,
        WikiUpload? wiki, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var state = new State();

        // --- Resolve the wiki delta: previous remote manifest vs. the local scan.
        List<FileEntry>? wikiChanged = null;
        var wikiStale = new List<string>();
        string? wikiRootDir = null;
        if (wiki is not null && wiki.Manifest.Files.Count > 0)
        {
            wikiRootDir = wiki.RootDir;
            var previous = await DownloadWikiManifestAsync(cancellationToken).ConfigureAwait(false);
            var delta = WikiDelta.Compute(wiki.Manifest, previous);
            wikiChanged = delta.ToUpload.ToList();
            wikiStale.AddRange(delta.StalePaths);

            AnsiConsole.Info(
                $"Wiki delta: {wikiChanged.Count} to upload, " +
                $"{wiki.Manifest.Files.Count - wikiChanged.Count} unchanged, {wikiStale.Count} stale.");
        }

        // Order is critical: 1) optional static wiki into <remoteBase>/wiki/ (the launcher never
        // sees it), 2) game files into files/, 3) manifest.json, 4) version.json — always LAST,
        // so the launcher never sees a new version before its manifest and files are complete.
        var groups = new List<FileGroup>();
        if (wikiChanged is { Count: > 0 } && wikiRootDir is not null)
            groups.Add(new FileGroup(
                wikiRootDir,
                wikiChanged.Select(f => (f.Path, f.Size)).ToList(),
                WikiRemotePrefix));
        groups.Add(new FileGroup(
            rootDir,
            manifest.Files.Select(f => (f.Path, f.Size)).ToList(),
            "files"));

        long totalBytes = groups.Sum(g => g.Files.Sum(x => x.Size));
        int totalFiles = groups.Sum(g => g.Files.Count);

        // 0. Remote directory tree (all ancestors of the remote paths).
        var directories = CollectRemoteDirectories(groups);
        AnsiConsole.Info($"Ensuring remote directories on {_options.Host}...");
        await SftpClientWrapper.EnsureRemoteDirectoriesAsync(_options, directories, cancellationToken)
            .ConfigureAwait(false);

        var mode = string.Equals(_config.UploadMode, "batch", StringComparison.OrdinalIgnoreCase)
            ? Mode.Batch
            : Mode.PerFile;

        // 1. Wiki files — only the changed/new ones from the delta.
        if (wikiChanged is { Count: > 0 })
            await UploadGroupAsync(groups[0], 0, totalFiles, totalBytes, state, sw, mode, cancellationToken)
                .ConfigureAwait(false);

        // 2. Commit the wiki update: remove stale files FIRST (if we fail before the new manifest,
        //    the old one still lists them → they are re-uploaded on the next run), then upload
        //    wiki_manifest.json LAST so it never claims content that is not in place yet.
        if (wiki is not null && wiki.Manifest.Files.Count > 0)
            await CommitWikiManifestAsync(wiki, wikiStale, cancellationToken).ConfigureAwait(false);

        // 3. Game files into <remoteBase>/files/.
        var gameGroup = groups[^1];
        var gameOffset = totalFiles - gameGroup.Files.Count;
        await UploadGroupAsync(gameGroup, gameOffset, totalFiles, totalBytes, state, sw, mode, cancellationToken)
            .ConfigureAwait(false);

        // 4. manifest.json — only after all game files are in place.
        AnsiConsole.Info("Uploading manifest.json...");
        await WithRetryAsync(
            () => SftpClientWrapper.UploadFileAsync(_options, localManifestPath, RemotePath("manifest.json"), cancellationToken),
            "manifest.json", cancellationToken).ConfigureAwait(false);

        // 5. version.json — always LAST (see above).
        AnsiConsole.Info("Uploading version.json...");
        await WithRetryAsync(
            () => SftpClientWrapper.UploadFileAsync(_options, localVersionPath, RemotePath("version.json"), cancellationToken),
            "version.json", cancellationToken).ConfigureAwait(false);

        sw.Stop();
        return new UploadStats(sw.Elapsed, totalFiles, totalBytes);
    }

    // ------------------------------------------------------- wiki commit

    /// <summary>Removes stale remote wiki files and uploads the fresh wiki_manifest.json.</summary>
    private async Task CommitWikiManifestAsync(WikiUpload wiki, List<string> stalePaths, CancellationToken ct)
    {
        if (stalePaths.Count > 0)
        {
            AnsiConsole.Info($"Removing {stalePaths.Count} stale wiki file(s)...");
            await SftpClientWrapper.RemoveRemoteFilesAsync(
                _options, stalePaths.Select(p => RemotePath($"{WikiRemotePrefix}/{p}")).ToList(), ct).ConfigureAwait(false);
        }

        var localWikiManifest = Path.Combine(Path.GetTempPath(), "gameup-wiki-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(localWikiManifest, JsonSerializer.Serialize(wiki.Manifest, JsonOpts), ct)
                .ConfigureAwait(false);

            AnsiConsole.Info("Uploading wiki_manifest.json...");
            await WithRetryAsync(
                () => SftpClientWrapper.UploadFileAsync(_options, localWikiManifest, RemotePath(ManifestBuilder.WikiManifestFileName), ct),
                "wiki_manifest.json", ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(localWikiManifest);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    /// <summary>
    /// Downloads the previous wiki_manifest.json from the server. Returns null when it does not exist
    /// yet (first wiki upload) or is unreadable/corrupt (falls back to a full re-upload).
    /// </summary>
    private async Task<Manifest?> DownloadWikiManifestAsync(CancellationToken ct)
    {
        var remote = RemotePath(ManifestBuilder.WikiManifestFileName);
        var local = Path.Combine(Path.GetTempPath(), "gameup-wiki-prev-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            for (var attempt = 1; attempt <= _maxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var (success, stderrTail) = await SftpClientWrapper.DownloadFileAsync(_options, remote, local, ct)
                    .ConfigureAwait(false);
                if (success)
                {
                    try
                    {
                        return JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(local, ct).ConfigureAwait(false));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        AnsiConsole.Warn($"Remote wiki_manifest.json is unreadable ({FirstLine(ex.Message)}); re-uploading the whole wiki.");
                        return null;
                    }
                }

                if (stderrTail.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                    return null; // no previous manifest — first wiki upload

                AnsiConsole.Warn($"Downloading {remote} failed (attempt {attempt}/{_maxRetries}): {FirstLine(stderrTail)}");
                await BackoffAsync(attempt, ct).ConfigureAwait(false);
            }

            throw new InvalidOperationException($"Failed to download {remote} after {_maxRetries} attempts.");
        }
        finally
        {
            try
            {
                File.Delete(local);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    // ------------------------------------------------------- per-file mode

    private async Task UploadOneFileAsync(
        FileGroup group, int index, int totalFiles, long totalBytes, State state,
        Stopwatch sw, CancellationToken ct)
    {
        var (relPath, size) = group.Files[index - 1];
        var local = Path.Combine(group.LocalRootDir, relPath);
        var remote = RemotePath($"{group.RemotePrefix}/{relPath}");

        await WithRetryAsync(
            () => SftpClientWrapper.UploadFileAsync(_options, local, remote, ct),
            $"{group.RemotePrefix}/{relPath}", ct).ConfigureAwait(false);

        state.UploadedBytes += size;
        state.DoneFiles = index;
        ReportProgress(state, index, totalFiles, totalBytes, sw, $"{group.RemotePrefix}/{relPath}");
    }

    // -------------------------------------------------------- batch mode

    /// <summary>
    /// One sftp session for all files of a group ("-b" aborts on the first failed put). If it
    /// aborts, the remaining files are re-uploaded per-file with retries.
    /// </summary>
    private async Task UploadBatchWithFallbackAsync(
        FileGroup group, int offset, int totalFiles, long totalBytes, State state, Stopwatch sw, CancellationToken ct)
    {
        var files = group.Files;
        var remotes = files.Select(f => RemotePath($"{group.RemotePrefix}/{f.RelPath}")).ToList();
        int lastStartedIndex = -1;

        void OnStdErrLine(string line)
        {
            if (!line.StartsWith("Uploading ", StringComparison.Ordinal))
                return;

            // sftp prints: "Uploading <local> to <remote>" — match by remote suffix.
            var index = -1;
            for (var i = 0; i < remotes.Count; i++)
            {
                if (line.EndsWith("to " + remotes[i], StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index <= lastStartedIndex)
                return;

            // Everything before the newly started file is done.
            for (var j = lastStartedIndex + 1; j < index; j++)
                state.UploadedBytes += files[j].Size;
            lastStartedIndex = index;
            ReportProgress(state, offset + index, totalFiles, totalBytes, sw, $"{group.RemotePrefix}/{files[index].RelPath}");
        }

        var pairs = new List<(string LocalPath, string Remote)>(files.Count);
        for (var i = 0; i < files.Count; i++)
            pairs.Add((Path.Combine(group.LocalRootDir, files[i].RelPath), remotes[i]));

        var result = await SftpClientWrapper.UploadBatchAsync(_options, pairs, OnStdErrLine, ct).ConfigureAwait(false);

        if (result.Success)
        {
            for (var j = lastStartedIndex + 1; j < files.Count; j++)
                state.UploadedBytes += files[j].Size;
            state.DoneFiles = offset + files.Count;
            ReportProgress(state, offset + files.Count, totalFiles, totalBytes, sw, $"{group.RemotePrefix}/ complete");
            return;
        }

        if (lastStartedIndex >= 0)
            AnsiConsole.Warn(
                $"Batch upload aborted at file #{offset + lastStartedIndex + 1}/{totalFiles} " +
                $"({group.RemotePrefix}/{files[lastStartedIndex].RelPath}); resuming per-file with retries.");
        else
            AnsiConsole.Warn(
                $"Batch upload failed before any file started ({FirstLine(result.StdErrTail)}); retrying per-file.");

        for (var i = Math.Max(0, lastStartedIndex); i < files.Count; i++)
            await UploadOneFileAsync(group, offset + i + 1, totalFiles, totalBytes, state, sw, ct)
                .ConfigureAwait(false);
    }

    // ------------------------------------------------------------- groups

    private async Task UploadGroupAsync(
        FileGroup group, int offset, int totalFiles, long totalBytes, State state, Stopwatch sw, Mode mode, CancellationToken ct)
    {
        if (group.Files.Count == 0)
            return;

        AnsiConsole.Info($"Uploading {group.RemotePrefix}/ ({group.Files.Count} files)...");
        if (mode == Mode.Batch)
            await UploadBatchWithFallbackAsync(group, offset, totalFiles, totalBytes, state, sw, ct).ConfigureAwait(false);
        else
            for (var i = 0; i < group.Files.Count; i++)
                await UploadOneFileAsync(group, offset + i + 1, totalFiles, totalBytes, state, sw, ct)
                    .ConfigureAwait(false);
    }

    // -------------------------------------------------------------- retry

    private async Task WithRetryAsync(
        Func<Task<(bool Success, string StdErrTail)>> action, string what, CancellationToken ct)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var (success, stderrTail) = await action().ConfigureAwait(false);
                if (success)
                    return;
                AnsiConsole.Warn($"Upload of {what} failed (attempt {attempt}/{_maxRetries}): {FirstLine(stderrTail)}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                AnsiConsole.Warn($"Upload of {what} failed (attempt {attempt}/{_maxRetries}): {ex.Message}");
            }

            await BackoffAsync(attempt, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Failed to upload {what} after {_maxRetries} attempts.", lastError);
    }

    /// <summary>Exponential backoff between retries (2s, 4s, 8s… capped at 30s); no-op on the last attempt.</summary>
    private async Task BackoffAsync(int attempt, CancellationToken ct)
    {
        if (attempt >= _maxRetries)
            return;

        var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))); // 2s, 4s, 8s...
        AnsiConsole.Info($"Retrying in {delay.TotalSeconds:F0}s...");
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- helpers

    private string RemotePath(string relativeUnix) =>
        _config.RemoteBasePath.TrimEnd('/') + "/" + relativeUnix;

    /// <summary>All unique ancestor directories of the remote paths, shallowest first.</summary>
    private List<string> CollectRemoteDirectories(List<FileGroup> groups)
    {
        var baseDir = _config.RemoteBasePath.TrimEnd('/');
        var dirs = new SortedSet<string>(StringComparer.Ordinal);

        void AddAncestors(string path)
        {
            var current = string.Empty;
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.Length == 0 ? "/" + part : current + "/" + part;
                dirs.Add(current);
            }
        }

        AddAncestors(baseDir); // base dir holds manifest.json / version.json / wiki_manifest.json
        foreach (var group in groups)
            foreach (var (relPath, _) in group.Files)
                AddAncestors(ParentDirectory(RemotePath($"{group.RemotePrefix}/{relPath}")));

        return dirs.OrderBy(d => d.Count(c => c == '/')).ThenBy(d => d, StringComparer.Ordinal).ToList();
    }

    private static string ParentDirectory(string unixPath)
    {
        var slash = unixPath.LastIndexOf('/');
        return slash <= 0 ? "/" : unixPath[..slash];
    }

    private void ReportProgress(State state, int doneFiles, int totalFiles, long totalBytes, Stopwatch sw, string label)
    {
        var percent = (int)(100.0 * state.UploadedBytes / Math.Max(1L, totalBytes));
        AnsiConsole.Progress(percent, $"{doneFiles}/{totalFiles} | {FormatSpeed(state, sw)} | {label}");
    }

    private static string FormatSpeed(State state, Stopwatch sw)
    {
        var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        return $"{ByteFormat.Format((long)(state.UploadedBytes / seconds))}/s";
    }

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return "unknown error";
    }
}
