using System.Globalization;
using System.IO;

namespace LoveNestLauncher.Common;

public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 || unit == 0 ? "0" : value >= 10 ? "0.0" : "0.00";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {Units[unit]}";
    }

    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return string.Empty;
        }

        return $"{Bytes((long)bytesPerSecond)}/s";
    }

    public static string Eta(long remainingBytes, double bytesPerSecond)
    {
        if (remainingBytes <= 0 || bytesPerSecond <= 0)
        {
            return string.Empty;
        }

        var seconds = Math.Max(0, (int)Math.Ceiling(remainingBytes / bytesPerSecond));
        if (seconds < 60)
        {
            return $"剩余 {seconds} 秒";
        }

        if (seconds < 3600)
        {
            return $"剩余 {seconds / 60} 分 {seconds % 60} 秒";
        }

        return $"剩余 {seconds / 3600} 时 {seconds % 3600 / 60} 分";
    }

    public static string ShortPath(string path, int maxLength = 44)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length <= maxLength)
        {
            return path;
        }

        var root = Path.GetPathRoot(path) ?? string.Empty;
        var remainder = path[root.Length..];
        var keep = Math.Max(8, maxLength - root.Length - 2);
        return $"{root}…{remainder[^keep..]}";
    }
}
