namespace Verify11683A;

/// <summary>The DVM functions this verification uses.</summary>
public enum DvmFunction
{
    /// <summary>DC volts (Part 1, Part 3, Part 4 monitoring).</summary>
    DcVoltage,

    /// <summary>Four-wire ohms (Part 2).</summary>
    FourWireResistance,
}

public static class DvmFunctionExtensions
{
    public static string Unit(this DvmFunction function) => function switch
    {
        DvmFunction.DcVoltage => "V",
        DvmFunction.FourWireResistance => "Ω",
        _ => throw new ArgumentOutOfRangeException(nameof(function)),
    };

    public static string DisplayName(this DvmFunction function) => function switch
    {
        DvmFunction.DcVoltage => "DC volts",
        DvmFunction.FourWireResistance => "4-wire ohms",
        _ => throw new ArgumentOutOfRangeException(nameof(function)),
    };
}
