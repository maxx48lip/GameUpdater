using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameUpdater.Shared;

/// <summary>
/// Shared console output: colored log lines (INFO/WARN/ERROR/OK) and a single-line
/// progress bar throttled to 10 updates/sec. Thread-safe.
///
/// Fallback without TTY: when stdout is redirected (Console.IsOutputRedirected),
/// ANSI colors and the progress bar are disabled — only plain log lines remain.
/// </summary>
public static class AnsiConsole
{
    private const int BarWidth = 20;
    private const double MinRenderIntervalMs = 100.0; // max 10 updates per second

    private static readonly object Sync = new();
    private static bool? _ansiEnabled;
    private static bool _progressLineActive;
    private static double _lastProgressRenderMs;

    /// <summary>Force-disables ANSI colors (CLI flag --no-color).</summary>
    public static void DisableColor()
    {
        lock (Sync)
            _ansiEnabled = false;
    }

    // ------------------------------------------------------------------ log

    public static void Info(string message) => WriteLog("\x1b[36m", "INFO", message);
    public static void Warn(string message) => WriteLog("\x1b[33m", "WARN", message);
    public static void Error(string message) => WriteLog("\x1b[31m", "ERROR", message);
    public static void Ok(string message) => WriteLog("\x1b[32m", "OK", message);

    private static void WriteLog(string color, string level, string message)
    {
        lock (Sync)
        {
            // If a progress bar occupies the last line, finish it first.
            if (_progressLineActive)
            {
                Console.Write('\n');
                _progressLineActive = false;
            }

            var prefix = $"[{level}]".PadRight(7);
            if (AnsiEnabledCore())
                Console.WriteLine($"{color}{prefix}\x1b[0m {message}");
            else
                Console.WriteLine($"{prefix} {message}");
        }
    }

    // ---------------------------------------------------------- progress bar

    /// <summary>
    /// Renders a single-line progress bar via \r, at most 10 times per second.
    /// Format: [████░░] 45% | &lt;detail&gt;. No-op when stdout is redirected.
    /// </summary>
    public static void Progress(int percent, string? detail = null)
    {
        percent = Math.Clamp(percent, 0, 100);

        lock (Sync)
        {
            if (!AnsiEnabledCore())
                return; // no TTY: progress bar disabled, log only

            var nowMs = Stopwatch.GetTimestamp() * 1000.0 / TimeSpan.TicksPerMillisecond;
            var isFinal = percent == 100;
            if (!isFinal && _progressLineActive && nowMs - _lastProgressRenderMs < MinRenderIntervalMs)
                return;

            var filled = (int)(BarWidth * percent / 100.0 + 0.5);
            var bar = new string('█', filled) + new string('░', BarWidth - filled);
            var line = $"[{bar}] {percent}%";
            if (!string.IsNullOrEmpty(detail))
                line += $" | {detail}";

            Console.Write("\r" + line);
            _progressLineActive = true;
            _lastProgressRenderMs = nowMs;
        }
    }

    /// <summary>Terminates the progress line so the next log entry starts on a fresh line.</summary>
    public static void EndProgress()
    {
        lock (Sync)
        {
            if (_progressLineActive)
            {
                Console.Write('\n');
                _progressLineActive = false;
            }
        }
    }

    // ------------------------------------------------- ANSI support detection

    private static bool AnsiEnabledCore()
    {
        if (_ansiEnabled.HasValue)
            return _ansiEnabled.Value;

        var enabled = DetectAnsiSupport();
        _ansiEnabled = enabled;
        return enabled;
    }

    private static bool DetectAnsiSupport()
    {
        // No TTY (output redirected to a file/pipe): plain log only.
        if (Console.IsOutputRedirected)
            return false;

        var term = Environment.GetEnvironmentVariable("TERM");
        if (!string.IsNullOrEmpty(term) && string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
            return false;

        if (OperatingSystem.IsWindows())
        {
            // Enable virtual terminal processing so ANSI escapes render on Windows 10+.
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                var handle = GetStdHandle(STD_OUTPUT_HANDLE);
                if (handle != IntPtr.Zero && GetConsoleMode(handle, out uint mode))
                    SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
            }
            catch
            {
                return false; // cannot enable VT processing — fall back to plain output
            }
        }

        return true; // macOS/Linux: ANSI works out of the box
    }

    // ------------------------------------------------------ Windows P/Invoke

    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
