using System.Diagnostics;
using GameUpdater.Shared;
using GameUpdater.Shared.Models;

namespace GameUpdater.Exporter;

/// <summary>Upload statistics returned after a successful run.</summary>
public sealed record UploadStats(TimeSpan Elapsed, int FileCount, long TotalBytes);

/// <summary>
/// Uploads the build to the server via the external sftp client.
/// Order is critical: 1) game files into files/, 2) manifest.json, 3) version.json (last).
/// </summary>
public sealed class SftpUploader
{
    private enum Mode { PerFile, Batch }

    private readonly ExporterConfig _config;
    private readonly SftpOptions _options;
    private readonly int _maxRetries;

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

    /// <summary>Runs the full upload: directories → files → manifest.json → version.json.</summary>
    public async Task<UploadStats> UploadAsync(
        string rootDir, Manifest manifest, string localManifestPath, string localVersionPath,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var state = new State();
        var files = manifest.Files;
        long totalBytes = files.Sum(f => f.Size);

        // 0. Remote directory tree (all ancestors of the remote paths).
        var directories = CollectRemoteDirectories(files);
        AnsiConsole.Info($"Ensuring remote directories on {_options.Host}...");
        await SftpClientWrapper.EnsureRemoteDirectoriesAsync(_options, directories, cancellationToken)
            .ConfigureAwait(false);

        // 1. Game files into <remoteBase>/files/.
        var mode = string.Equals(_config.UploadMode, "batch", StringComparison.OrdinalIgnoreCase)
            ? Mode.Batch
            : Mode.PerFile;

        if (mode == Mode.Batch)
            await UploadBatchWithFallbackAsync(rootDir, files, totalBytes, state, sw, cancellationToken).ConfigureAwait(false);
        else
            for (var i = 0; i < files.Count; i++)
                await UploadOneFileAsync(rootDir, files[i], i + 1, files.Count, totalBytes, state, sw, cancellationToken)
                    .ConfigureAwait(false);

        // 2. manifest.json — only after all game files are in place.
        AnsiConsole.Info("Uploading manifest.json...");
        await WithRetryAsync(
            () => SftpClientWrapper.UploadFileAsync(_options, localManifestPath, RemotePath("manifest.json"), cancellationToken),
            "manifest.json", cancellationToken).ConfigureAwait(false);

        // 3. version.json — always LAST, so the launcher never sees a new version
        //    before its manifest and files are complete.
        AnsiConsole.Info("Uploading version.json...");
        await WithRetryAsync(
            () => SftpClientWrapper.UploadFileAsync(_options, localVersionPath, RemotePath("version.json"), cancellationToken),
            "version.json", cancellationToken).ConfigureAwait(false);

        sw.Stop();
        return new UploadStats(sw.Elapsed, files.Count, totalBytes);
    }

    // ------------------------------------------------------- per-file mode

    private async Task UploadOneFileAsync(
        string rootDir, FileEntry file, int index, int total, long totalBytes, State state,
        Stopwatch sw, CancellationToken ct)
    {
        var local = Path.Combine(rootDir, file.Path);
        var remote = RemotePath("files/" + file.Path);

        await WithRetryAsync(
            () => SftpClientWrapper.UploadFileAsync(_options, local, remote, ct),
            $"files/{file.Path}", ct).ConfigureAwait(false);

        state.UploadedBytes += file.Size;
        state.DoneFiles = index;
        ReportProgress(state, index, total, totalBytes, sw, $"files/{file.Path}");
    }

    // -------------------------------------------------------- batch mode

    /// <summary>
    /// One sftp session for all files ("-b" aborts on the first failed put). If it aborts,
    /// the remaining files are re-uploaded per-file with retries.
    /// </summary>
    private async Task UploadBatchWithFallbackAsync(
        string rootDir, List<FileEntry> files, long totalBytes, State state, Stopwatch sw, CancellationToken ct)
    {
        var remotes = files.Select(f => RemotePath("files/" + f.Path)).ToList();
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
            ReportProgress(state, index, files.Count, totalBytes, sw, $"files/{files[index].Path}");
        }

        var pairs = new List<(string LocalPath, string Remote)>(files.Count);
        for (var i = 0; i < files.Count; i++)
            pairs.Add((Path.Combine(rootDir, files[i].Path), remotes[i]));

        var result = await SftpClientWrapper.UploadBatchAsync(_options, pairs, OnStdErrLine, ct).ConfigureAwait(false);

        if (result.Success)
        {
            for (var j = lastStartedIndex + 1; j < files.Count; j++)
                state.UploadedBytes += files[j].Size;
            state.DoneFiles = files.Count;
            ReportProgress(state, files.Count, files.Count, totalBytes, sw, "files complete");
            return;
        }

        if (lastStartedIndex >= 0)
            AnsiConsole.Warn(
                $"Batch upload aborted at file #{lastStartedIndex + 1}/{files.Count} " +
                $"(files/{files[lastStartedIndex].Path}); resuming per-file with retries.");
        else
            AnsiConsole.Warn(
                $"Batch upload failed before any file started ({FirstLine(result.StdErrTail)}); retrying per-file.");

        for (var i = Math.Max(0, lastStartedIndex); i < files.Count; i++)
            await UploadOneFileAsync(rootDir, files[i], i + 1, files.Count, totalBytes, state, sw, ct)
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

            if (attempt < _maxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))); // 2s, 4s, 8s...
                AnsiConsole.Info($"Retrying in {delay.TotalSeconds:F0}s...");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"Failed to upload {what} after {_maxRetries} attempts.", lastError);
    }

    // ------------------------------------------------------------- helpers

    private string RemotePath(string relativeUnix) =>
        _config.RemoteBasePath.TrimEnd('/') + "/" + relativeUnix;

    /// <summary>All unique ancestor directories of the remote paths, shallowest first.</summary>
    private List<string> CollectRemoteDirectories(List<FileEntry> files)
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

        AddAncestors(baseDir); // base dir holds manifest.json / version.json
        foreach (var file in files)
            AddAncestors(ParentDirectory(RemotePath("files/" + file.Path)));

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
