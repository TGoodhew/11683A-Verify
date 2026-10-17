using System.Globalization;

namespace Verify11683A.Instruments.Dvm;

/// <summary>Number formatting and parsing for instrument strings: always invariant culture.</summary>
internal static class ScpiNumber
{
    /// <summary>Both meters return about ±1E+38 on overload (34401A: 9.90000000E+37).</summary>
    public const double OverloadThreshold = 9.0e37;

    public static string Format(double value) => value.ToString("G", CultureInfo.InvariantCulture);

    public static double Parse(string text, string what)
    {
        var trimmed = text.Trim().Trim('"');
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new DvmException($"Could not read {what} from '{text}'.");
        return value;
    }

    public static bool IsOverload(double value) => Math.Abs(value) >= OverloadThreshold;

    public static bool SameRange(double a, double b) =>
        Math.Abs(a - b) <= 1e-6 * Math.Max(Math.Abs(a), Math.Abs(b));
}
