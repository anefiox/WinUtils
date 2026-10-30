using System.Globalization;

namespace WinUtils.Common.Text;

/// <summary>GNU-style sizes: a number with an optional suffix, e.g. 10K, 5MB, 2GiB, 3b.</summary>
public static class Sizes
{
    public static bool TryParse(string text, out long value)
    {
        value = 0;
        int end = 0;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        if (end == 0) return false;

        long multiplier = text[end..] switch
        {
            "" => 1,
            "b" => 512,
            "K" or "KiB" => 1L << 10,
            "kB" or "KB" => 1000,
            "M" or "MiB" => 1L << 20,
            "MB" => 1000_000,
            "G" or "GiB" => 1L << 30,
            "GB" => 1000_000_000,
            "T" or "TiB" => 1L << 40,
            "TB" => 1000_000_000_000,
            "P" or "PiB" => 1L << 50,
            "PB" => 1000_000_000_000_000,
            "E" or "EiB" => 1L << 60,
            "EB" => 1000_000_000_000_000_000,
            _ => 0,
        };
        if (multiplier == 0) return false;

        // Sizes too big to represent mean "effectively unlimited".
        if (!long.TryParse(text.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out long n))
            n = long.MaxValue;
        value = n > long.MaxValue / multiplier ? long.MaxValue : n * multiplier;
        return true;
    }
}
