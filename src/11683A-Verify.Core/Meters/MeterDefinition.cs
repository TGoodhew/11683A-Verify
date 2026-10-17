using System.Globalization;
using Verify11683A.Calibrator;

namespace Verify11683A.Meters;

/// <summary>Where a test point's reading comes from.</summary>
public enum ReadingSource
{
    /// <summary>The power meter itself: over the bus if it has a driver, otherwise typed in by the operator.</summary>
    Meter,

    /// <summary>The DVM on the power meter's rear-panel RECORDER OUTPUT (435A/435B: 1.000 V = full scale).</summary>
    Recorder,

    /// <summary>Always typed in by the operator (e.g. a dBm or dB REL display the bus driver does not read).</summary>
    Operator,
}

/// <summary>The 11683A's switches for a test point. Null on a point means the 11683A is not connected (e.g. a power sensor instead).</summary>
public sealed record CalibratorSetting
{
    public string Range { get; init; } = "1mW";
    public FunctionSwitch Function { get; init; } = FunctionSwitch.Calibrate;
    public PolaritySwitch Polarity { get; init; } = PolaritySwitch.Normal;
    public ReferenceSwitch Reference { get; init; } = ReferenceSwitch.Int;

    /// <summary>Option H01 with REF EXT: the DC calibrator voltage on REF EXT.</summary>
    public double? ExternalVolts { get; init; }

    public override string ToString() =>
        $"RANGE {RangeSwitch.Get(Range).Label}, FUNCTION {Function.ToString().ToUpperInvariant()}, POLARITY {Polarity.ToString().ToUpperInvariant()}"
        + (Reference == ReferenceSwitch.Ext ? $", REF EXT, DC calibrator {(ExternalVolts ?? 0).ToString("G5", CultureInfo.InvariantCulture)} V" : "");
}

/// <summary>One row of a manual's performance test table.</summary>
public sealed record MeterTestPoint
{
    /// <summary>What the row is called in the manual's table, e.g. "3 µW", "RNG 2", "CAL FACTOR 95%".</summary>
    public string Label { get; init; } = "";

    public CalibratorSetting? Calibrator { get; init; }

    /// <summary>What to set on the power meter for this row, as the operator would.</summary>
    public string MeterSetting { get; init; } = "";

    /// <summary>The meter's RANGE switch position for this row, where it matters to the recorder output (435A/B).</summary>
    public string? MeterRange { get; init; }

    /// <summary>The meter's CAL FACTOR setting for this row, when the test steps it.</summary>
    public double? CalFactorPercent { get; init; }

    /// <summary>The expected (nominal) reading. Where the manual prints only limits it is noted in the file.</summary>
    public double Expected { get; init; }

    /// <summary>W, mW, uW, nW, dBm, dB, mV or V.</summary>
    public string Unit { get; init; } = "";

    public double Min { get; init; }
    public double Max { get; init; }

    /// <summary>Overrides the test's reading source for this row.</summary>
    public ReadingSource? Reading { get; init; }

    public string? Note { get; init; }
}

public sealed record MeterTest
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public ReadingSource Reading { get; init; } = ReadingSource.Meter;

    /// <summary>Run once per channel of a multi-channel meter.</summary>
    public bool PerChannel { get; init; }

    public List<string> Equipment { get; init; } = [];
    public List<string> Setup { get; init; } = [];
    public List<MeterTestPoint> Points { get; init; } = [];
    public List<string> Notes { get; init; } = [];

    /// <summary>True if any point uses the 11683A's Option H01 REF EXT input.</summary>
    public bool NeedsH01 => Points.Any(p => p.Calibrator?.Reference == ReferenceSwitch.Ext);

    public ReadingSource SourceOf(MeterTestPoint point) => point.Reading ?? Reading;
}

/// <summary>A performance test in the manual that this app does not run, and what it needs.</summary>
public sealed record UncoveredTest(string Title, string Source, IReadOnlyList<string> Equipment);

/// <summary>
/// A power meter and its performance tests, transcribed from its manual into data/meters/&lt;model&gt;.json. The app
/// loads the one the operator selects and runs its tests with the 11683A.
/// </summary>
public sealed class MeterDefinition
{
    public const string DirectoryName = "meters";

