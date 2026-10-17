using Verify11683A.H01;

namespace Verify11683A.Results;

/// <summary>
/// Steps 1–2: the power meter's reading with the 11683A on its internal reference at 1 mW, then on REF EXT fed with
/// that same 1 mW DC REFERENCE OUTPUT voltage from a DC calibrator.
/// </summary>
/// <param name="TargetVolts">The 1 mW DC REFERENCE OUTPUT voltage measured with REF INT (Part 1).</param>
/// <param name="AppliedVolts">What the DVM read at the REF EXT tee while the EXT reading was taken.</param>
public sealed record TransferCheck(
    double IntWatts,
    double TargetVolts,
    double AppliedVolts,
    double ExtWatts,
    double Tolerance)
{
    /// <summary>EXT reading / INT reading − 1, as read.</summary>
    public double RawAgreement => ExtWatts / IntWatts - 1;

    /// <summary>
    /// The agreement with the EXT reading corrected for the DC calibrator being slightly off the target. The
    /// correction is linear, which Table 2-4 is up to 1 mW, and it is only a few ppm.
    /// </summary>
    public double Agreement => ExtWatts * (TargetVolts / AppliedVolts) / IntWatts - 1;

    public bool WithinTolerance => Math.Abs(Agreement) <= Tolerance;
}

/// <summary>Step 3: one Table 2-4 row, read against the table's own limits.</summary>
public sealed record H01PointResult(H01Row Row, double AppliedVolts, double ReadingWatts)
{
    /// <summary>The DC calibrator's error from the table setting. The reading is not corrected for it (above 1 mW the table is not linear).</summary>
    public double SourceError => AppliedVolts / Row.SettingVolts - 1;

    public double Deviation => ReadingWatts / Row.EffectiveWatts - 1;

    public bool WithinLimits => ReadingWatts >= Row.LowerWatts && ReadingWatts <= Row.UpperWatts;
}

/// <summary>
/// Part 4, the Option H01 REF EXT check. Informational: an indication under the power meter's own uncertainty, not a
/// verdict on the 11683A, so it never changes the run's verdict.
/// </summary>
public sealed record Part4Result(
    string Source,
    string PowerMeter,
    bool PowerMeterOverBus,
    TransferCheck? Transfer,
    IReadOnlyList<H01PointResult> TablePoints,
    IReadOnlyList<string> Notes)
{
    public bool AllWithin => (Transfer?.WithinTolerance ?? true) && TablePoints.All(p => p.WithinLimits);
}
