namespace Verify11683A.Measurement;

/// <summary>
/// How long the instruments must warm up and how long they have been on. The 11683A defaults to 30 minutes; the
/// DVM's figure comes from its spec table (34401A: 1 hour, Service Guide p. 13; 3458A: 4 hours).
/// </summary>
public sealed record WarmupPlan(double CalibratorRequiredMinutes, double DvmRequiredMinutes, double CalibratorOnMinutes, double DvmOnMinutes)
{
    public const double DefaultCalibratorMinutes = 30;

    /// <summary>The wait still needed so both instruments have warmed up.</summary>
    public TimeSpan Remaining => TimeSpan.FromMinutes(Math.Max(0, Math.Max(
        CalibratorRequiredMinutes - CalibratorOnMinutes,
        DvmRequiredMinutes - DvmOnMinutes)));

    /// <summary>Which instrument sets the wait: "11683A", the DVM, both, or none.</summary>
    public string Limiting(string dvmModel)
    {
        var calibrator = CalibratorRequiredMinutes - CalibratorOnMinutes;
        var dvm = DvmRequiredMinutes - DvmOnMinutes;
        if (calibrator <= 0 && dvm <= 0) return "none";
        if (Math.Abs(calibrator - dvm) < 1e-9) return $"the 11683A and the {dvmModel}";
        return calibrator > dvm ? "the 11683A" : $"the {dvmModel}";
    }

    /// <summary>The plan after waiting <paramref name="waited"/>.</summary>
    public WarmupPlan After(TimeSpan waited) => this with
    {
        CalibratorOnMinutes = CalibratorOnMinutes + waited.TotalMinutes,
        DvmOnMinutes = DvmOnMinutes + waited.TotalMinutes,
    };
}
