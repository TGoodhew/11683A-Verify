using Verify11683A.Results;

namespace Verify11683A.Instruments.Dvm;

/// <summary>The outcome of checking that the DC REFERENCE OUTPUT is dead before ohms current is applied.</summary>
public sealed record OhmsSafetyCheck(bool Safe, DvmReading Reading, double ThresholdVolts)
{
    public string Explanation => Reading.Overload
        ? "the output overloads the 100 mV range: FUNCTION is in CALIBRATE"
        : Safe
            ? "the output is grounded (FUNCTION in STANDBY)"
            : "the output is live: FUNCTION is in CALIBRATE";
}

/// <summary>
/// Never apply ohms current while the 11683A FUNCTION switch is in CALIBRATE. STANDBY grounds the output,
/// so a DCV reading of a few µV confirms it. The 100 mV range is used because the 3 µW range in CALIBRATE
/// gives only about 0.46 mV, which a coarser range could miss.
/// </summary>
public static class OhmsSafety
{
    public const double DefaultThresholdVolts = 50e-6;

    /// <summary>
    /// Readings at NPLC 10 (about 0.4 s each, so up to 4 s) to allow, so an output still falling to ground just after FUNCTION went
    /// to STANDBY is not refused. A live output never reads near zero, so waiting is never a risk.
    /// </summary>
    public const int DefaultMaxReadings = 10;

    public static OhmsSafetyCheck Check(IDvm dvm, double thresholdVolts = DefaultThresholdVolts, int maxReadings = DefaultMaxReadings)
    {
        dvm.Configure(DvmFunction.DcVoltage, dvm.Ranges(DvmFunction.DcVoltage)[0], nplc: 10);   // the lowest range
        DvmReading reading = default;
        for (var i = 0; i < Math.Max(1, maxReadings); i++)
        {
            reading = dvm.Read();
            if (!reading.Overload && Math.Abs(reading.Value) <= thresholdVolts)
                return new OhmsSafetyCheck(true, reading, thresholdVolts);
        }
        return new OhmsSafetyCheck(false, reading, thresholdVolts);
    }

    /// <summary>
    /// An interlock for <see cref="Measurement.PointAcquirer.Interlock"/>: runs the check before every detection
    /// attempt, records it, and refuses while the output is live.
    /// </summary>
    public static Func<string?> Interlock(IDvm dvm, Action<OhmsSafetyCheck>? record = null, double thresholdVolts = DefaultThresholdVolts) => () =>
    {
        var check = Check(dvm, thresholdVolts);
        record?.Invoke(check);
        return check.Safe ? null : $"{check.Explanation}; set FUNCTION to STANDBY before ohms are measured";
    };

    public static OhmsSafetyRecord ToRecord(this OhmsSafetyCheck check) =>
        new(check.Reading.Time, check.Reading.Value, check.Reading.Overload, check.ThresholdVolts, check.Safe);
}
