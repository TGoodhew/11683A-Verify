using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Verify11683A.Calibrator;
using Verify11683A.Uncertainty;

namespace Verify11683A.Results;

/// <summary>
/// One measured quantity of one range. <see cref="Nominal"/> is the middle of the manual's limits and
/// <see cref="Deviation"/> is value / nominal − 1; all three limit fields are NaN where the manual gives no limit
/// (Part 1 voltages other than 1 mW).
/// </summary>
public sealed record ActualValue(
    string Range,
    double NominalWatts,
    string Quantity,
    string Unit,
    double Value,
    double ExpandedUncertainty,
    double Nominal,
    double Deviation,
    double LimitMin,
    double LimitMax);

/// <summary>
/// The 11683A's measured output, for the power meter performance-test app: 11683A-actuals.json. Documented in
/// docs/actuals-format.md. Only a passing run is exported.
/// </summary>
public sealed record CalibratorActuals
{
    public const string CurrentFormat = "11683A-actuals/1";
    public const string FileName = "11683A-actuals.json";

    public string Format { get; init; } = CurrentFormat;
    public required UnitUnderTest Unit { get; init; }
    public DateTimeOffset Measured { get; init; }
    public string Source { get; init; } = "";
    public string SourceSha256 { get; init; } = "";
    public required DvmRecord Dvm { get; init; }
    public double CoverageFactor { get; init; }

    /// <summary>True when the source run was simulated: a consumer must not use these values.</summary>
    public bool Simulated { get; init; }

    /// <summary>"V(100 mW) / V(range)": each Part 1 range's DC REFERENCE OUTPUT voltage ratio.</summary>
    public IReadOnlyList<ActualValue> Ratios { get; init; } = [];

    /// <summary>Part 1 DC REFERENCE OUTPUT voltages, FUNCTION in CALIBRATE.</summary>
    public IReadOnlyList<ActualValue> Voltages { get; init; } = [];

    /// <summary>Part 2 four-wire resistances at DC REFERENCE OUTPUT, FUNCTION in STANDBY.</summary>
    public IReadOnlyList<ActualValue> Resistances { get; init; } = [];

    /// <summary>Why a run cannot be exported, or null if it can: Part 1 complete and passing, Part 2 passing if run.</summary>
    public static string? WhyNotExportable(VerificationRun run)
    {
        if (run.Part1 is not { } p1)
            return "Part 1 was not run.";
        if (!p1.Complete)
            return $"Part 1 is incomplete (missing {string.Join(", ", p1.Missing.Select(r => RangeSwitch.Get(r).Label))}).";
        if (p1.Verdict != Verdict.Pass)
            return $"Part 1 did not pass ({p1.Verdict}).";
        if (run.Part2 is { } p2 && (!p2.Complete || p2.Verdict != Verdict.Pass))
            return p2.Complete ? $"Part 2 did not pass ({p2.Verdict})." : "Part 2 is incomplete.";
        return null;
    }

    /// <param name="sourceJson">The results file's text, hashed so the export can be traced to it.</param>
    public static CalibratorActuals From(VerificationRun run, string sourceFile, string sourceJson)
    {
        if (WhyNotExportable(run) is { } reason)
            throw new InvalidOperationException($"Not exported: {reason}");
        var p1 = run.Part1!;

        var ratios = p1.Points.Where(p => p.RatioAssessment is not null).Select(p =>
        {
            var a = p.RatioAssessment!;
            return Value(p.RangeId, "Ratio", "", a.Value, a.ExpandedUncertainty, a.Tolerance!.Mid, a.Tolerance);
        }).ToList();

        var voltages = p1.Points.Select(p =>
        {
            var tolerance = p.VoltsAssessment?.Tolerance;
            return Value(p.RangeId, "DcVoltage", "V", p.Volts, p.Uncertainty.Expanded, tolerance?.Mid ?? double.NaN, tolerance);
        }).ToList();

        var resistances = run.Part2?.Points.Select(p =>
            Value(p.RangeId, "FourWireResistance", "Ω", p.Ohms, p.Assessment.ExpandedUncertainty, p.Assessment.Tolerance!.Mid, p.Assessment.Tolerance)).ToList() ?? [];

        return new CalibratorActuals
        {
            Unit = run.Unit,
            Measured = run.Started,
            Source = sourceFile,
            SourceSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceJson))),
            Dvm = run.Dvm,
            CoverageFactor = run.Settings.Uncertainty.CoverageFactor,
            Simulated = run.Conditions.SimulationScenario is not null,
            Ratios = ratios,
            Voltages = voltages,
            Resistances = resistances,
        };
    }

    /// <summary>Writes 11683A-actuals.json in <paramref name="directory"/>, replacing the previous one.</summary>
    public string Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options));
        return path;
    }

    public static CalibratorActuals Load(string path)
    {
        var actuals = Json.Load<CalibratorActuals>(path);
        if (actuals.Format != CurrentFormat)
            throw new InvalidDataException($"Unsupported actuals format '{actuals.Format}'.");
        return actuals;
    }

    private static ActualValue Value(string rangeId, string quantity, string unit, double value, double expanded, double nominal, Tolerance? tolerance) =>
        new(rangeId, RangeSwitch.Get(rangeId).NominalWatts, quantity, unit, value, expanded, nominal, value / nominal - 1,
            tolerance?.Min ?? double.NaN, tolerance?.Max ?? double.NaN);
}
