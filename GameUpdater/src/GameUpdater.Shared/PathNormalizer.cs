namespace GameUpdater.Shared;

/// <summary>Cross-platform path helpers.</summary>
public static class PathNormalizer
{
    /// <summary>Converts a path to unix format (forward slashes), regardless of OS.</summary>
    public static string ToUnix(string path) => path.Replace('\\', '/');

    /// <summary>Expands a leading "~" or "~/..." to the user profile directory.</summary>
    public static string ExpandHome(string path)
    {
        if (path is "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        return path;
    }

    /// <summary>Relative unix-style path of fullPath inside root.</summary>
    public static string ToRelativeUnix(string root, string fullPath) =>
        ToUnix(Path.GetRelativePath(root, fullPath));
}
