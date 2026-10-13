using System.Diagnostics;
using System.Net.Sockets;
using GameUpdater.Shared;
using GameUpdater.Shared.Models;

namespace GameUpdater.Launcher;

/// <summary>Statistics of a completed download batch.</summary>
public sealed class DownloadStats
{
    public int FilesDownloaded { get; init; }
    public long TotalBytes { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>Thrown when a file cannot be downloaded after all retries.</summary>
public sealed class DownloadFailedException : Exception
{
    public string FilePath { get; }

    public DownloadFailedException(string path, Exception? inner)
        : base($"Failed to download {path}.", inner) => FilePath = path;
}

/// <summary>
/// Parallel file downloader (3 threads via SemaphoreSlim): downloads into a .tmp file with 80 KB buffers,
/// verifies the MD5 hash and atomically moves the result over the target. Up to 3 attempts per file.
/// </summary>
public sealed class FileDownloader : IDisposable
{
    private const int MaxParallelism = 3;     // spec: 3–4 threads
    private const int BufferSize = 80 * 1024; // spec: 80 KB buffers
    private const int MaxAttempts = 3;        // spec: retry up to 3 times

    private readonly HttpClient _http;
    private readonly string _serverUrl;
    private readonly SemaphoreSlim _semaphore = new(MaxParallelism, MaxParallelism);
    private long _bytesDone;
    private int _filesDone;
    private volatile string? _lastFile;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public FileDownloader(HttpClient http, string serverUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _serverUrl = serverUrl.TrimEnd('/');
    }

    /// <summary>Downloads all files in parallel and reports overall progress via AnsiConsole.</summary>
    public async Task<DownloadStats> DownloadAllAsync(
        IReadOnlyList<FileEntry> files, string gameFolder, CancellationToken ct)
    {
        var totalBytes = files.Sum(f => f.Size);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = files.Select(entry =>
            DownloadOneAsync(entry, gameFolder, totalBytes, files.Count, linked.Token)).ToList();

        Exception? firstError = null;
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Ctrl+C — propagate as-is
        }
        catch (Exception ex)
        {
            firstError = ex;
            linked.Cancel(); // stop in-flight downloads; their finally blocks remove .tmp files
        }

        if (firstError is not null)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                // drain the remaining tasks, then rethrow the original error
            }

            throw firstError;
        }

        _stopwatch.Stop();
        return new DownloadStats
        {
            FilesDownloaded = files.Count,
            TotalBytes = Interlocked.Read(ref _bytesDone),
            Elapsed = _stopwatch.Elapsed,
        };
    }

    private async Task DownloadOneAsync(
        FileEntry entry, string gameFolder, long totalBytes, int totalFiles, CancellationToken ct)
    {
        var rel = UpdateChecker.ValidateRelativePath(entry.Path);
        var target = Path.Combine(gameFolder, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".tmp";

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await _semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await DownloadToTmpAsync(rel, tmp, totalBytes, totalFiles, ct).ConfigureAwait(false);
                }
                finally
                {
                    _semaphore.Release();
                }

                var actualHash = await HashCalculator.ComputeMd5Async(tmp, ct).ConfigureAwait(false);
                if (string.Equals(actualHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(tmp, target, overwrite: true); // atomic replace
                    Interlocked.Increment(ref _filesDone);
                    return;
                }

                AnsiConsole.Warn($"Hash mismatch for {rel}, retrying ({attempt}/{MaxAttempts}).");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Ctrl+C — the .tmp file was already removed by DownloadToTmpAsync
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TaskCanceledException)
            {
                AnsiConsole.Warn($"Download of {rel} failed ({Describe(ex)}), retrying ({attempt}/{MaxAttempts}).");
            }

            if (attempt >= MaxAttempts)
                throw new DownloadFailedException(rel, null);
        }
    }

    private async Task DownloadToTmpAsync(
        string rel, string tmp, long totalBytes, int totalFiles, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildFileUrl(rel));
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            await using var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = File.Create(tmp);

            _lastFile = rel;
            var buffer = new byte[BufferSize];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = await network.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                Interlocked.Add(ref _bytesDone, read);
                ReportProgress(totalBytes, totalFiles);
            }
        }
        catch
        {
            DeleteQuietly(tmp); // any failure (including Ctrl+C) must not leave a .tmp behind
            throw;
        }
    }

    private void ReportProgress(long totalBytes, int totalFiles)
    {
        var percent = totalBytes > 0 ? (int)(Interlocked.Read(ref _bytesDone) * 100 / totalBytes) : 100;
        AnsiConsole.Progress(percent, BuildDetail(totalFiles));
    }

    private string BuildDetail(int totalFiles)
    {
        var elapsed = _stopwatch.Elapsed;
        var speed = elapsed.TotalSeconds >= 0.25
            ? ByteFormat.Format((long)(Interlocked.Read(ref _bytesDone) / elapsed.TotalSeconds)) + "/s"
            : "—";

        return $"{Volatile.Read(ref _filesDone)}/{totalFiles} | {speed} | {_lastFile ?? string.Empty}";
    }

    private string BuildFileUrl(string relUnixPath) =>
        _serverUrl + "/files/" + string.Join('/', relUnixPath.Split('/').Select(Uri.EscapeDataString));

    private static string Describe(Exception ex) =>
        ex is TaskCanceledException ? "timeout" : (ex.Message.Length > 80 ? ex.Message[..80] + "…" : ex.Message);

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best effort cleanup
        }
    }

    public void Dispose() => _semaphore.Dispose();
}
