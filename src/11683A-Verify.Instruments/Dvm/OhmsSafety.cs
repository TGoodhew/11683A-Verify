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

    public static OhmsSafetyCheck Check(IDvm dvm, double thresholdVolts = DefaultThresholdVolts)
    {
        dvm.Configure(DvmFunction.DcVoltage, 0.1, nplc: 10);
        var reading = dvm.Read();
        var safe = !reading.Overload && Math.Abs(reading.Value) <= thresholdVolts;
        return new OhmsSafetyCheck(safe, reading, thresholdVolts);
    }
}
