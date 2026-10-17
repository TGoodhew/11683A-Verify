using Verify11683A.Calibrator;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

namespace Verify11683A.Results;

/// <summary>A DCV reading confirming the output was grounded (FUNCTION in STANDBY) before ohms current was applied.</summary>
public sealed record OhmsSafetyRecord(DateTimeOffset Time, double Volts, bool Overload, double ThresholdVolts, bool Safe);

/// <summary>One Part 2 range: its four-wire resistance with FUNCTION in STANDBY.</summary>
public sealed record Part2PointResult(
    string RangeId,
    MeasuredPoint Measurement,
    MeasurementUncertainty Uncertainty,
    Assessment Assessment)
{
    public double Ohms => Uncertainty.Value;

    public Verdict Verdict => Assessment.Verdict;
}

/// <summary>Part 2, the four-wire resistance test (11683A manual, Range Switch Performance Test, p. 8, PDF p. 19).</summary>
public sealed record Part2Result(
    string Source,
    IReadOnlyList<Part2PointResult> Points,
    IReadOnlyList<string> Missing,
    IReadOnlyList<OhmsSafetyRecord> SafetyChecks,
    IReadOnlyList<string> Notes)
{
    public bool Complete => Missing.Count == 0;

    public Verdict Verdict => Verdicts.Combine(Points.Select(p => p.Verdict));

    public Part2PointResult? Point(string rangeId) => Points.FirstOrDefault(p => p.RangeId == rangeId);
}

public static class Part2Evaluator
{
    public static Part2Result Evaluate(
        CalibratorLimits limits,
        IReadOnlyCollection<MeasuredPoint> measured,
        IReadOnlyList<OhmsSafetyRecord> safetyChecks,
        DvmSpecTable specs,
        SpecPeriod period,
        double? ambientC,
        UncertaintyConvention convention)
    {
        var part = limits.Part2;
        var byRange = measured.ToDictionary(m => m.RangeId);

        var points = new List<Part2PointResult>();
        foreach (var limit in part.Points)
        {
            if (!byRange.TryGetValue(limit.Range, out var m))
                continue;
            var spec = specs.Limit(DvmFunction.FourWireResistance, m.DvmRange, m.Statistics.Mean, period, ambientC);
            var u = MeasurementUncertainty.Of(spec, m.Statistics, convention);
            var assessment = Assessment.Of(u.Value, u.Expanded, new Tolerance(limit.OhmsMin, limit.OhmsMax), convention);
            points.Add(new Part2PointResult(limit.Range, m, u, assessment));
        }

        var missing = part.Points.Select(p => p.Range).Where(id => !byRange.ContainsKey(id)).ToList();
        var notes = new List<string>();
        if (points.Count > 0 && !safetyChecks.Any(c => c.Safe))
            notes.Add("No safety check confirmed the output was grounded before these readings.");
        return new Part2Result(part.Source, points, missing, safetyChecks, notes);
    }
}
