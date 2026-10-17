namespace Verify11683A.Calibrator;

/// <summary>A Part 1 row: voltage ratio V(100 mW)/V(range) limits, plus voltage limits where the manual gives them.</summary>
public sealed class Part1Point
{
    public string Range { get; init; } = "";
    public double RatioMin { get; init; }
    public double RatioMax { get; init; }
    public double? VoltsMin { get; init; }
    public double? VoltsMax { get; init; }

    public double RatioMid => (RatioMin + RatioMax) / 2;
}

public sealed class Part1Limits
{
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string ReferenceRange { get; init; } = "";
    public List<Part1Point> Points { get; init; } = [];

    public Part1Point Point(string range) =>
        Points.FirstOrDefault(p => p.Range == range)
        ?? throw new KeyNotFoundException($"Part 1 has no {range} point.");
}

/// <summary>A Part 2 row: four-wire resistance limits at the rear BNC with FUNCTION in STANDBY.</summary>
public sealed class Part2Point
{
    public string Range { get; init; } = "";
    public double OhmsMin { get; init; }
    public double OhmsMax { get; init; }

    public double OhmsMid => (OhmsMin + OhmsMax) / 2;
}

public sealed class Part2Limits
{
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public List<Part2Point> Points { get; init; } = [];

    public Part2Point Point(string range) =>
        Points.FirstOrDefault(p => p.Range == range)
        ?? throw new KeyNotFoundException($"Part 2 has no {range} point.");
}

public sealed class SupplyAdjustmentLimits
{
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string Range { get; init; } = "";
    public double TargetVolts { get; init; }
    public double ToleranceVolts { get; init; }
    public string Adjustment { get; init; } = "";
}

/// <summary>The 11683A limits from data/limits-11683A.json, each section citing its manual page.</summary>
public sealed class CalibratorLimits
{
    public string Model { get; init; } = "";
    public string Source { get; init; } = "";
    public double LoadMinimumOhms { get; init; }
    public string LoadMinimumSource { get; init; } = "";
    public Part1Limits Part1 { get; init; } = new();
    public Part2Limits Part2 { get; init; } = new();
    public SupplyAdjustmentLimits SupplyAdjustment { get; init; } = new();

    public static CalibratorLimits Load(string path)
    {
        var limits = Json.Load<CalibratorLimits>(path);
        limits.Validate();
        return limits;
    }

    public static CalibratorLimits LoadDefault(string? dataDirectory = null) =>
        Load(DataFiles.Resolve(DataFiles.LimitsFileName, dataDirectory));

    /// <summary>
    /// Expected DC REFERENCE OUTPUT voltage for a Part 1 range. The manual gives only ratios, so this is
    /// anchored on the supply-adjustment target at 1 mW (145.00 mV) and scaled by the mid-band ratios:
    /// V(range) = V(1 mW) × ratio(1 mW) / ratio(range). That puts 100 mW at about 15.8 V.
    /// </summary>
    public double NominalPart1Volts(string range)
    {
        var anchor = Part1.Point(SupplyAdjustment.Range);
        var reference = SupplyAdjustment.TargetVolts * anchor.RatioMid;
        return reference / Part1.Point(range).RatioMid;
    }

    private void Validate()
    {
        foreach (var p in Part1.Points)
        {
            _ = RangeSwitch.Get(p.Range);
            if (p.RatioMin > p.RatioMax)
                throw new InvalidDataException($"Part 1 {p.Range}: ratioMin > ratioMax.");
        }
        foreach (var p in Part2.Points)
        {
            _ = RangeSwitch.Get(p.Range);
            if (p.OhmsMin > p.OhmsMax)
                throw new InvalidDataException($"Part 2 {p.Range}: ohmsMin > ohmsMax.");
        }
        _ = Part1.Point(Part1.ReferenceRange);
        _ = Part1.Point(SupplyAdjustment.Range);
    }
}
