using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameUpdater.Shared;
using GameUpdater.Shared.Models;

namespace GameUpdater.Launcher;

/// <summary>The result of a differential check: what to download and what to delete.</summary>
public sealed class UpdatePlan
{
    /// <summary>Manifest files that are missing locally, differ in size, or fail the MD5 check.</summary>
    public List<FileEntry> ToDownload { get; } = new();

    /// <summary>Local files (relative unix paths) absent from the manifest — to be deleted.</summary>
    public List<string> ToDelete { get; } = new();

    public long TotalBytesToDownload => ToDownload.Sum(f => f.Size);
}

/// <summary>
/// Compares local/remote versions and performs a differential file check against the manifest:
/// missing files, size mismatches and MD5 mismatches go to download; extra local files go to delete.
/// </summary>
public static class UpdateChecker
{
    public const string LocalVersionFileName = "local_version.json";

    /// <summary>Reads the local version from gameFolder/local_version.json. Returns null when absent or unreadable.</summary>
    public static string? ReadLocalVersion(string gameFolder)
    {
        var path = Path.Combine(gameFolder, LocalVersionFileName);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Treat a missing/corrupt file as "no local version" → full update.
            return null;
        }
    }

    /// <summary>Writes local_version.json atomically (tmp + move).</summary>
    public static void WriteLocalVersion(string gameFolder, string version)
    {
        var path = Path.Combine(gameFolder, LocalVersionFileName);
        var tmp = path + ".tmp";
        File.WriteAllText(
            tmp,
            JsonSerializer.Serialize(new LocalVersionFile { Version = version }, LauncherJsonContext.Default.LocalVersionFile));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>True when the local version equals the remote one (ordinal comparison).</summary>
    public static bool IsUpToDate(string? localVersion, string remoteVersion) =>
        localVersion is not null && string.Equals(localVersion, remoteVersion, StringComparison.Ordinal);

    /// <summary>
    /// Computes the update plan. For each manifest file: missing locally / different size / MD5 mismatch → ToDownload.
    /// Local files that are absent from the manifest and not excluded by excludeFromDelete → ToDelete.
    /// </summary>
    public static async Task<UpdatePlan> ComputeDiffAsync(
        Manifest manifest, string gameFolder, IReadOnlyList<string>? excludeFromDelete = null, CancellationToken ct = default)
    {
        var plan = new UpdatePlan();

        // 1) Which files from the manifest must be (re)downloaded.
        var manifestPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Files)
        {
            var rel = ValidateRelativePath(entry.Path);
            manifestPaths.Add(rel);

            ct.ThrowIfCancellationRequested();
            var localPath = Path.Combine(gameFolder, rel.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(localPath))
            {
                plan.ToDownload.Add(entry);
                continue;
            }

            var size = new FileInfo(localPath).Length;
            if (size != entry.Size)
            {
                plan.ToDownload.Add(entry);
                continue;
            }

            // Size matches — verify the hash.
            var actualHash = await HashCalculator.ComputeMd5Async(localPath, ct).ConfigureAwait(false);
            if (!string.Equals(actualHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                plan.ToDownload.Add(entry);
        }

        // 2) Which local files are obsolete (not in the manifest) and must be deleted.
        if (Directory.Exists(gameFolder))
        {
            foreach (var fullPath in Directory.EnumerateFiles(gameFolder, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = PathNormalizer.ToRelativeUnix(gameFolder, fullPath);

                if (manifestPaths.Contains(rel))
                    continue;
                if (IsExcluded(rel, excludeFromDelete))
                    continue;

                plan.ToDelete.Add(rel);
            }
        }

        return plan;
    }

    /// <summary>Rejects absolute paths and ".." segments — manifest entries must stay inside gameFolder.</summary>
    public static string ValidateRelativePath(string path)
    {
        var rel = PathNormalizer.ToUnix(path).TrimStart('/');
        if (rel.Length == 0 || rel.Split('/').Any(segment => segment == ".."))
            throw new InvalidOperationException($"Invalid path in manifest: '{path}'");
        return rel;
    }

    /// <summary>
    /// Checks a relative unix path against excludeFromDelete patterns.
    /// A pattern without wildcards matches the file exactly or an entire directory ("logs" → everything under logs/).
    /// Wildcards: '*' — any characters except '/', '?' — one character, '**' — anything including '/'.
    /// local_version.json is always excluded (it is the launcher's own bookkeeping file).
    /// </summary>
    public static bool IsExcluded(string relativeUnixPath, IReadOnlyList<string>? patterns)
    {
        if (relativeUnixPath == LocalVersionFileName)
            return true;

        if (patterns is null || patterns.Count == 0)
            return false;

        foreach (var raw in patterns)
        {
            var pattern = PathNormalizer.ToUnix(raw).Trim().TrimStart('/');
            if (pattern.Length == 0)
                continue;

            if (relativeUnixPath.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!pattern.Contains('*') && !pattern.Contains('?'))
            {
                // Plain name: also covers the whole directory subtree.
                if (relativeUnixPath.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            var regex = "^" + GlobToRegex(pattern) + "$";
            if (Regex.IsMatch(relativeUnixPath, regex, RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }

    private static string GlobToRegex(string pattern)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    sb.Append(".*");
                    i++;
                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(pattern[i].ToString()));
                    break;
            }
        }

        return sb.ToString();
    }
}
