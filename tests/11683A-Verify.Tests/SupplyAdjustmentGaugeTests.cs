using Verify11683A.Calibrator;

namespace Verify11683A.Tests;

public class SupplyAdjustmentGaugeTests
{
    private static readonly SupplyAdjustmentLimits Limits = CalibratorLimits.LoadDefault().SupplyAdjustment;

    [Theory]
    [InlineData(0.14500, AdjustmentState.Centred)]
    [InlineData(0.14519, AdjustmentState.Centred)]      // within 0.2 mV of 145.00
    [InlineData(0.14480, AdjustmentState.Centred)]
    [InlineData(0.14530, AdjustmentState.InTolerance)]
    [InlineData(0.14300, AdjustmentState.InTolerance)]  // on the low limit
    [InlineData(0.14700, AdjustmentState.InTolerance)]
    [InlineData(0.14210, AdjustmentState.Low)]          // the supply-low scenario
    [InlineData(0.14705, AdjustmentState.High)]
    public void State_against_145_00_plus_minus_2_00_mV(double volts, AdjustmentState expected) =>
        Assert.Equal(expected, AdjustmentReading.For(volts, Limits).State);

    [Fact]
    public void Error_position_and_advice()
    {
        var low = AdjustmentReading.For(0.1421, Limits);
        Assert.Equal(-2.9e-3, low.Error, 12);
        Assert.Equal(-20000, low.ErrorPpm, 6);
        Assert.Equal(-1.45, low.Position, 9);
        Assert.Equal("raise the output", low.Advice);
        Assert.Equal("lower the output", AdjustmentReading.For(0.146, Limits).Advice);
        Assert.Equal("leave A2R1 here", AdjustmentReading.For(0.14501, Limits).Advice);
    }

    [Fact]
    public void Target_and_tolerance_come_from_the_manual()
    {
        Assert.Equal(0.145, Limits.TargetVolts);
        Assert.Equal(0.002, Limits.ToleranceVolts);
        Assert.Equal("A2R1", Limits.Adjustment);
        Assert.Equal("1mW", Limits.Range);
    }
}
