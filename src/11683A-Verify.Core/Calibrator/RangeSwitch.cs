namespace Verify11683A.Calibrator;

/// <summary>One position of the 11683A RANGE switch.</summary>
public sealed record RangePosition(string Id, double NominalWatts, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The 11683A RANGE switch, 3 µW to 100 mW (manual p. 5, PDF p. 16).</summary>
public static class RangeSwitch
{
    public static readonly IReadOnlyList<RangePosition> Positions =
    [
        new("3uW", 3e-6, "3 µW"),
        new("10uW", 10e-6, "10 µW"),
        new("30uW", 30e-6, "30 µW"),
        new("100uW", 100e-6, "100 µW"),
        new("300uW", 300e-6, "300 µW"),
        new("1mW", 1e-3, "1 mW"),
        new("3mW", 3e-3, "3 mW"),
        new("10mW", 10e-3, "10 mW"),
        new("30mW", 30e-3, "30 mW"),
        new("100mW", 100e-3, "100 mW"),
    ];

    public static RangePosition Get(string id) =>
        Positions.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"'{id}' is not an 11683A RANGE position.");

    public static int IndexOf(string id) => Positions.ToList().FindIndex(p => p.Id == Get(id).Id);
}

/// <summary>FUNCTION switch. STANDBY grounds the output; CALIBRATE couples it to the power meter (manual p. 5).</summary>
public enum FunctionSwitch
{
    Standby,
    Calibrate,
}

/// <summary>POLARITY switch. NORMAL gives an upscale power meter reading; REVERSE a downscale one.</summary>
public enum PolaritySwitch
{
    Normal,
    Reverse,
}

/// <summary>Option H01 rear-panel reference switch (INT/EXT). EXT takes the reference from the REF EXT BNC.</summary>
public enum ReferenceSwitch
{
    Int,
    Ext,
}
