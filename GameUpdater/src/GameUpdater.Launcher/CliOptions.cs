namespace GameUpdater.Launcher;

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
    public string? ConfigPath { get; private set; }
    public bool NoLaunch { get; private set; }
    public bool NoColor { get; private set; }
    public bool ShowHelp { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config":
                    options.ConfigPath = NextValue(args, ref i, "--config");
                    break;
                case "--no-launch":
                    options.NoLaunch = true;
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

        return options;
    }

    private static string NextValue(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new UsageException($"{name} requires a value.");
        return args[++i];
    }

    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            GameUpdater.Launcher — checks version.json, downloads changed files and launches the game

            Usage:
              launcher [--config <file>] [options]

            Options:
              --config <file>   Config file (default: launcher.json next to the executable)
              --no-launch       Update only; do not start the game
              --no-color        Disable ANSI colors
              -h, --help        Show this help

            Exit codes: 0 — ok, 1 — error, 2 — bad arguments, 130 — Ctrl+C
            """);
    }
}
