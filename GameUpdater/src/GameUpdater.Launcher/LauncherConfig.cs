using System.Text.Json;
using System.Text.Json.Serialization;
using GameUpdater.Shared;

namespace GameUpdater.Launcher;

/// <summary>
/// Contents of launcher.json — where to fetch updates from and how to start the game.
/// </summary>
public sealed class LauncherConfig
{
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = string.Empty;

    [JsonPropertyName("gameFolder")]
    public string GameFolder { get; set; } = "./game";

    [JsonPropertyName("executable")]
    public string Executable { get; set; } = string.Empty;

    [JsonPropertyName("excludeFromDelete")]
    public List<string> ExcludeFromDelete { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Loads and validates launcher.json.</summary>
    public static LauncherConfig Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot read config file: {path}", ex);
        }

        LauncherConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in config file: {path}", ex);
        }

        if (config is null)
            throw new InvalidOperationException($"Empty config file: {path}");

        config.ServerUrl = PathNormalizer.ToUnix(config.ServerUrl.Trim()).TrimEnd('/');
        config.GameFolder = PathNormalizer.ExpandHome(config.GameFolder.Trim());

        if (!Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Invalid serverUrl '{config.ServerUrl}' — expected an absolute http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(config.Executable))
            throw new InvalidOperationException("Config field 'executable' is required.");

        return config;
    }

    /// <summary>gameFolder resolved to a full path (relative paths are relative to the current directory).</summary>
    public string ResolvedGameFolder => Path.GetFullPath(GameFolder);
}
