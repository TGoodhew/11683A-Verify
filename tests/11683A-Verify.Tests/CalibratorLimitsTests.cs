using Verify11683A.Calibrator;

namespace Verify11683A.Tests;

/// <summary>The limits file against the 11683A manual, pp. 7-8 (PDF pp. 18-19).</summary>
public class CalibratorLimitsTests
{
    private static readonly CalibratorLimits Limits = CalibratorLimits.LoadDefault();

    [Theory]
    [InlineData("30mW", 3.3457, 3.3604)]
    [InlineData("10mW", 10.768, 10.815)]
    [InlineData("3mW", 34.394, 34.545)]
    [InlineData("1mW", 108.76, 109.23)]
    [InlineData("300uW", 343.95, 345.45)]
    public void Part_1_ratio_limits_match_the_manual(string range, double min, double max)
    {
        var point = Limits.Part1.Point(range);
        Assert.Equal(min, point.RatioMin);
        Assert.Equal(max, point.RatioMax);
    }

    [Fact]
    public void Part_1_has_the_100_mW_reference_and_the_1_mW_voltage_limits()
    {
        Assert.Equal(6, Limits.Part1.Points.Count);
        Assert.Equal("100mW", Limits.Part1.ReferenceRange);
        var oneMilliwatt = Limits.Part1.Point("1mW");
        Assert.Equal(0.14300, oneMilliwatt.VoltsMin);
        Assert.Equal(0.14700, oneMilliwatt.VoltsMax);
    }

    [Theory]
    [InlineData("300uW", 3143.3, 3157.1)]
    [InlineData("100uW", 995.90, 1000.2)]
    [InlineData("30uW", 315.14, 316.52)]
    [InlineData("10uW", 99.749, 100.18)]
    [InlineData("3uW", 31.580, 31.718)]
    public void Part_2_ohms_limits_match_the_manual(string range, double min, double max)
    {
        var point = Limits.Part2.Point(range);
        Assert.Equal(min, point.OhmsMin);
        Assert.Equal(max, point.OhmsMax);
    }

    [Fact]
    public void Supply_adjustment_is_145_mV_plus_or_minus_2_mV_on_A2R1()
    {
        Assert.Equal("1mW", Limits.SupplyAdjustment.Range);
        Assert.Equal(0.145, Limits.SupplyAdjustment.TargetVolts);
        Assert.Equal(0.002, Limits.SupplyAdjustment.ToleranceVolts);
        Assert.Equal("A2R1", Limits.SupplyAdjustment.Adjustment);
        Assert.Equal(1e8, Limits.LoadMinimumOhms);
    }

    [Fact]
    public void Nominal_voltages_are_anchored_on_145_mV_and_put_100_mW_near_15_8_V()
    {
        Assert.Equal(0.145, Limits.NominalPart1Volts("1mW"), 12);
        // 0.145 V × 108.995 = 15.8043 V
        Assert.Equal(15.804275, Limits.NominalPart1Volts("100mW"), 6);
        // Every nominal reproduces its mid-band ratio.
        foreach (var p in Limits.Part1.Points)
            Assert.Equal(p.RatioMid, Limits.NominalPart1Volts("100mW") / Limits.NominalPart1Volts(p.Range), 9);
    }

    [Fact]
    public void Range_switch_has_ten_positions_and_rejects_unknown_ones()
    {
        Assert.Equal(10, RangeSwitch.Positions.Count);
        Assert.Equal("300 µW", RangeSwitch.Get("300uW").Label);
        Assert.Throws<KeyNotFoundException>(() => RangeSwitch.Get("300mW"));
    }
}
