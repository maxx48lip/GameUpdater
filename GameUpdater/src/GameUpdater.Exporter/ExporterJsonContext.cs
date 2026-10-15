using System.Text.Json;
using System.Text.Json.Serialization;
using GameUpdater.Shared.Models;

namespace GameUpdater.Exporter;

/// <summary>
/// Source-generated JSON metadata for the exporter — trim-safe (no reflection),
/// so PublishTrimmed keeps serialization working. Options are the union of what
/// every read/write site in this app used: case-insensitive reads, lenient parsing,
/// indented output.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(ExporterConfig))]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(Manifest))]
public partial class ExporterJsonContext : JsonSerializerContext
{
}
