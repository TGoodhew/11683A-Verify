using System.Security.Cryptography;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

namespace Verify11683A.Results;

/// <summary>The unit under test.</summary>
public sealed record UnitUnderTest(string Model, string Serial, string Options);

/// <summary>The DVM: what the report states about it, where it was on the bus, and its raw identity reply.</summary>
public sealed record DvmRecord(DvmDetails Details, string Resource, string IdentityReply, CalibrationState CalibrationState);

/// <summary>
/// The conditions of the run. Warm-up times are how long each instrument had been on when testing started;
/// <paramref name="WarmupCutShort"/> is set when the operator skipped the rest of the countdown.
/// </summary>
public sealed record TestConditions(
    double? AmbientC,
    double? WarmupMinutes,
    string? SimulationScenario,
    double? DvmWarmupMinutes = null,
    bool WarmupCutShort = false);

/// <summary>How the points were measured and evaluated.</summary>
public sealed record RunSettings(
    double Nplc,
    int ReadingsPerPoint,
    double LineFrequencyHz,
    double SettlingBandPpm,
    double SettlingHoldSeconds,
    double SettlingTimeoutSeconds,
    SpecPeriod SpecPeriod,
    UncertaintyConvention Uncertainty);

/// <summary>A data file the results depend on: what it says it is, and a hash of exactly what was loaded.</summary>
public sealed record DataFileVersion(string File, string Source, string Sha256)
{
    public static DataFileVersion Of(string path, string source)
    {
        using var stream = System.IO.File.OpenRead(path);
        return new DataFileVersion(Path.GetFileName(path), source, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }
}

/// <summary>
/// Everything a run produced, as saved to 11683A_&lt;serial&gt;_&lt;yyyyMMdd-HHmm&gt;.json: identities, conditions,
/// settings, the versions of the limits and spec table, and every raw reading. The PDF report and the export to
/// the power meter app are built from this alone.
/// </summary>
public sealed record VerificationRun
{
    public const string CurrentSchema = "11683A-Verify/results/1";

    public string Schema { get; init; } = CurrentSchema;
    public string AppVersion { get; init; } = "";
    public DateTimeOffset Started { get; init; }
    public DateTimeOffset Finished { get; init; }
    public string Company { get; init; } = "";
    public string Operator { get; init; } = "";
    public required UnitUnderTest Unit { get; init; }
    public required DvmRecord Dvm { get; init; }
    public required TestConditions Conditions { get; init; }
    public required RunSettings Settings { get; init; }
    public required DataFileVersion Limits { get; init; }
    public required DataFileVersion SpecTable { get; init; }
    public Part1Result? Part1 { get; init; }
    public Part2Result? Part2 { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Both parts run and every range in each measured.</summary>
    public bool Complete => Part1 is { Complete: true } && Part2 is { Complete: true };

    public Verdict Verdict => Verdicts.Combine(new[] { Part1?.Verdict, Part2?.Verdict }.OfType<Verdict>());
}
