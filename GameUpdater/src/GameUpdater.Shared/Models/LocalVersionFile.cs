using System.Text.Json.Serialization;

namespace GameUpdater.Shared.Models;

/// <summary>Contents of local_version.json — the launcher's own bookkeeping file in gameFolder.</summary>
public sealed class LocalVersionFile
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}
