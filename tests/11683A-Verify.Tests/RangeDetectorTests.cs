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
        Assert.Contains("300 µW", detection.Message);
        Assert.Contains("Check RANGE", detection.Message);
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
