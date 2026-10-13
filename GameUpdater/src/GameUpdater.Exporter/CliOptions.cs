using System.Text.RegularExpressions;

namespace GameUpdater.Exporter;

/// <summary>Thrown for invalid CLI usage (unknown/missing arguments).</summary>
public sealed class UsageException : Exception
{
    public UsageException(string message) : base(message)
    {
    }
}

/// <summary>Parsed command-line options. Parsed manually from args[] — no parsing libraries.</summary>
public sealed class CliOptions
{
    public string Version { get; private set; } = string.Empty;
    public string Path { get; private set; } = string.Empty;
    public string? ConfigPath { get; private set; }
    public bool DryRun { get; private set; }
    public bool SkipUpload { get; private set; }
    public bool Verbose { get; private set; }
    public bool NoColor { get; private set; }
    public bool ShowHelp { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--version":
                    options.Version = NextValue(args, ref i, "--version");
                    break;
                case "--path":
                    options.Path = NextValue(args, ref i, "--path");
                    break;
                case "--config":
                    options.ConfigPath = NextValue(args, ref i, "--config");
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--skip-upload":
                    options.SkipUpload = true;
                    break;
                case "--verbose":
                    options.Verbose = true;
                    break;
                case "--no-color":
                    options.NoColor = true;
                    break;
                case "-h" or "--help":
                    options.ShowHelp = true;
                    break;
                default:
                    throw new UsageException($"Unknown argument: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(options.Version))
            throw new UsageException("--version is required.");
        if (!IsValidVersion(options.Version))
            throw new UsageException(
                $"Invalid version '{options.Version}' — expected a dotted number, e.g. 1.0.5.");
        if (string.IsNullOrWhiteSpace(options.Path))
            throw new UsageException("--path is required.");

        return options;
    }

    private static string NextValue(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new UsageException($"{name} requires a value.");
        return args[++i];
    }

    private static bool IsValidVersion(string version) =>
        Regex.IsMatch(version, @"^\d+\.\d+\.\d+([.-][0-9A-Za-z.-]+)?$");

    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            GameUpdater.Exporter — build → manifest.json/version.json → SFTP upload

            Usage:
              exporter --version <ver> --path <buildDir> [--config <file>] [options]

            Required:
              --version <ver>     Version string, e.g. 1.0.5
              --path <dir>        Path to the build folder

            Options:
              --config <file>     Config file (default: ./exporter.json)
              --dry-run           Generate manifest locally, do not upload
              --skip-upload       Recompute manifest locally, do not upload
              --verbose           Verbose logging
              --no-color          Disable ANSI colors
              -h, --help          Show this help

            Authentication (see exporter.json):
              key:     "sftpKeyPath" in config (recommended)
              password: SFTP_PASSWORD environment variable (requires sshpass)
            """);
    }
}
