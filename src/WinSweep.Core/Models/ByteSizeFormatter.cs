using System.Globalization;

namespace WinSweep.Core.Models;

/// <summary>Formats byte counts the way Windows Explorer does.</summary>
public static class ByteSizeFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "Unknown";
        }

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Round first, then pick the unit, so 999_999 B does not become "1000.0 KB".
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        if (rounded >= 1024 && unit < Units.Length - 1)
        {
            rounded = Math.Round(rounded / 1024, 1, MidpointRounding.AwayFromZero);
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{rounded:0.#} {Units[unit]}");
    }
}
