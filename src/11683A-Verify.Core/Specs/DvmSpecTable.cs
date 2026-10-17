namespace Verify11683A.Specs;

/// <summary>Calibration-interval column of a DVM accuracy table.</summary>
public enum SpecPeriod
{
    Hours24,
    Days90,
    Year1,
    Year2,
}

public static class SpecPeriodExtensions
{
    /// <summary>The key used for the period in the data files.</summary>
    public static string Key(this SpecPeriod period) => period switch
    {
        SpecPeriod.Hours24 => "24h",
        SpecPeriod.Days90 => "90d",
        SpecPeriod.Year1 => "1y",
        SpecPeriod.Year2 => "2y",
        _ => throw new ArgumentOutOfRangeException(nameof(period)),
    };

    public static SpecPeriod Parse(string key)
    {
        foreach (var period in Enum.GetValues<SpecPeriod>())
            if (period.Key() == key)
                return period;
        throw new FormatException($"Unknown spec period '{key}'. Use 24h, 90d, 1y or 2y.");
    }
}

/// <summary>One "reading + range" pair, in the table's units (% or ppm).</summary>
public sealed record SpecTerm(double Reading, double Range);

public sealed class DvmSpecRange
{
    public double Range { get; init; }
    public Dictionary<string, SpecTerm> Accuracy { get; init; } = [];
    public SpecTerm? TempcoPerC { get; init; }
    public string? InputResistance { get; init; }
    public bool HighImpedanceAvailable { get; init; }
    public string? TestCurrent { get; init; }
}

/// <summary>An additional error term from a table footnote (e.g. "without MATH NULL add ...").</summary>
public sealed class SpecAdder
{
    public string Description { get; init; } = "";

    /// <summary>The range it applies to, or null for every range.</summary>
    public double? Range { get; init; }

    public SpecTerm Term { get; init; } = new(0, 0);

    /// <summary>Whether the app includes it. Data, so the choice is visible and reviewable.</summary>
    public bool Applied { get; init; }
}

public sealed class DvmSpecFunction
{
    public DvmFunction Function { get; init; }
    public List<DvmSpecRange> Ranges { get; init; } = [];
    public List<SpecAdder> Adders { get; init; } = [];
}

/// <summary>The breakdown of a spec limit for one reading, in measurement units (V or Ω).</summary>
public sealed record SpecLimit(
    DvmFunction Function,
    double Range,
    double Reading,
    SpecPeriod Period,
    double BaseError,
    double AdderError,
    double TemperatureError,
    IReadOnlyList<string> AppliedAdders)
{
    /// <summary>The total ± spec limit.</summary>
    public double Total => BaseError + AdderError + TemperatureError;

    /// <summary>The total as a fraction of the reading.</summary>
    public double Relative => Reading == 0 ? double.PositiveInfinity : Total / Math.Abs(Reading);
}

/// <summary>
/// A DVM accuracy table loaded from data/dvm-specs-&lt;model&gt;.json. Accuracy is
/// ±(reading term × |reading| + range term × range), in the table's units (% or ppm).
/// </summary>
public sealed class DvmSpecTable
{
    public string Model { get; init; } = "";
    public string Source { get; init; } = "";

    /// <summary>"%" or "ppm".</summary>
    public string Units { get; init; } = "%";

    /// <summary>Ambient band [low, high] in °C inside which no temperature coefficient is added.</summary>
    public double[] CalibrationBandC { get; init; } = [18.0, 28.0];

    public int WarmupMinutes { get; init; }
    public List<string> Notes { get; init; } = [];
    public List<DvmSpecFunction> Functions { get; init; } = [];

    /// <summary>Multiplier from the table's units to a plain fraction.</summary>
    public double UnitScale => Units switch
    {
        "%" => 1e-2,
        "ppm" => 1e-6,
        _ => throw new InvalidDataException($"{Model} spec table: unknown units '{Units}'."),
    };

    public static DvmSpecTable Load(string path)
    {
        var table = Json.Load<DvmSpecTable>(path);
        table.Validate();
        return table;
    }

    public static DvmSpecTable LoadForModel(string dvmModel, string? dataDirectory = null) =>
        Load(DataFiles.Resolve(DataFiles.SpecFileName(dvmModel), dataDirectory));

    public DvmSpecFunction For(DvmFunction function) =>
        Functions.FirstOrDefault(f => f.Function == function)
        ?? throw new KeyNotFoundException($"{Model} spec table has no {function} section.");

    public IReadOnlyList<double> RangesFor(DvmFunction function) =>
        For(function).Ranges.Select(r => r.Range).OrderBy(r => r).ToList();

    public DvmSpecRange RangeEntry(DvmFunction function, double range) =>
        For(function).Ranges.FirstOrDefault(r => SameRange(r.Range, range))
        ?? throw new KeyNotFoundException($"{Model} spec table has no {function} {range} range.");

    /// <summary>
    /// The ± spec limit for a reading on a fixed range. If an ambient temperature is given and it is
    /// outside the calibration band, the temperature coefficient is added for each degree outside it.
    /// </summary>
    public SpecLimit Limit(DvmFunction function, double range, double reading, SpecPeriod period, double? ambientC = null)
    {
        var section = For(function);
        var entry = RangeEntry(function, range);
        if (!entry.Accuracy.TryGetValue(period.Key(), out var term))
            throw new KeyNotFoundException($"{Model} spec table has no {period.Key()} column for {function} {range}.");

        var scale = UnitScale;
        var magnitude = Math.Abs(reading);
        double Apply(SpecTerm t) => (t.Reading * magnitude + t.Range * range) * scale;

        var baseError = Apply(term);

        var adders = section.Adders
            .Where(a => a.Applied && (a.Range is null || SameRange(a.Range.Value, range)))
            .ToList();
        var adderError = adders.Sum(a => Apply(a.Term));

        var temperatureError = 0.0;
        if (ambientC is { } t && entry.TempcoPerC is { } tempco)
        {
            var degreesOutside = Math.Max(0, CalibrationBandC[0] - t) + Math.Max(0, t - CalibrationBandC[1]);
            temperatureError = Apply(tempco) * degreesOutside;
        }

        return new SpecLimit(function, range, reading, period, baseError, adderError, temperatureError,
            adders.Select(a => a.Description).ToList());
    }

    private void Validate()
    {
        _ = UnitScale;
        if (CalibrationBandC is not { Length: 2 } || CalibrationBandC[0] > CalibrationBandC[1])
            throw new InvalidDataException($"{Model} spec table: calibrationBandC must be [low, high].");
        foreach (var f in Functions)
        foreach (var r in f.Ranges)
        foreach (var key in r.Accuracy.Keys)
            _ = SpecPeriodExtensions.Parse(key);
    }

    private static bool SameRange(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(Math.Abs(a), Math.Abs(b));
}
