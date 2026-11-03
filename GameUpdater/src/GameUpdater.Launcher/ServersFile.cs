using System.Text.Json.Serialization;

namespace GameUpdater.Launcher;

/// <summary>
/// servers.json in the game folder: gateway address. Distributed by the launcher as a regular
/// manifest file (the Unity client reads it via ServersStore); the launcher health-checks this URL
/// before starting the game.
/// </summary>
public sealed class ServersFile
{
    [JsonPropertyName("server")]
    public string? Server { get; set; }
}
