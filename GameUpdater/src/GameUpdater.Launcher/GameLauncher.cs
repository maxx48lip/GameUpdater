using System.Diagnostics;
using System.Runtime.Versioning;
using GameUpdater.Shared;

namespace GameUpdater.Launcher;

/// <summary>
/// Cross-platform game start: Windows — plain exe; Linux/macOS — chmod +x then Process.Start with the full path.
/// On macOS a ".app" bundle is resolved to its Contents/MacOS executable.
/// </summary>
public static class GameLauncher
{
    public static Process Start(string gameFolder, string executable)
    {
        var rel = PathNormalizer.ToUnix(executable).TrimStart('/');
        if (rel.Length == 0)
            throw new ArgumentException("Executable path is empty.", nameof(executable));

        var full = Path.Combine(gameFolder, rel.Replace('/', Path.DirectorySeparatorChar));

        // macOS: the "executable" may be a .app bundle — launch its inner binary.
        if (OperatingSystem.IsMacOS() && Directory.Exists(full) &&
            full.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return StartAppBundle(full);
        }

        if (!File.Exists(full))
            throw new FileNotFoundException($"Game executable not found: {full}", full);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            EnsureExecutableBit(full);

        return StartProcess(full, gameFolder);
    }

    [SupportedOSPlatform("macos")]
    private static Process StartAppBundle(string bundlePath)
    {
        var macosDir = Path.Combine(bundlePath, "Contents", "MacOS");
        if (!Directory.Exists(macosDir))
            throw new DirectoryNotFoundException($"Invalid .app bundle (no Contents/MacOS): {bundlePath}");

        var binaries = Directory.GetFiles(macosDir);
        if (binaries.Length == 0)
            throw new DirectoryNotFoundException($"No executable inside the .app bundle: {bundlePath}");

        // By convention the first entry in Contents/MacOS is the main executable.
        var binary = binaries[0];
        EnsureExecutableBit(binary);
        return StartProcess(binary, Path.GetDirectoryName(bundlePath)!);
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void EnsureExecutableBit(string path)
    {
        try
        {
            // 0755: rwxr-xr-x
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot set execute permission on {path}: {ex.Message}", ex);
        }
    }

    private static Process StartProcess(string fileName, string workingDirectory) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
}
