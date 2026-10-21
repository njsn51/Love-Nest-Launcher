using System.Windows.Media;

namespace LoveNestLauncher.Common;

public static class BrushHelper
{
    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Brush FromHex(string hex)
    {
        if (Cache.TryGetValue(hex, out var cached))
        {
            return cached;
        }

        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        Cache[hex] = brush;
        return brush;
    }
}
