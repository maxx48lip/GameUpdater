using System.Diagnostics;
using System.Text;

namespace GameUpdater.Shared;

/// <summary>Connection settings for the external sftp client (OpenSSH).</summary>
public sealed class SftpOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string User { get; init; }

    /// <summary>Private key path after "~" expansion. Null → password auth via SFTP_PASSWORD env var.</summary>
    public string? KeyPath { get; init; }

    public int ConnectTimeoutSeconds { get; init; } = 15;
}

/// <summary>Result of an sftp process run.</summary>
public sealed record SftpCommandResult(int ExitCode, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Wrapper around the external OpenSSH "sftp" client (System.Diagnostics.Process).
/// No NuGet dependencies: batch files are written to a temp dir and fed via "-b",
/// interactive commands are piped through stdin.
/// </summary>
public static class SftpClientWrapper
{
    private const int MaxStdErrChars = 65536;

    private static readonly object Sync = new();
    private static Task<string>? _findTask;
    private static string? _sftpVersion;

    /// <summary>OpenSSH version reported by "sftp -V" (e.g. "OpenSSH_9.6"), if detected.</summary>
    public static string? SftpVersion => _sftpVersion;

    // ------------------------------------------------------------ discovery

    /// <summary>
    /// Finds the sftp executable in PATH ("sftp.exe"/"sftp") and verifies it runs via "-V".
    /// Result is cached for the process lifetime.
    /// </summary>
    public static Task<string> FindSftpAsync(CancellationToken cancellationToken = default)
    {
        lock (Sync)
            return _findTask ??= FindSftpCoreAsync(cancellationToken);
    }

    private static async Task<string> FindSftpCoreAsync(CancellationToken ct)
    {
        var found = SearchPath() ?? throw new InvalidOperationException(
            "'sftp' was not found in PATH. Install the OpenSSH client: it ships with " +
            "Windows 10 1809+, macOS and most Linux distributions.");

        // Best-effort version probe for logging.
        try
        {
            var psi = new ProcessStartInfo(found, "-V")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start sftp.");
            var version = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode == 0)
                _sftpVersion = version.Trim();
        }
        catch
        {
            // Version probe is not critical.
        }

        _sftpExeCached = found;
        return found;
    }

    /// <summary>Synchronous PATH lookup (no version probe); used by the argument builder.</summary>
    private static string ResolveSftpExecutable() =>
        _sftpExeCached ??= SearchPath() ?? throw new InvalidOperationException(
            "'sftp' was not found in PATH. Install the OpenSSH client.");

    private static string? SearchPath()
    {
        var candidates = OperatingSystem.IsWindows() ? new[] { "sftp.exe", "sftp" } : new[] { "sftp" };
        foreach (var name in candidates)
            if (IsCommandAvailable(name))
                return name;
        return null;
    }

    private static bool IsCommandAvailable(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), OperatingSystem.IsWindows() ? name + ".exe" : name);
                if (File.Exists(candidate))
                    return true;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return false;
    }

    // ------------------------------------------------------------- uploads

    /// <summary>
    /// Creates remote directories via one sftp session (commands piped through stdin, no "-b",
    /// so an "Already exists" error does not abort the session). Any other mkdir failure is fatal.
    /// </summary>
    public static async Task EnsureRemoteDirectoriesAsync(
        SftpOptions options, IReadOnlyCollection<string> directories, CancellationToken ct)
    {
        if (directories.Count == 0)
            return;

        var commands = new List<string>(directories.Count + 1);
        foreach (var dir in directories)
            commands.Add($"mkdir {Quote(dir)}");
        commands.Add("bye");

        var result = await RunSftpCoreAsync(options, batchFilePath: null, stdinCommands: commands, onStdErrLine: null, ct)
            .ConfigureAwait(false);

        // Scan for real mkdir failures. "Already exists" is expected and harmless;
        // a non-zero exit code alone is not proof of failure (sftp may report it even
        // when only benign errors occurred), so combine both signals.
        var sawMkdirOutput = false;
        foreach (var line in result.StdErr.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("mkdir:", StringComparison.Ordinal))
                continue;
            sawMkdirOutput = true;
            if (!trimmed.Contains("Already exists", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Remote directory creation failed: {trimmed}");
        }

        if (!result.Success && !sawMkdirOutput)
            throw new InvalidOperationException(
                $"Could not connect to {options.User}@{options.Host}:{options.Port}: " +
                FirstMeaningfulLine(result.StdErr));
    }

    /// <summary>
    /// Uploads a single file in its own sftp process ("-b" batch with one put), so the exit code
    /// reliably reflects success/failure. Returns (success, stderr tail).
    /// </summary>
    public static async Task<(bool Success, string StdErrTail)> UploadFileAsync(
        SftpOptions options, string localPath, string remotePath, CancellationToken ct)
    {
        var batch = MakeTempBatchFile(new[] { $"put {Quote(PathNormalizer.ToUnix(localPath))} {Quote(remotePath)}" });
        try
        {
            var result = await RunSftpCoreAsync(options, batchFilePath: batch, stdinCommands: null, onStdErrLine: null, ct)
                .ConfigureAwait(false);
            return (result.Success, Tail(result.StdErr));
        }
        finally
        {
            TryDeleteFile(batch);
        }
    }

    /// <summary>
    /// Downloads a single remote file in its own sftp process ("-b" batch with one get), so the exit
    /// code reliably reflects success/failure. A missing remote file reports "No such file" in stderr.
    /// Returns (success, stderr tail).
    /// </summary>
    public static async Task<(bool Success, string StdErrTail)> DownloadFileAsync(
        SftpOptions options, string remotePath, string localPath, CancellationToken ct)
    {
        var batch = MakeTempBatchFile(new[] { $"get {Quote(remotePath)} {Quote(PathNormalizer.ToUnix(localPath))}" });
        try
        {
            var result = await RunSftpCoreAsync(options, batchFilePath: batch, stdinCommands: null, onStdErrLine: null, ct)
                .ConfigureAwait(false);
            return (result.Success, Tail(result.StdErr));
        }
        finally
        {
            TryDeleteFile(batch);
        }
    }

    /// <summary>
    /// Removes remote files via one sftp session (commands piped through stdin, no "-b", so a missing
    /// file does not abort the session). Any other rm failure is fatal.
    /// </summary>
    public static async Task RemoveRemoteFilesAsync(
        SftpOptions options, IReadOnlyCollection<string> files, CancellationToken ct)
    {
        if (files.Count == 0)
            return;

        var commands = new List<string>(files.Count + 1);
        foreach (var file in files)
            commands.Add($"rm {Quote(file)}");
        commands.Add("bye");

        var result = await RunSftpCoreAsync(options, batchFilePath: null, stdinCommands: commands, onStdErrLine: null, ct)
            .ConfigureAwait(false);

        // Scan for real rm failures. "No such file" is expected and harmless (already removed);
        // a non-zero exit code alone is not proof of failure, so combine both signals.
        var sawRmOutput = false;
        foreach (var line in result.StdErr.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("rm:", StringComparison.Ordinal))
                continue;
            sawRmOutput = true;
            if (!trimmed.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Remote file removal failed: {trimmed}");
        }

        if (!result.Success && !sawRmOutput)
            throw new InvalidOperationException(
                $"Could not connect to {options.User}@{options.Host}:{options.Port}: " +
                FirstMeaningfulLine(result.StdErr));
    }

    /// <summary>
    /// Uploads all files in one sftp session ("-b" batch). onStdErrLine receives each stderr line live
    /// (used for progress and to locate the failed file when the batch aborts).
    /// </summary>
    public static async Task<(bool Success, string StdErrTail)> UploadBatchAsync(
        SftpOptions options, IReadOnlyList<(string LocalPath, string RemotePath)> files,
        Action<string>? onStdErrLine, CancellationToken ct)
    {
        var lines = new List<string>(files.Count);
        foreach (var (local, remote) in files)
            lines.Add($"put {Quote(PathNormalizer.ToUnix(local))} {Quote(remote)}");

        var batch = MakeTempBatchFile(lines);
        try
        {
            var result = await RunSftpCoreAsync(options, batchFilePath: batch, stdinCommands: null, onStdErrLine, ct)
                .ConfigureAwait(false);
            return (result.Success, Tail(result.StdErr));
        }
        finally
        {
            TryDeleteFile(batch);
        }
    }

    // ---------------------------------------------------------- core runner

    private static async Task<SftpCommandResult> RunSftpCoreAsync(
        SftpOptions options, string? batchFilePath, IEnumerable<string>? stdinCommands,
        Action<string>? onStdErrLine, CancellationToken ct)
    {
        var (exe, passwordMode) = PrepareAuth(options);
        var psi = BuildStartInfo(exe, options, passwordMode, batchFilePath);

        using var process = new Process { StartInfo = psi };
        var stderrBuilder = new StringBuilder();
        var stdoutBuilder = new StringBuilder();
        var outputFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { } data)
                return;
            lock (stderrBuilder)
                stderrBuilder.AppendLine(data);
            try
            {
                onStdErrLine?.Invoke(data);
            }
            catch
            {
                // A progress callback must never kill the output reader.
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { } data)
                return;
            lock (stdoutBuilder)
                stdoutBuilder.AppendLine(data);
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Failed to start sftp: {ex.Message}", ex);
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => outputFlushed.TrySetResult();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        if (stdinCommands is not null)
        {
            // Write LF-only lines: sftp rejects "\r" in batch input.
            using var stdin = process.StandardInput;
            foreach (var command in stdinCommands)
                await stdin.WriteAsync((command + "\n").AsMemory(), ct).ConfigureAwait(false);
            // Closing stdin signals EOF — sftp exits after processing the commands.
        }

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (batchFilePath is not null)
                TryDeleteFile(batchFilePath);
        }

        // Give async output readers a bounded grace period to flush the tail.
        await Task.WhenAny(outputFlushed.Task, Task.Delay(2000)).ConfigureAwait(false);

        string stderr;
        lock (stderrBuilder)
            stderr = stderrBuilder.ToString();

        return new SftpCommandResult(process.ExitCode, Cap(stderr));
    }

    private static string? _sftpExeCached;

    private static (string Exe, bool PasswordMode) PrepareAuth(SftpOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.KeyPath))
        {
            if (!File.Exists(options.KeyPath))
                throw new FileNotFoundException($"SFTP key file not found: {options.KeyPath}");
            return (ResolveSftpExecutable(), false);
        }

        var password = Environment.GetEnvironmentVariable("SFTP_PASSWORD");
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException(
                "No SFTP authentication configured. Set \"sftpKeyPath\" in the config file " +
                "(recommended) or export the SFTP_PASSWORD environment variable.");

        // OpenSSH sftp cannot read a password non-interactively; sshpass does it for us.
        if (!IsCommandAvailable("sshpass"))
            throw new InvalidOperationException(
                "Password authentication requires 'sshpass' (not found in PATH). Install it " +
                "(e.g. 'brew install hudochenkov/sshpass/sshpass', 'apt-get install sshpass') " +
                "or switch to key-based auth.");

        return ("sshpass", true);
    }

    private static ProcessStartInfo BuildStartInfo(
        string exe, SftpOptions options, bool passwordMode, string? batchFilePath)
    {
        var arguments = new StringBuilder();
        if (passwordMode)
            arguments.Append("-e sftp "); // sshpass -e <command...>

        if (options.Port != 22)
            arguments.Append("-P ").Append(options.Port).Append(' ');

        if (!string.IsNullOrWhiteSpace(options.KeyPath))
            arguments.Append("-i ").Append(Quote(PathNormalizer.ToUnix(options.KeyPath!))).Append(' ');

        arguments.Append("-o StrictHostKeyChecking=accept-new ");
        arguments.Append("-o ConnectTimeout=").Append(options.ConnectTimeoutSeconds).Append(' ');
        if (!passwordMode)
            arguments.Append("-o BatchMode=yes ");

        if (batchFilePath is not null)
            arguments.Append("-b ").Append(Quote(batchFilePath)).Append(' ');

        arguments.Append(options.User).Append('@').Append(options.Host);

        var psi = new ProcessStartInfo(exe, arguments.ToString())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (passwordMode)
            psi.Environment["SSHPASS"] = Environment.GetEnvironmentVariable("SFTP_PASSWORD") ?? string.Empty;

        return psi;
    }

    // -------------------------------------------------------------- helpers

    private static string MakeTempBatchFile(IEnumerable<string> lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gameup-sftp-{Guid.NewGuid():N}.sftp");
        // LF-only line endings: sftp -b aborts on "\r" ("newline in batch command").
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    /// <summary>Quotes an argument for the sftp command parser (whitespace-aware).</summary>
    private static string Quote(string arg) =>
        arg.Contains(' ') || arg.Contains('"') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static string Cap(string text) =>
        text.Length <= MaxStdErrChars ? text : text[^MaxStdErrChars..];

    private static string Tail(string text, int maxLines = 5)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length <= maxLines)
            return text.Trim();
        return string.Join(" | ", lines[^maxLines..]);
    }

    private static string FirstMeaningfulLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0 && !line.StartsWith("Warning:", StringComparison.Ordinal))
                return line;
        }

        return "unknown error";
    }
}
