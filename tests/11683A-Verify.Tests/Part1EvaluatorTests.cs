using Verify11683A.Calibrator;
using Verify11683A.Measurement;
using Verify11683A.Results;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

namespace Verify11683A.Tests;

public class Part1EvaluatorTests
{
    private static readonly CalibratorLimits Limits = CalibratorLimits.LoadDefault();
    private static readonly DvmSpecTable Specs = DvmSpecTable.LoadForModel("34401A");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Ten readings alternating ±1 ppm about the value, on the range the test would use.</summary>
    internal static MeasuredPoint Point(string range, double volts)
    {
        double[] ranges = [0.1, 1, 10, 100];
        var dvmRange = RangePicker.Pick(ranges, volts);
        var readings = Enumerable.Range(0, 10)
            .Select(i => new TimedValue(volts * (1 + (i % 2 == 0 ? 1e-6 : -1e-6)), T0.AddSeconds(3.383 * i)))
            .ToList();
        return new MeasuredPoint(range, DvmFunction.DcVoltage, dvmRange, 100, dvmRange <= 10 ? ">10 GΩ" : "10 MΩ ±1%", readings, true, 20, 1);
    }

    private static List<MeasuredPoint> All(Func<string, double, double> volts) =>
        Limits.Part1.Points.Select(p => Point(p.Range, volts(p.Range, Limits.NominalPart1Volts(p.Range)))).ToList();

    private static Part1Result Evaluate(IReadOnlyCollection<MeasuredPoint> points) =>
        Part1Evaluator.Evaluate(Limits, points, Specs, SpecPeriod.Year1, null, UncertaintyConvention.Default);

    [Fact]
    public void Nominal_unit_passes_every_ratio_and_the_1_mW_voltage()
    {
        var result = Evaluate(All((_, v) => v));

        Assert.True(result.Complete);
        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.False(result.SupplyAdjustmentRecommended);
        Assert.Equal(6, result.Points.Count);

        var reference = result.Point("100mW")!;
        Assert.Null(reference.RatioAssessment);
        Assert.Equal(Verdict.Info, reference.Verdict);

        foreach (var p in result.Points.Where(p => p.RangeId != "100mW"))
        {
            var limit = Limits.Part1.Point(p.RangeId);
            Assert.Equal(limit.RatioMid, p.Ratio!.Value, limit.RatioMid * 1e-6);
            Assert.Equal(Verdict.Pass, p.RatioAssessment!.Verdict);
        }
        Assert.Equal(Verdict.Pass, result.Point("1mW")!.VoltsAssessment!.Verdict);
    }

    [Fact]
    public void The_100_mW_reading_on_the_10_megohm_range_is_noted()
    {
        var result = Evaluate(All((_, v) => v));
        Assert.Contains(result.Notes, n => n.Contains("100 V range") && n.Contains("10 MΩ") && n.Contains("3455A"));
    }

    [Fact]
    public void Supply_2_percent_low_recommends_the_power_supply_adjustment()
    {
        var result = Evaluate(All((_, v) => v * 0.98));

        // Every ratio still passes: the supply scales every range together.
        Assert.All(result.Points.Where(p => p.RatioAssessment is not null), p => Assert.Equal(Verdict.Pass, p.RatioAssessment!.Verdict));
        var oneMw = result.Point("1mW")!.VoltsAssessment!;
        Assert.Equal(0.1421, oneMw.Value, 4);
        Assert.Equal(Verdict.Fail, oneMw.Verdict);
        Assert.True(result.SupplyAdjustmentRecommended);
        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Notes, n => n.Contains("A2R1") && n.Contains("repeat Part 1"));
    }

    [Fact]
    public void A_range_0_6_percent_high_fails_its_ratio()
    {
        // V(10 mW) 0.6% high → ratio 10.7915 / 1.006 = 10.727, below 10.768.
        var result = Evaluate(All((r, v) => r == "10mW" ? v * 1.006 : v));
        var tenMw = result.Point("10mW")!;
        Assert.Equal(10.727, tenMw.Ratio!.Value, 3);
        Assert.Equal(Verdict.Fail, tenMw.Verdict);
        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.False(result.SupplyAdjustmentRecommended);
    }

    [Fact]
    public void One_mW_just_inside_its_limit_is_indeterminate_and_suggests_margin()
    {
        // 143.005 mV: inside 143.00 mV, but U ≈ 14.8 µV reaches below it.
        var result = Evaluate(All((r, v) => v * 0.143005 / 0.145));
        var oneMw = result.Point("1mW")!.VoltsAssessment!;
        Assert.True(oneMw.InsideLimits);
        Assert.Equal(Verdict.Indeterminate, oneMw.Verdict);
        Assert.False(result.SupplyAdjustmentRecommended);
        Assert.Contains(result.Notes, n => n.Contains("would add margin"));
        Assert.Equal(Verdict.Indeterminate, result.Verdict);
    }

    [Fact]
    public void Without_100_mW_there_are_no_ratios()
    {
        var result = Evaluate(All((_, v) => v).Where(p => p.RangeId != "100mW").ToList());
        Assert.False(result.Complete);
        Assert.Equal(["100mW"], result.Missing);
        Assert.All(result.Points, p => Assert.Null(p.Ratio));
        Assert.Contains(result.Notes, n => n.Contains("no ratios"));
        // The 1 mW voltage is still checked.
        Assert.Equal(Verdict.Pass, result.Verdict);
    }

    [Fact]
    public void Points_may_arrive_in_any_order_and_missing_ones_are_listed()
    {
        var points = All((_, v) => v);
        points.Reverse();
        var result = Evaluate(points.Where(p => p.RangeId is not ("3mW" or "30mW")).ToList());
        Assert.Equal(["30mW", "3mW"], result.Missing);
        Assert.Equal(["100mW", "10mW", "1mW", "300uW"], result.Points.Select(p => p.RangeId));
    }
}

public class VerdictsTests
{
    [Theory]
    [InlineData(new[] { Verdict.Pass, Verdict.Info }, Verdict.Pass)]
    [InlineData(new[] { Verdict.Pass, Verdict.Indeterminate }, Verdict.Indeterminate)]
    [InlineData(new[] { Verdict.Indeterminate, Verdict.Fail, Verdict.Pass }, Verdict.Fail)]
    [InlineData(new[] { Verdict.Info }, Verdict.Info)]
    [InlineData(new Verdict[0], Verdict.Info)]
    public void Worst_verdict_wins(Verdict[] verdicts, Verdict expected) => Assert.Equal(expected, Verdicts.Combine(verdicts));
}
