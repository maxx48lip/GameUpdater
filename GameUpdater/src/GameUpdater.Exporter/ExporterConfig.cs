using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameUpdater.Exporter;

/// <summary>Contents of exporter.json (SFTP connection + export options).</summary>
public sealed class ExporterConfig
{
    [JsonPropertyName("sftpHost")]
    public string SftpHost { get; set; } = string.Empty;

    [JsonPropertyName("sftpPort")]
    public int SftpPort { get; set; } = 22;

    [JsonPropertyName("sftpUser")]
    public string SftpUser { get; set; } = string.Empty;

    /// <summary>Private key path, "~" is expanded. Null/empty → password auth via SFTP_PASSWORD.</summary>
    [JsonPropertyName("sftpKeyPath")]
    public string? SftpKeyPath { get; set; }

    [JsonPropertyName("remoteBasePath")]
    public string RemoteBasePath { get; set; } = "/srv/gamefiles";

    [JsonPropertyName("ignorePatterns")]
    public List<string> IgnorePatterns { get; set; } = new();

    /// <summary>Minimum launcher version written to version.json.</summary>
    [JsonPropertyName("minLauncherVersion")]
    public string MinLauncherVersion { get; set; } = "1.0.0";

    /// <summary>"perFile" (default, robust) or "batch" (one sftp session for all files).</summary>
    [JsonPropertyName("uploadMode")]
    public string UploadMode { get; set; } = "perFile";

    /// <summary>Upload attempts per file before giving up.</summary>
    [JsonPropertyName("maxRetries")]
    public int MaxRetries { get; set; } = 3;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ExporterConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}", path);

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ExporterConfig>(json, JsonOpts)
               ?? throw new InvalidDataException($"Config file is empty or invalid: {path}");
    }

    public void ValidateForUpload()
    {
        if (string.IsNullOrWhiteSpace(SftpHost))
            throw new InvalidDataException("Config: \"sftpHost\" is required for upload.");
        if (string.IsNullOrWhiteSpace(SftpUser))
            throw new InvalidDataException("Config: \"sftpUser\" is required for upload.");
        if (string.IsNullOrWhiteSpace(RemoteBasePath) || !RemoteBasePath.StartsWith('/'))
            throw new InvalidDataException(
                $"Config: \"remoteBasePath\" must be an absolute unix path, got '{RemoteBasePath}'.");
    }
}
