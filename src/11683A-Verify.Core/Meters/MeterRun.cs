using System.Text.Json;
using Verify11683A.Results;
using Verify11683A.Uncertainty;

namespace Verify11683A.Meters;

/// <summary>One row as measured: the manual's row, and what was read.</summary>
public sealed record MeterPointResult(MeterTestPoint Point, ReadingSource Source, double? Reading, bool Simulated)
{
    /// <summary>Inside the manual's limits (allowing for the last digit of rounding). Null when the row was skipped.</summary>
    public bool? Pass => Reading is { } r
        ? r >= Point.Min - Slack && r <= Point.Max + Slack
        : null;

    private double Slack => 1e-9 * Math.Max(Math.Abs(Point.Min), Math.Abs(Point.Max));

    public Verdict Verdict => Pass switch { true => Verdict.Pass, false => Verdict.Fail, _ => Verdict.Info };
}

public sealed record MeterTestResult(string TestId, string Title, string Source, string Channel, IReadOnlyList<MeterPointResult> Points)
{
    public bool Complete => Points.All(p => p.Reading is not null);

    public Verdict Verdict => Verdicts.Combine(Points.Select(p => p.Verdict));
}

public sealed record MeterUnderTest(string Model, string Title, string Serial, string Identity, string Driver, DataFileVersion Definition);

/// <summary>A power meter performance test run, saved as &lt;model&gt;_&lt;serial&gt;_&lt;stamp&gt;.json beside a PDF record.</summary>
public sealed record MeterRun
{
    public const string CurrentSchema = "11683A-Verify/meter-results/1";

    public string Schema { get; init; } = CurrentSchema;
    public string AppVersion { get; init; } = "";
    public DateTimeOffset Started { get; init; }
    public DateTimeOffset Finished { get; init; }
    public string Company { get; init; } = "";
    public string Operator { get; init; } = "";
    public required MeterUnderTest Meter { get; init; }

    /// <summary>The range calibrator used: model, serial, options.</summary>
    public required UnitUnderTest Calibrator { get; init; }

    /// <summary>The DVM, when a test read the recorder output.</summary>
    public DvmRecord? Dvm { get; init; }

    public string? SimulationScenario { get; init; }
    public IReadOnlyList<MeterTestResult> Tests { get; init; } = [];
    public IReadOnlyList<UncoveredTest> NotRun { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool Complete => Tests.Count > 0 && Tests.All(t => t.Complete);

    public Verdict Verdict => Verdicts.Combine(Tests.Select(t => t.Verdict));

    public string BaseName()
    {
        var invalid = Path.GetInvalidFileNameChars();
        var serial = new string(Meter.Serial.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray());
        return $"{Meter.Model}_{(serial == "" ? "no-serial" : serial)}_{Started:yyyyMMdd-HHmm}";
    }

    public string Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, BaseName() + ".json");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(directory, $"{BaseName()}-{n}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options));
        return path;
    }

    public static MeterRun Load(string path)
    {
        var run = Json.Load<MeterRun>(path);
        if (run.Schema != CurrentSchema)
            throw new InvalidDataException($"Unsupported meter results schema '{run.Schema}'.");
        return run;
    }
}
