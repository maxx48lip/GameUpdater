using System.Text.Json.Serialization;

namespace GameUpdater.Shared.Models;

/// <summary>
/// Contents of version.json — the last file uploaded to the server.
/// </summary>
public sealed class VersionInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("buildDate")]
    public string BuildDate { get; set; } = string.Empty;

    [JsonPropertyName("minLauncherVersion")]
    public string MinLauncherVersion { get; set; } = "1.0.0";
}
