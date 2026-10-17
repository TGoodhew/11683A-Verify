namespace Verify11683A.Specs;

public enum CalibrationState
{
    NotEntered,
    InDate,
    PastDue,
}

/// <summary>
/// The DVM as recorded on the report: identity from the bus where the instrument gives it, serial and calibration
/// date from config (the 34401A reports serial 0 and the 3458A none).
/// </summary>
public sealed record DvmDetails(
    string Manufacturer,
    string Model,
    string Serial,
    string Firmware,
    DateOnly? CalibrationDate,
    int CalibrationIntervalMonths)
{
    public DateOnly? DueDate => CalibrationDate?.AddMonths(CalibrationIntervalMonths);

    public CalibrationState StateOn(DateOnly today) => DueDate switch
    {
        null => CalibrationState.NotEntered,
        { } due when due < today => CalibrationState.PastDue,
        _ => CalibrationState.InDate,
    };

    /// <summary>The serial reported on the bus, unless it is empty or "0", in which case the configured one.</summary>
    public static string ChooseSerial(string reported, string configured) =>
        reported is "" or "0" ? configured : reported;
}
