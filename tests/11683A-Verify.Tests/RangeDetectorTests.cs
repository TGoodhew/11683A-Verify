using Verify11683A.Calibrator;
using Verify11683A.Measurement;

namespace Verify11683A.Tests;

public class RangeDetectorTests
{
    private static readonly CalibratorLimits Limits = CalibratorLimits.LoadDefault();

    private static readonly RangeDetector Volts = new(DvmFunction.DcVoltage,
        Limits.Part1.Points.Select(p => new ExpectedValue(p.Range, Limits.NominalPart1Volts(p.Range))).ToList());

    private static readonly RangeDetector Ohms = new(DvmFunction.FourWireResistance,
        Limits.Part2.Points.Select(p => new ExpectedValue(p.Range, p.OhmsMid)).ToList());

    [Theory]
    [InlineData("100mW")]
    [InlineData("30mW")]
    [InlineData("10mW")]
    [InlineData("3mW")]
    [InlineData("1mW")]
    [InlineData("300uW")]
    public void Every_part_1_voltage_is_recognised_even_2_percent_low(string range)
    {
        var nominal = Limits.NominalPart1Volts(range);
        foreach (var value in new[] { nominal, nominal * 0.98, nominal * 1.02 })
        {
            var detection = Volts.Detect(value, overload: false);
            Assert.Equal(DetectionKind.Match, detection.Kind);
            Assert.Equal(range, detection.Candidate!.RangeId);
        }
    }

    [Fact]
    public void A_range_that_fails_its_ratio_is_still_recognised()
    {
        // The ratio-fail scenario: 10 mW 0.6% high.
        var detection = Volts.Detect(Limits.NominalPart1Volts("10mW") * 1.006, overload: false);
        Assert.Equal("10mW", detection.Candidate!.RangeId);
        Assert.Equal(0.006, detection.Deviation, 6);
    }

    [Theory]
    [InlineData(0.4e-6)]
    [InlineData(-3e-6)]
    [InlineData(49e-6)]
    public void About_zero_volts_means_standby_or_ref_ext(double reading)
    {
        var detection = Volts.Detect(reading, overload: false);
        Assert.Equal(DetectionKind.NearZero, detection.Kind);
        Assert.Contains("STANDBY", detection.Message);
        Assert.Contains("EXT", detection.Message);
        Assert.Null(detection.Candidate);
    }

    [Fact]
    public void A_negative_reading_means_polarity_reverse()
    {
        var detection = Volts.Detect(-15.80, overload: false);
        Assert.Equal(DetectionKind.Reversed, detection.Kind);
        Assert.Contains("REVERSE", detection.Message);
        Assert.Equal("100mW", detection.Candidate!.RangeId);
        Assert.False(detection.IsMatch);
    }

    [Fact]
    public void A_range_that_is_not_a_part_1_point_is_out_of_family()
    {
        // 100 µW in CALIBRATE: about 14.5 mV, a √10 step below 300 µW.
        var detection = Volts.Detect(0.0145, overload: false);
        Assert.Equal(DetectionKind.OutOfFamily, detection.Kind);
        Assert.Contains("RANGE is on 100 µW", detection.Message);
        Assert.Contains("turn RANGE UP to 300 µW or higher", detection.Message);
    }

    [Fact]
    public void The_3_uW_position_in_part_1_says_turn_up()
    {
        // On the bench 2026-10-26: RANGE on 3 µW read 456.64 µV, four steps below 300 µW.
        var detector = new RangeDetector(DvmFunction.DcVoltage, Volts.Candidates, scope: "Part 1");
        var detection = detector.Detect(456.64e-6, overload: false);
        Assert.Equal(DetectionKind.OutOfFamily, detection.Kind);
        Assert.Equal("RANGE is on 3 µW (456.64 µV), which Part 1 doesn't use: turn RANGE UP to 300 µW or higher", detection.Message);
    }

    [Fact]
    public void A_position_above_part_2_says_turn_down()
    {
        // 1 mW in STANDBY: a √10 step above 300 µW's 3150 Ω.
        var detection = Ohms.Detect(Limits.Part2.Point("300uW").OhmsMid * Math.Sqrt(10) * 1.01, overload: false);
        Assert.Contains("RANGE is on 1 mW", detection.Message);
        Assert.Contains("turn RANGE DOWN to 300 µW or lower", detection.Message);
    }

    [Fact]
    public void A_reading_between_steps_names_no_position()
    {
        Assert.Contains("Check RANGE", Volts.Detect(0.258, overload: false).Message);
    }

    [Fact]
    public void Halfway_between_two_ranges_is_out_of_family()
    {
        // Geometric middle of 1 mW (145 mV) and 3 mW (458.5 mV).
        Assert.Equal(DetectionKind.OutOfFamily, Volts.Detect(0.258, overload: false).Kind);
    }

    [Fact]
    public void Close_to_two_candidates_is_ambiguous()
    {
        var detector = new RangeDetector(DvmFunction.DcVoltage, [new("1mW", 1.0), new("3mW", 1.15)]);
        var detection = detector.Detect(1.07, overload: false);
        Assert.Equal(DetectionKind.Ambiguous, detection.Kind);
        Assert.Null(detection.Candidate);
    }

    [Fact]
    public void Overload_is_never_a_match()
    {
        Assert.Equal(DetectionKind.Overload, Volts.Detect(9.9e37, overload: true).Kind);
        var open = Ohms.Detect(9.9e37, overload: true);
        Assert.Equal(DetectionKind.Overload, open.Kind);
        Assert.Contains("open circuit", open.Message);
    }

    [Theory]
    [InlineData("300uW")]
    [InlineData("100uW")]
    [InlineData("30uW")]
    [InlineData("10uW")]
    [InlineData("3uW")]
    public void Every_part_2_resistance_is_recognised(string range)
    {
        var detection = Ohms.Detect(Limits.Part2.Point(range).OhmsMid, overload: false);
        Assert.True(detection.IsMatch);
        Assert.Equal(range, detection.Candidate!.RangeId);
    }

    [Fact]
    public void Ohms_with_the_output_live_on_300_uW_is_out_of_family()
    {
        // 3150.2 Ω + 45.85 mV / 100 µA = 3608.7 Ω: 14.6% high.
        var detection = Ohms.Detect(3608.7, overload: false);
        Assert.Equal(DetectionKind.OutOfFamily, detection.Kind);
        Assert.Contains("STANDBY", detection.Message);
    }

    [Fact]
    public void Shorted_or_negative_ohms_are_warnings()
    {
        Assert.Equal(DetectionKind.NearZero, Ohms.Detect(0.02, overload: false).Kind);
        Assert.Equal(DetectionKind.Reversed, Ohms.Detect(-12.0, overload: false).Kind);
    }
}
