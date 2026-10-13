using System.Text.RegularExpressions;
using GameUpdater.Shared;
using GameUpdater.Shared.Models;

namespace GameUpdater.Exporter;

/// <summary>A file found during the scan.</summary>
public sealed record ScannedFile(string FullPath, string RelativeUnixPath, long Size);

/// <summary>
/// Walks the build folder, applies ignore patterns, computes streaming MD5 hashes
/// and produces a Manifest sorted by path (relative, unix-style).
/// </summary>
public static class ManifestBuilder
{
    public const string ManifestFileName = "manifest.json";
    public const string VersionFileName = "version.json";

    /// <summary>OS junk always ignored, in addition to config patterns.</summary>
    public static readonly IReadOnlyList<string> BuiltInIgnorePatterns = new[]
    {
        ".DS_Store",
        "Thumbs.db",
        "desktop.ini",
    };

    // --------------------------------------------------------------- scan

    /// <summary>
    /// Recursively collects files under rootDir, skipping ignored entries and generated
    /// artifacts (manifest.json / version.json at the root). Unreadable directories are
    /// skipped with a warning. Result is sorted by relative unix path (ordinal).
    /// </summary>
    public static List<ScannedFile> Scan(string rootDir, IEnumerable<string> ignorePatterns, bool verbose)
    {
        // Built-in OS junk is always ignored, in addition to the caller's patterns.
        var matcher = new IgnoreMatcher(BuiltInIgnorePatterns.Concat(ignorePatterns));
        var results = new List<ScannedFile>();

        void Walk(string dir)
        {
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir);
            }
            catch (UnauthorizedAccessException)
            {
                if (verbose) AnsiConsole.Warn($"Skipping inaccessible directory: {dir}");
                return;
            }

            foreach (var sub in subDirs)
            {
                var relSub = PathNormalizer.ToUnix(Path.GetRelativePath(rootDir, sub));
                if (!matcher.IsIgnored(relSub))
                    Walk(sub);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (UnauthorizedAccessException)
            {
                if (verbose) AnsiConsole.Warn($"Skipping inaccessible directory: {dir}");
                return;
            }

            foreach (var file in files)
            {
                var rel = PathNormalizer.ToUnix(Path.GetRelativePath(rootDir, file));

                // Generated artifacts at the build root are never part of a manifest.
                if (rel == ManifestFileName || rel == VersionFileName)
                    continue;
                if (matcher.IsIgnored(rel))
                    continue;

                long size;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch (Exception) when (!verbose)
                {
                    continue; // vanished or unreadable file — skip silently in quiet mode
                }
                catch (Exception ex)
                {
                    AnsiConsole.Warn($"Skipping unreadable file {rel}: {ex.Message}");
                    continue;
                }

                results.Add(new ScannedFile(file, rel, size));
            }
        }

        Walk(rootDir);
        return results.OrderBy(f => f.RelativeUnixPath, StringComparer.Ordinal).ToList();
    }

    // ------------------------------------------------------------- build

    /// <summary>Scans rootDir and computes MD5 for every file (streaming).</summary>
    public static async Task<Manifest> BuildAsync(
        string rootDir,
        string version,
        IEnumerable<string> ignorePatterns,
        IProgress<(int Done, int Total, long BytesDone)>? progress,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var files = Scan(rootDir, ignorePatterns, verbose);
        var manifest = new Manifest
        {
            Version = version,
            Algorithm = HashCalculator.AlgorithmName,
        };

        long bytesDone = 0;
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];

            if (verbose)
                AnsiConsole.Info($"Hashing {file.RelativeUnixPath} ({ByteFormat.Format(file.Size)})");

            var hash = await HashCalculator.ComputeMd5Async(file.FullPath, cancellationToken).ConfigureAwait(false);
            manifest.Files.Add(new FileEntry
            {
                Path = file.RelativeUnixPath,
                Hash = hash,
                Size = file.Size,
            });

            bytesDone += file.Size;
            progress?.Report((i + 1, files.Count, bytesDone));
        }

        return manifest;
    }

    // ------------------------------------------------------- ignore matcher

    /// <summary>
    /// Glob-style patterns ("*", "?") matched case-insensitively against the full relative
    /// unix path and against the file name. A pattern matching a directory prunes its subtree.
    /// </summary>
    private sealed class IgnoreMatcher
    {
        private readonly List<Regex> _rules = new();

        public IgnoreMatcher(IEnumerable<string> patterns)
        {
            foreach (var pattern in patterns.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(pattern))
                    continue;

                var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                _rules.Add(new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
        }

        public bool IsIgnored(string relativeUnixPath)
        {
            var slash = relativeUnixPath.LastIndexOf('/');
            var name = slash >= 0 ? relativeUnixPath[(slash + 1)..] : relativeUnixPath;

            foreach (var rule in _rules)
                if (rule.IsMatch(relativeUnixPath) || rule.IsMatch(name))
                    return true;

            return false;
        }
    }
}
