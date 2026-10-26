namespace Verify11683A.Instruments.DcSource;

/// <summary>One step of a trim: what the calibrator was set to, and what the DVM read at the tee.</summary>
public readonly record struct TrimStep(double Setting, double Tee);

/// <summary>How a trim ended: the last setting and tee reading, and whether it got inside the band.</summary>
public sealed record TrimResult(double Target, IReadOnlyList<TrimStep> Steps, bool Within, bool Gross)
{
    public double Setting => Steps[^1].Setting;

    public double Tee => Steps[^1].Tee;

    public double Error => Tee - Target;
}

/// <summary>
/// Sets a DC calibrator so a DVM on a BNC tee at the 11683A REF EXT reads the target: set, read the tee, correct the
/// setting by the error, up to <see cref="MaxTrims"/> times. That takes out what the lead drops into the REF EXT load,
/// the thermal EMFs along it, the DIVIDER's ~500 Ω source, and the calibrator's own offset.
/// </summary>
public static class TeeTrim
{
    /// <summary>Close enough: within this of the target, or <see cref="FloorVolts"/>.</summary>
    public const double BandPpm = 20;

    /// <summary>At the lowest REF EXT voltages the DVM's own noise is tens of ppm.</summary>
    public const double FloorVolts = 1e-6;

    /// <summary>How many times to set it, read the tee and correct, before taking what it gives.</summary>
    public const int MaxTrims = 4;

    /// <summary>
    /// Further off than 2% plus this is not the lead. 50 µV leaves room for the ~20 µV the 458.31 µV row looked to be
    /// short on the bench (2026-10-21), while a tee that reads nothing at that row is still 458 µV off.
    /// </summary>
    public const double GrossFloorVolts = 50e-6;

    public static double Band(double target) => Math.Max(BandPpm * 1e-6 * Math.Abs(target), FloorVolts);

    /// <summary>
    /// Trims <paramref name="output"/> until <paramref name="readTee"/> is within <see cref="Band"/> of
    /// <paramref name="target"/>. Stops at once, <see cref="TrimResult.Gross"/>, if the tee is more than 2% plus
    /// <see cref="GrossFloorVolts"/> off: that
    /// is not the lead, but the wrong terminals or no connection, and correcting it would only drive the calibrator up.
    /// </summary>
    public static TrimResult Run(double target, Action<double> output, Func<double> readTee, Action<TrimStep>? onStep = null)
    {
        var band = Band(target);
        var steps = new List<TrimStep>();
        var setting = target;
        for (var i = 0; i < MaxTrims; i++)
        {
            output(setting);
            var step = new TrimStep(setting, readTee());
            steps.Add(step);
            onStep?.Invoke(step);
            var error = step.Tee - target;
            if (Math.Abs(error) <= band)
                return new TrimResult(target, steps, Within: true, Gross: false);
            if (Math.Abs(error) > 0.02 * Math.Abs(target) + GrossFloorVolts)
                return new TrimResult(target, steps, Within: false, Gross: true);
            setting -= error;
        }
        return new TrimResult(target, steps, Within: false, Gross: false);
    }
}
