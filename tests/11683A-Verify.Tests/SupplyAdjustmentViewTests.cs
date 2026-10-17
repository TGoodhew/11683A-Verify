using Spectre.Console.Testing;
using Verify11683A.App.Procedures;
using Verify11683A.Calibrator;

namespace Verify11683A.Tests;

public class SupplyAdjustmentViewTests
{
    private static readonly SupplyAdjustmentLimits Limits = CalibratorLimits.LoadDefault().SupplyAdjustment;

    [Theory]
    [InlineData(0.145, 30)]     // the centre column
    [InlineData(0.143, 10)]     // the low limit
    [InlineData(0.147, 50)]     // the high limit
    public void The_pointer_sits_under_the_matching_mark(double volts, int column)
    {
        var lines = SupplyAdjustmentProcedure.Gauge(AdjustmentReading.For(volts, Limits)).Split('\n');
        Assert.Equal('▲', lines[1][column]);
        Assert.Contains(lines[0][column], "|┼");
    }

    [Fact]
    public void Off_the_scale_points_off_the_end()
    {
        var lines = SupplyAdjustmentProcedure.Gauge(AdjustmentReading.For(0.130, Limits)).Split('\n');
        Assert.Equal('◄', lines[1][0]);
        Assert.Contains("143.00 mV", lines[2]);
        Assert.Contains("147.00 mV", lines[2]);
    }

    [Fact]
    public void The_view_renders_the_state_and_the_advice()
    {
        var console = new TestConsole().Width(120);
        console.Write(SupplyAdjustmentProcedure.View(AdjustmentReading.For(0.1421, Limits), [0.1421, 0.14211], null, "Press Enter when done."));
        Assert.Contains("LOW", console.Output);
        Assert.Contains("raise the output", console.Output);
        Assert.Contains("-2.900 mV", console.Output);
        Assert.Contains("Press Enter when done.", console.Output);
    }

    [Fact]
    public void The_warning_is_the_manuals_text()
    {
        Assert.StartsWith("The operations in this section require the instrument is operated with its covers removed",
            SupplyAdjustmentProcedure.CoversOffWarning);
        Assert.EndsWith("qualified service personnel.", SupplyAdjustmentProcedure.CoversOffWarning);
    }
}
