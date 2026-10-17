using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

namespace Verify11683A.Results;

/// <summary>One Part 1 range: its voltage, and its ratio to the 100 mW reading.</summary>
public sealed record Part1PointResult(
    string RangeId,
    MeasuredPoint Measurement,
    MeasurementUncertainty Uncertainty,
    RatioUncertainty? Ratio,
    Assessment? RatioAssessment,
    Assessment? VoltsAssessment)
{
    public double Volts => Uncertainty.Value;

    public Verdict Verdict => Verdicts.Combine(new[] { RatioAssessment, VoltsAssessment }
        .Where(a => a is not null).Select(a => a!.Verdict).DefaultIfEmpty(Verdict.Info));
}

/// <summary>
/// Part 1, the voltage and ratio test (11683A manual, Range Switch Performance Test, p. 7, PDF p. 18). Ranges may be
/// measured in any order; ratios are V(100 mW)/V(range) and need the 100 mW reading.
/// </summary>
public sealed record Part1Result(
    string Source,
    string ReferenceRange,
    IReadOnlyList<Part1PointResult> Points,
    IReadOnlyList<string> Missing,
    bool SupplyAdjustmentRecommended,
    IReadOnlyList<string> Notes)
{
    public bool Complete => Missing.Count == 0;

    public Verdict Verdict => Verdicts.Combine(Points.Select(p => p.Verdict));

    public Part1PointResult? Point(string rangeId) => Points.FirstOrDefault(p => p.RangeId == rangeId);
}

public static class Part1Evaluator
{
    public static Part1Result Evaluate(
        CalibratorLimits limits,
        IReadOnlyCollection<MeasuredPoint> measured,
        DvmSpecTable specs,
        SpecPeriod period,
        double? ambientC,
        UncertaintyConvention convention)
    {
        var part = limits.Part1;
        var byRange = measured.ToDictionary(m => m.RangeId);
        var notes = new List<string>();

        MeasurementUncertainty UncertaintyOf(MeasuredPoint m) =>
            MeasurementUncertainty.Of(specs.Limit(DvmFunction.DcVoltage, m.DvmRange, m.Statistics.Mean, period, ambientC), m.Statistics, convention);

        var reference = byRange.TryGetValue(part.ReferenceRange, out var r) ? UncertaintyOf(r) : null;

        var points = new List<Part1PointResult>();
        foreach (var limit in part.Points)
        {
            if (!byRange.TryGetValue(limit.Range, out var m))
                continue;
            var u = UncertaintyOf(m);

            RatioUncertainty? ratio = null;
            Assessment? ratioCheck = null;
            if (limit.Range != part.ReferenceRange && reference is not null)
            {
                ratio = RatioUncertainty.Of(reference, u, convention);
                ratioCheck = Assessment.Of(ratio.Value, ratio.Expanded, new Tolerance(limit.RatioMin, limit.RatioMax), convention);
            }

            Assessment? voltsCheck = null;
            if (limit.VoltsMin is { } lo && limit.VoltsMax is { } hi)
                voltsCheck = Assessment.Of(u.Value, u.Expanded, new Tolerance(lo, hi), convention);

            points.Add(new Part1PointResult(limit.Range, m, u, ratio, ratioCheck, voltsCheck));
        }

        var missing = part.Points.Select(p => p.Range).Where(id => !byRange.ContainsKey(id)).ToList();
        if (reference is null && points.Count > 0)
            notes.Add($"{RangeSwitch.Get(part.ReferenceRange)} was not measured, so no ratios could be computed.");

        if (reference is not null)
        {
            if (reference.Value <= 0)
                notes.Add($"The {RangeSwitch.Get(part.ReferenceRange)} reading is not positive: check POLARITY is NORMAL.");
            var refPoint = byRange[part.ReferenceRange];
            if (!refPoint.InputResistance.Contains("GΩ", StringComparison.Ordinal))
                notes.Add($"The {RangeSwitch.Get(part.ReferenceRange)} reading (about {Si.Format(reference.Value, "V", 3)}) was taken on the "
                    + $"{Si.Range(refPoint.DvmRange, "V")} range, whose input is {refPoint.InputResistance}: the same as the manual's HP 3455A.");
        }

        var supply = limits.SupplyAdjustment;
        var anchor = points.FirstOrDefault(p => p.RangeId == supply.Range)?.VoltsAssessment;
        var recommend = anchor?.InsideLimits == false;
        if (recommend)
            notes.Add($"{RangeSwitch.Get(supply.Range)} reads {Si.Format(anchor!.Value, "V", 6)}, outside "
                + $"{Si.Format(anchor.Tolerance!.Min, "V", 5)}–{Si.Format(anchor.Tolerance.Max, "V", 5)}: perform the {supply.Title} "
                + $"(adjust {supply.Adjustment} for {Si.Format(supply.TargetVolts, "V", 5)}), then repeat Part 1.");
        else if (anchor is { Verdict: Verdict.Indeterminate })
            notes.Add($"{RangeSwitch.Get(supply.Range)} is inside its limits but within U of one; the {supply.Title} "
                + $"({supply.Adjustment}) to {Si.Format(supply.TargetVolts, "V", 5)} would add margin.");

        return new Part1Result(part.Source, part.ReferenceRange, points, missing, recommend, notes);
    }
}
