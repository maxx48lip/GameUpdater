using System.Text.Json;
using System.Text.Json.Serialization;
using GameUpdater.Shared.Models;

namespace GameUpdater.Launcher;

/// <summary>
/// Source-generated JSON metadata for the launcher — trim-safe (no reflection),
/// so PublishTrimmed keeps serialization working. Options are the union of what
/// every read/write site in this app used: case-insensitive reads, lenient parsing,
/// indented output.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(LauncherConfig))]
[JsonSerializable(typeof(LocalVersionFile))]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(Manifest))]
public partial class LauncherJsonContext : JsonSerializerContext
{
}
