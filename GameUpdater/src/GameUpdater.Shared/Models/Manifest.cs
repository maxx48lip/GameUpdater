using System.Text.Json.Serialization;

namespace GameUpdater.Shared.Models;

/// <summary>
/// Contents of manifest.json — a list of files with checksums.
/// </summary>
public sealed class Manifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("algorithm")]
    public string Algorithm { get; set; } = "MD5";

    [JsonPropertyName("files")]
    public List<FileEntry> Files { get; set; } = new();
}

/// <summary>
/// A single file entry inside a manifest. Paths are relative and use forward slashes.
/// </summary>
public sealed class FileEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
