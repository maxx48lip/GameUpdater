using GameUpdater.Shared.Models;

namespace GameUpdater.Exporter;

/// <summary>Result of comparing the local wiki manifest against the previous remote one.</summary>
public sealed record WikiDeltaResult(IReadOnlyList<FileEntry> ToUpload, IReadOnlyList<string> StalePaths);

/// <summary>
/// Computes which wiki files must be uploaded and which stale remote files should be removed.
/// A file is "changed" when its path is missing from the previous manifest or its hash differs
/// (hashes are compared case-insensitively). Files present only in the previous manifest are stale.
/// </summary>
public static class WikiDelta
{
    public static WikiDeltaResult Compute(Manifest local, Manifest? previous)
    {
        var toUpload = new List<FileEntry>();

        if (previous is null || previous.Files.Count == 0)
        {
            // First run: no remote manifest yet — everything is new.
            toUpload.AddRange(local.Files);
        }
        else
        {
            var remoteByPath = new Dictionary<string, FileEntry>(previous.Files.Count, StringComparer.Ordinal);
            foreach (var entry in previous.Files)
                remoteByPath[entry.Path] = entry;

            foreach (var file in local.Files)
            {
                if (!remoteByPath.TryGetValue(file.Path, out var remote) ||
                    !string.Equals(remote.Hash, file.Hash, StringComparison.OrdinalIgnoreCase))
                    toUpload.Add(file);
            }
        }

        var stalePaths = new List<string>();
        if (previous is not null && previous.Files.Count > 0)
        {
            var localByPath = new HashSet<string>(local.Files.Select(f => f.Path), StringComparer.Ordinal);
            foreach (var entry in previous.Files)
                if (!localByPath.Contains(entry.Path))
                    stalePaths.Add(entry.Path);
        }

        return new WikiDeltaResult(toUpload, stalePaths);
    }
}
