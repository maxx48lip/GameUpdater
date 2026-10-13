namespace GameUpdater.Shared;

/// <summary>Human-readable byte sizes: "512 B", "12.3 MB", "2.3 GB".</summary>
public static class ByteFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Format(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:F1} {Units[unit]}";
    }
}
