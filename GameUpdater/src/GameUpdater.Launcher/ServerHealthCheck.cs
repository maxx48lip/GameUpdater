using System.Net.Sockets;
using System.Text.Json;

namespace GameUpdater.Launcher;

/// <summary>
/// Pre-launch server availability check: reads servers.json from the game folder and requests the
/// gateway's /healthz route. Any HTTP response with status &lt; 500 means the gateway is alive; a
/// network error or timeout means it is unavailable. A missing or unreadable servers.json skips the
/// check (the game will report the problem at login itself).
/// </summary>
public static class ServerHealthCheck
{
    public const string ServersFileName = "servers.json";

    private const string HealthPath = "/healthz";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>ServerUrl is null when the check was skipped (no file / no address).</summary>
    public sealed record Outcome(bool Available, string? ServerUrl, string? Error)
    {
        public bool Skipped => ServerUrl is null;
    }

    public static async Task<Outcome> CheckAsync(string gameFolder, CancellationToken ct = default)
    {
        var path = Path.Combine(gameFolder, ServersFileName);
        if (!File.Exists(path))
            return new Outcome(true, null, $"{ServersFileName} not found — server check skipped");

        string? url;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            // ServersFile is always registered in LauncherJsonContext.
            var typeInfo = LauncherJsonContext.Default.GetTypeInfo(typeof(ServersFile))
                ?? throw new InvalidOperationException("ServersFile is not registered in LauncherJsonContext.");
            url = ((ServersFile?)JsonSerializer.Deserialize(json, typeInfo))?.Server;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new Outcome(true, null, $"{ServersFileName} cannot be read ({FirstLine(ex.Message)}) — server check skipped");
        }

        if (string.IsNullOrWhiteSpace(url))
            return new Outcome(true, null, $"no \"server\" in {ServersFileName} — server check skipped");

        url = url.Trim().TrimEnd('/');
        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            using var response = await http
                .GetAsync(url + HealthPath, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if ((int)response.StatusCode < 500)
                return new Outcome(true, url, null);
            return new Outcome(false, url, $"server answered {(int)response.StatusCode} {response.StatusCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Ctrl+C — let the caller clean up
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException or UriFormatException)
        {
            var message = ex is TaskCanceledException && !ct.IsCancellationRequested ? "timeout" : FirstLine(ex.Message);
            return new Outcome(false, url, message);
        }
    }

    private static string FirstLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0)
                return line;
        }

        return "unknown error";
    }
}
