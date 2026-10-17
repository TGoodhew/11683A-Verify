using Verify11683A.Measurement;
using Verify11683A.Specs;

namespace Verify11683A.Uncertainty;

/// <summary>How the two readings of a ratio combine.</summary>
public enum RatioCombination
{
    /// <summary>Root-sum-square of the relative standard uncertainties (independent errors).</summary>
    Rss,

    /// <summary>Their sum: worst case, as if the errors were fully correlated in the unfavourable direction.</summary>
    Linear,
}

/// <summary>
/// The uncertainty convention, stated in the README and the report. A DVM spec limit is treated as a rectangular
/// distribution (u = spec/√3), combined by RSS with the Type A term s/√N, and expanded with k = 2.
/// </summary>
public sealed record UncertaintyConvention
{
    public double CoverageFactor { get; init; } = 2;

    public RatioCombination RatioCombination { get; init; } = RatioCombination.Rss;

    /// <summary>Points with a test uncertainty ratio below this are flagged.</summary>
    public double MinimumTur { get; init; } = 4;

    public static readonly UncertaintyConvention Default = new();

    /// <summary>Standard uncertainty of a ± limit treated as rectangular.</summary>
    public static double Rectangular(double halfWidth) => halfWidth / Math.Sqrt(3);
}

/// <summary>The uncertainty budget of one averaged reading, in V or Ω.</summary>
public sealed record MeasurementUncertainty(
    double Value,
    SpecLimit Spec,
    double TypeB,
    double TypeA,
    double Combined,
    double CoverageFactor)
{
    /// <summary>U = k × u_c.</summary>
    public double Expanded => CoverageFactor * Combined;

    public double RelativeCombined => Combined / Math.Abs(Value);

    /// <summary>
    /// u_B = spec/√3 at the mean, on the range the readings were taken; u_A = s/√N; u_c = √(u_B² + u_A²).
    /// </summary>
    public static MeasurementUncertainty Of(SpecLimit spec, ReadingStatistics stats, UncertaintyConvention convention)
    {
        var typeB = UncertaintyConvention.Rectangular(spec.Total);
        var typeA = stats.Count > 1 ? stats.StandardError : 0;
        return new MeasurementUncertainty(stats.Mean, spec, typeB, typeA, Math.Sqrt(typeB * typeB + typeA * typeA), convention.CoverageFactor);
    }
}

/// <summary>The uncertainty of a ratio of two readings, N/D, from their relative standard uncertainties.</summary>
public sealed record RatioUncertainty(double Value, double RelativeCombined, double CoverageFactor, RatioCombination Combination)
{
    public double Combined => Math.Abs(Value) * RelativeCombined;

    public double Expanded => CoverageFactor * Combined;

    public static RatioUncertainty Of(MeasurementUncertainty numerator, MeasurementUncertainty denominator, UncertaintyConvention convention)
    {
        var a = numerator.RelativeCombined;
        var b = denominator.RelativeCombined;
        var relative = convention.RatioCombination == RatioCombination.Rss ? Math.Sqrt(a * a + b * b) : a + b;
        return new RatioUncertainty(numerator.Value / denominator.Value, relative, convention.CoverageFactor, convention.RatioCombination);
    }
}

/// <summary>A test limit [Min, Max].</summary>
public sealed record Tolerance(double Min, double Max)
{
    public double Mid => (Min + Max) / 2;

    public double HalfWidth => (Max - Min) / 2;
}

public enum Verdict
{
    /// <summary>The value ± U is entirely inside the limits.</summary>
    Pass,

    /// <summary>The value ± U is entirely outside the limits.</summary>
    Fail,

    /// <summary>The value ± U straddles a limit.</summary>
    Indeterminate,

    /// <summary>No limit applies; recorded for information.</summary>
    Info,
}

/// <summary>A value checked against its limits with the guard band of its expanded uncertainty.</summary>
public sealed record Assessment(double Value, double ExpandedUncertainty, Tolerance? Tolerance, double Tur, Verdict Verdict, bool LowTur)
{
    /// <summary>Simple acceptance, ignoring U: inside the limits or not. Null when there are no limits.</summary>
    public bool? InsideLimits => Tolerance is { } t ? Value >= t.Min && Value <= t.Max : null;

    public static Assessment Of(double value, double expandedUncertainty, Tolerance? tolerance, UncertaintyConvention convention)
    {
        if (tolerance is null)
            return new Assessment(value, expandedUncertainty, null, double.NaN, Verdict.Info, false);

        var tur = expandedUncertainty > 0 ? tolerance.HalfWidth / expandedUncertainty : double.PositiveInfinity;
        var low = value - expandedUncertainty;
        var high = value + expandedUncertainty;
        var verdict = low >= tolerance.Min && high <= tolerance.Max
            ? Verdict.Pass
            : high < tolerance.Min || low > tolerance.Max
                ? Verdict.Fail
                : Verdict.Indeterminate;
        return new Assessment(value, expandedUncertainty, tolerance, tur, verdict, tur < convention.MinimumTur);
    }
}
