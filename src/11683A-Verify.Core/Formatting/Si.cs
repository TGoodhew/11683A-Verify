using System.Globalization;

namespace Verify11683A.Formatting;

/// <summary>Engineering-prefix formatting: 0.145 V → "145.000 mV", 3150.2 Ω → "3.15020 kΩ".</summary>
public static class Si
{
    private static readonly (double Scale, string Prefix)[] Prefixes =
    [
        (1e9, "G"), (1e6, "M"), (1e3, "k"), (1, ""), (1e-3, "m"), (1e-6, "µ"), (1e-9, "n"),
    ];

    /// <summary>Formats with a fixed number of significant digits.</summary>
    public static string Format(double value, string unit, int significantDigits = 6)
    {
        if (double.IsNaN(value)) return "—";
        if (double.IsInfinity(value)) return value > 0 ? "∞" : "-∞";
        if (value == 0) return $"0 {unit}";

        var (scale, prefix) = PrefixFor(value);
        var scaled = value / scale;
        var integerDigits = (int)Math.Floor(Math.Log10(Math.Abs(scaled))) + 1;
        var decimals = Math.Clamp(significantDigits - integerDigits, 0, 9);
        return scaled.ToString("F" + decimals, CultureInfo.InvariantCulture) + " " + prefix + unit;
    }

    /// <summary>Formats a range name compactly: 100 mV, 1 V, 10 kΩ.</summary>
    public static string Range(double range, string unit)
    {
        var (scale, prefix) = PrefixFor(range);
        return (range / scale).ToString("0.###", CultureInfo.InvariantCulture) + " " + prefix + unit;
    }

    /// <summary>A fraction in parts per million, e.g. 1.23e-5 → "12.3 ppm".</summary>
    public static string Ppm(double fraction) =>
        (fraction * 1e6).ToString(fraction * 1e6 >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture) + " ppm";

    private static (double Scale, string Prefix) PrefixFor(double value)
    {
        var magnitude = Math.Abs(value);
        foreach (var p in Prefixes)
            if (magnitude >= p.Scale * 0.9999999)
                return p;
        return Prefixes[^1];
    }
}
