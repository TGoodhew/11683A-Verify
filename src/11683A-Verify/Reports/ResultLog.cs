using System.Globalization;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.Measurement;
using Verify11683A.Meters;
using Verify11683A.Results;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

namespace Verify11683A.App.Reports;

/// <summary>
/// The session log's record of each result, so the log holds what the console showed: the mean, σ, n, the DVM
/// spec, U (k), the manual's limits and the verdict.
/// </summary>
public static class ResultLog
{
    /// <summary>The dvm-check reading of the 11683A at RANGE 1 mW.</summary>
    public static string OneMilliwatt(ReadingStatistics stats, MeasurementUncertainty u, Assessment assessment, string dvmModel, string limitsSource, bool settled) =>
        $"1 mW result: {Reading(stats, "V")}{(settled ? "" : " (not settled)")}; {Spec(u, dvmModel, "V")}; {Expanded(u, "V")}; "
        + $"{Limits(assessment, "V", 5)} ({limitsSource}); {Verdict(assessment)}";

    /// <summary>One line per range, then the overall result.</summary>
    public static IEnumerable<string> Part1(Part1Result result, string dvmModel)
    {
        foreach (var p in result.Points)
        {
            var line = $"Part 1 result {p.RangeId}: {Reading(p.Measurement, "V")}; {Spec(p.Uncertainty, dvmModel, "V")}; {Expanded(p.Uncertainty, "V")}";
            if (p.VoltsAssessment is { } v)
                line += $"; volts {Limits(v, "V", 5)}, {Verdict(v)}";
            if (p.Ratio is { } r && p.RatioAssessment is { } ra)
                line += $"; ratio {Number(r.Value, 7)}, U (k = {Number(r.CoverageFactor, 2)}) ±{Number(r.Expanded, 3)}, "
                    + $"limits {Number(ra.Tolerance!.Min, 6)} to {Number(ra.Tolerance.Max, 6)}, {Verdict(ra)}";
            yield return line + $"; {Upper(p.Verdict)}";
        }
        yield return $"Part 1 overall: {Upper(result.Verdict)}, {Coverage(result.Points.Count, result.Missing)}"
            + (result.SupplyAdjustmentRecommended ? "; Power Supply Adjustment (A2R1) recommended" : "")
            + $" ({result.Source})";
    }

    public static IEnumerable<string> Part2(Part2Result result, string dvmModel)
    {
        foreach (var p in result.Points)
            yield return $"Part 2 result {p.RangeId}: {Reading(p.Measurement, "Ω")}; {Spec(p.Uncertainty, dvmModel, "Ω")}; {Expanded(p.Uncertainty, "Ω")}; "
                + $"{Limits(p.Assessment, "Ω", 6)}; {Verdict(p.Assessment)}";
        yield return $"Part 2 overall: {Upper(result.Verdict)}, {Coverage(result.Points.Count, result.Missing)}; "
            + $"{result.SafetyChecks.Count} ohms safety check(s), {result.SafetyChecks.Count(c => c.Safe)} safe ({result.Source})";
    }

    public static string Part4(Part4Result result)
    {
        var line = "Part 4 result (indication only): power meter " + result.PowerMeter;
        if (result.Transfer is { } t)
            line += $"; EXT/INT agreement {Percent(t.Agreement)} (raw {Percent(t.RawAgreement)}), tolerance ±{Percent(t.Tolerance, signed: false)}: "
                + (t.WithinTolerance ? "within" : "outside");
        if (result.TablePoints.Count > 0)
            line += $"; Table 2-4 {result.TablePoints.Count(p => p.WithinLimits)} of {result.TablePoints.Count} rows within";
        return line + $"; {(result.AllWithin ? "all within" : "not all within")} ({result.Source})";
    }

    /// <summary>One line per test and channel, then the overall result. Each row is logged as it is read.</summary>
    public static IEnumerable<string> Meter(MeterRun run)
    {
        foreach (var t in run.Tests)
            yield return $"Meter test {t.TestId} channel {t.Channel}: {Upper(t.Verdict)}, {t.Points.Count(p => p.Pass == true)} of {t.Points.Count} rows "
                + $"within the manual's limits, {t.Points.Count(p => p.Pass == false)} outside, {t.Points.Count(p => p.Reading is null)} skipped ({t.Source})";
        yield return $"Meter run {run.Meter.Model} s/n {run.Meter.Serial}: {Upper(run.Verdict)}, {(run.Complete ? "complete" : "incomplete")}";
    }

    /// <summary>The run as saved: the verdict that goes in the report.</summary>
    public static string Run(VerificationRun run) =>
        $"Run verdict: {Upper(run.Verdict)}, {(run.Complete ? "complete" : "incomplete")}"
        + $" (Part 1 {(run.Part1 is { } p1 ? Upper(p1.Verdict) : "not run")}, Part 2 {(run.Part2 is { } p2 ? Upper(p2.Verdict) : "not run")}"
        + (run.Part4 is { } p4 ? $", Part 4 {(p4.AllWithin ? "within" : "not all within")} (indication)" : "") + ")";

    private static string Reading(MeasuredPoint m, string unit) =>
        Reading(m.Statistics, unit) + $" on the {Si.Range(m.DvmRange, unit)} range{(m.Settled ? "" : " (not settled)")}";

    private static string Reading(ReadingStatistics s, string unit) =>
        $"mean {Si.Format(s.Mean, unit, 8)}, σ {Si.Format(s.StandardDeviation, unit, 3)}"
        + (s.Mean == 0 ? "" : $" ({Si.Ppm(s.StandardDeviation / Math.Abs(s.Mean))})") + $", n {s.Count}";

    private static string Spec(MeasurementUncertainty u, string dvmModel, string unit) =>
        $"{dvmModel} {u.Spec.Period.Key()} spec ±{Si.Format(u.Spec.Total, unit, 3)} ({Si.Ppm(u.Spec.Relative)})"
        + (u.Spec.TemperatureError > 0 ? $" incl. ±{Si.Format(u.Spec.TemperatureError, unit, 2)} temperature" : "");

    private static string Expanded(MeasurementUncertainty u, string unit) =>
        $"U (k = {Number(u.CoverageFactor, 2)}) ±{Si.Format(u.Expanded, unit, 3)}"
        + (u.Value == 0 ? "" : $" ({Si.Ppm(u.Expanded / Math.Abs(u.Value))})");

    private static string Limits(Assessment a, string unit, int digits) => a.Tolerance is { } t
        ? $"manual limits {Si.Format(t.Min, unit, digits)} to {Si.Format(t.Max, unit, digits)}"
        : "no manual limits";

    private static string Verdict(Assessment a) =>
        "TUR " + (double.IsNaN(a.Tur) ? "n/a" : double.IsPositiveInfinity(a.Tur) ? "∞" : Number(Math.Round(a.Tur, 1), 6) + ":1")
        + (a.LowTur ? " (low)" : "") + $", verdict {Upper(a.Verdict)}";

    private static string Coverage(int measured, IReadOnlyList<string> missing) =>
        missing.Count == 0
            ? $"all {measured} ranges measured"
            : $"{measured} ranges measured, not measured: {string.Join(", ", missing.Select(id => RangeSwitch.Get(id).Label))}";

    private static string Upper(Verdict verdict) => verdict.ToString().ToUpperInvariant();

    private static string Number(double value, int significantDigits) =>
        value.ToString("G" + significantDigits, CultureInfo.InvariantCulture);

    private static string Percent(double fraction, bool signed = true) =>
        (fraction * 100).ToString(signed ? "+0.000;-0.000;0.000" : "0.0##", CultureInfo.InvariantCulture) + "%";
}