    public string Model { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Title { get; init; } = "";
    public string Manual { get; init; } = "";

    /// <summary>The bus driver: E4418B, 437B, 438A or 436A. Null when the meter has no bus.</summary>
    public string? BusDriver { get; init; }

    public string DefaultResource { get; init; } = "GPIB0::13::INSTR";
    public List<string> Channels { get; init; } = ["A"];
    public int WarmupMinutes { get; init; }
    public List<string> Notes { get; init; } = [];
    public List<MeterTest> Tests { get; init; } = [];
    public List<UncoveredTest> NotCovered { get; init; } = [];

    /// <summary>Differences between editions, OCR doubts and choices made in transcribing the manual.</summary>
    public List<string> Ambiguities { get; init; } = [];

    /// <summary>The file it came from, set on load.</summary>
    public string FileName { get; private set; } = "";

    public static string DefaultDirectory => Path.Combine(DataFiles.DefaultDirectory, DirectoryName);

    public static MeterDefinition Load(string path)
    {
        var definition = Json.Load<MeterDefinition>(path);
        definition.FileName = Path.GetFileName(path);
        definition.Validate();
        return definition;
    }

    /// <summary>Every definition in data/meters, by model.</summary>
    public static IReadOnlyList<MeterDefinition> LoadAll(string? directory = null) =>
        Directory.GetFiles(directory ?? DefaultDirectory, "*.json").Order().Select(Load).OrderBy(d => d.Model, StringComparer.Ordinal).ToList();

    public static MeterDefinition LoadModel(string model, string? directory = null) =>
        LoadAll(directory).FirstOrDefault(d => string.Equals(d.Model, model, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"No meter definition for '{model}' in {directory ?? DefaultDirectory}.");

    private void Validate()
    {
        if (Model == "" || Tests.Count == 0)
            throw new InvalidDataException($"{FileName}: a meter needs a model and at least one test.");
        if (Tests.Select(t => t.Id).Distinct().Count() != Tests.Count)
            throw new InvalidDataException($"{FileName}: test ids must be unique.");
        foreach (var test in Tests)
        foreach (var p in test.Points)
        {
            var where = $"{FileName} {test.Id} '{p.Label}'";
            if (!MeterUnits.IsKnown(p.Unit))
                throw new InvalidDataException($"{where}: unknown unit '{p.Unit}'.");
            if (p.Min > p.Max)
                throw new InvalidDataException($"{where}: min > max.");
            if (p.Calibrator is { } c)
                _ = RangeSwitch.Get(c.Range);
            if (p.MeterRange is { } r)
                _ = RangeSwitch.Get(r);
            var source = test.SourceOf(p);
            if (source == ReadingSource.Recorder && !MeterUnits.IsVolts(p.Unit))
                throw new InvalidDataException($"{where}: a recorder-output reading must be in mV or V.");
            if (source == ReadingSource.Meter && !MeterUnits.FromWatts(p.Unit, 1e-3).HasValue)
                throw new InvalidDataException($"{where}: a meter reading in '{p.Unit}' cannot come from the bus; mark it \"reading\": \"Operator\".");
        }
    }
}

/// <summary>Units used in the meter definitions, and conversions from what the instruments report.</summary>
public static class MeterUnits
{
    private static readonly Dictionary<string, double> PowerScale = new(StringComparer.Ordinal)
    {
        ["W"] = 1, ["mW"] = 1e-3, ["uW"] = 1e-6, ["nW"] = 1e-9,
    };

    private static readonly Dictionary<string, double> VoltScale = new(StringComparer.Ordinal)
    {
        ["V"] = 1, ["mV"] = 1e-3,
    };

    public static bool IsKnown(string unit) => PowerScale.ContainsKey(unit) || VoltScale.ContainsKey(unit) || unit is "dBm" or "dB";

    public static bool IsVolts(string unit) => VoltScale.ContainsKey(unit);

    /// <summary>A power in watts expressed in <paramref name="unit"/>, or null if it cannot be (dB is relative).</summary>
    public static double? FromWatts(string unit, double watts) =>
        PowerScale.TryGetValue(unit, out var scale) ? watts / scale
        : unit == "dBm" ? 10 * Math.Log10(watts / 1e-3)
        : null;

    public static double FromVolts(string unit, double volts) => volts / VoltScale[unit];

    /// <summary>The unit as printed: µW rather than uW.</summary>
    public static string Display(string unit) => unit.Replace("uW", "µW", StringComparison.Ordinal);
}
