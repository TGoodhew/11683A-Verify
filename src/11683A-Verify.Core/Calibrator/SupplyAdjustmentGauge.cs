namespace Verify11683A.Calibrator;

public enum AdjustmentState
{
    /// <summary>Within a tenth of the tolerance of the target: leave A2R1 here.</summary>
    Centred,

    /// <summary>Inside the tolerance, but not at the centre yet.</summary>
    InTolerance,

    Low,
    High,
}

/// <summary>Where a 1 mW reading sits against the Power Supply Adjustment target (145.00 ± 2.00 mV, manual p. 8).</summary>
public sealed record AdjustmentReading(double Volts, double Target, double Tolerance)
{
    /// <summary>The fraction of the tolerance that counts as "centred".</summary>
    public const double CentredFraction = 0.1;

    public double Error => Volts - Target;

    public double ErrorPpm => Error / Target * 1e6;

    /// <summary>Position in the band: −1 at the low limit, 0 at the target, +1 at the high limit (unclamped).</summary>
    public double Position => Error / Tolerance;

    /// <summary>A reading exactly on a limit is in tolerance; this keeps 0.147 − 0.145 from rounding past 0.002.</summary>
    private const double Rounding = 1 + 1e-9;

    public AdjustmentState State => Math.Abs(Error) <= Tolerance * CentredFraction * Rounding
        ? AdjustmentState.Centred
        : Math.Abs(Error) <= Tolerance * Rounding
            ? AdjustmentState.InTolerance
            : Error < 0 ? AdjustmentState.Low : AdjustmentState.High;

    /// <summary>Which way to turn: "raise", "lower" or "leave".</summary>
    public string Advice => State switch
    {
        AdjustmentState.Centred => "leave A2R1 here",
        _ when Error < 0 => "raise the output",
        _ => "lower the output",
    };

    public static AdjustmentReading For(double volts, SupplyAdjustmentLimits limits) =>
        new(volts, limits.TargetVolts, limits.ToleranceVolts);
}
