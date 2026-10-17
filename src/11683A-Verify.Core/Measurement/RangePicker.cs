namespace Verify11683A.Measurement;

/// <summary>
/// Chooses a fixed DVM range. Autorange is never used for a result: the range is the smallest one
/// whose full scale covers the expected magnitude, so 15.8 V lands on 100 V, 145 mV on 1 V,
/// 3150 Ω on 10 kΩ and 99.96 Ω on 100 Ω (its 100.18 Ω upper limit is inside the 20% overrange).
/// </summary>
public static class RangePicker
{
    /// <summary>Readings above this fraction of a range are treated as "near the end" and re-ranged.</summary>
    public const double NearTopFraction = 1.15;

    /// <summary>Readings below this fraction of a range would be better on the next range down.</summary>
    public const double NearBottomFraction = 0.09;

    public static double Pick(IReadOnlyList<double> ranges, double expectedMagnitude)
    {
        if (ranges.Count == 0)
            throw new ArgumentException("No ranges to pick from.", nameof(ranges));

        var magnitude = Math.Abs(expectedMagnitude);
        foreach (var range in ranges.Order())
            if (magnitude <= range)
                return range;
        return ranges.Max();
    }

    /// <summary>True if the reading belongs on a different range from the one it was taken on.</summary>
    public static bool ShouldRerange(IReadOnlyList<double> ranges, double range, double reading)
    {
        var magnitude = Math.Abs(reading);
        if (magnitude > range * NearTopFraction && range < ranges.Max())
            return true;
        return magnitude < range * NearBottomFraction && range > ranges.Min();
    }
}
