using System.Globalization;
using System.Text.RegularExpressions;

namespace Verify11683A.H01;

/// <summary>One row of the E4418B Service Guide's Table 2-4 (p. 2-14): a DC calibrator setting and the power it stands for.</summary>
public sealed record H01Row(double SettingVolts, double EffectiveWatts, int Filter, double MeasUncWatts, double LowerWatts, double UpperWatts);

/// <summary>
/// The Option H01 REF EXT data, from data/h01-ext-table.json. Table 2-4 is not linear in power above 1 mW, so its rows
/// are only ever used as data: <see cref="EffectiveWatts"/> interpolates between them and never extrapolates a slope.
/// </summary>
public sealed class H01Table
{
    public const string FileName = "h01-ext-table.json";

    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string RangeSetting { get; init; } = "";
    public string RangeSource { get; init; } = "";
    public double CalibrationVolts { get; init; }
    public string CalibrationSource { get; init; } = "";

    /// <summary>The default agreement tolerance for the INT/EXT transfer check (0.2%).</summary>
    public double TransferTolerance { get; init; }

    public List<string> Notes { get; init; } = [];
    public List<H01Row> Rows { get; init; } = [];

    public static H01Table Load(string path)
    {
        var table = Json.Load<H01Table>(path);
        table.Validate();
        return table;
    }

    public static H01Table LoadDefault(string? dataDirectory = null) => Load(DataFiles.Resolve(FileName, dataDirectory));

    /// <summary>
    /// The power a REF EXT voltage stands for: through every row exactly as printed, interpolated between neighbouring
    /// rows on log-log axes (exact where the table is linear, as it is up to 1 mW), and linear through zero below the
    /// first row. Up to 0.1% beyond the last row (a calibrator set a hair high) follows the last segment; further it throws.
    /// </summary>
    public double EffectiveWatts(double volts)
    {
        if (volts <= 0)
            return 0;
        if (volts <= Rows[0].SettingVolts)
            return Rows[0].EffectiveWatts * volts / Rows[0].SettingVolts;
        for (var i = 1; i < Rows.Count; i++)
        {
            var (a, b) = (Rows[i - 1], Rows[i]);
            var last = i == Rows.Count - 1;
            if (volts <= b.SettingVolts * (1 + (last ? BeyondLastRow : 1e-12)))
            {
                var t = Math.Log(volts / a.SettingVolts) / Math.Log(b.SettingVolts / a.SettingVolts);
                return a.EffectiveWatts * Math.Pow(b.EffectiveWatts / a.EffectiveWatts, t);
            }
        }
        throw new ArgumentOutOfRangeException(nameof(volts), $"{volts} V is beyond Table 2-4 ({Rows[^1].SettingVolts} V).");
    }

    /// <summary>How far past the last row a voltage may be and still be interpolated along the last segment.</summary>
    public const double BeyondLastRow = 1e-3;

    private void Validate()
    {
        if (Rows.Count == 0)
            throw new InvalidDataException($"{FileName} has no rows.");
        for (var i = 1; i < Rows.Count; i++)
            if (Rows[i].SettingVolts <= Rows[i - 1].SettingVolts || Rows[i].EffectiveWatts <= Rows[i - 1].EffectiveWatts)
                throw new InvalidDataException($"{FileName}: rows must rise in voltage and power.");
        foreach (var r in Rows)
            if (r.LowerWatts > r.EffectiveWatts || r.UpperWatts < r.EffectiveWatts)
                throw new InvalidDataException($"{FileName}: limits of the {r.SettingVolts} V row do not bracket its power.");
        if (!Rows.Any(r => SameValue(r.SettingVolts, CalibrationVolts)))
            throw new InvalidDataException($"{FileName}: the calibration voltage is not a row.");
    }

    private static bool SameValue(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(Math.Abs(a), Math.Abs(b));
}

/// <summary>Parses a power reading typed by the operator: "1.0003 mW", "999.8uW", "0.0010003", "1.0003e-3 W", "0.0 dBm".</summary>
public static partial class PowerText
{
    [GeneratedRegex(@"^\s*([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*(pW|nW|uW|µW|mW|W|dBm)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    /// <summary>Watts, or null if the text is not a power. A bare number is taken as watts.</summary>
    public static double? ParseWatts(string text)
    {
        var m = Pattern().Match(text);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "pw" => value * 1e-12,
            "nw" => value * 1e-9,
            "uw" or "µw" => value * 1e-6,
            "mw" => value * 1e-3,
            "dbm" => Math.Pow(10, value / 10) * 1e-3,
            _ => value,
        };
    }
}
