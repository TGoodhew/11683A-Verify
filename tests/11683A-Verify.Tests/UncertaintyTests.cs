using Verify11683A.Measurement;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

namespace Verify11683A.Tests;

/// <summary>
/// Uncertainty, TUR and verdicts worked by hand. Spec terms are from the 34401A Service Guide p. 12 (PDF p. 20),
/// 1-year column; the convention is u = spec/√3, RSS with s/√N, U = 2·u_c.
/// </summary>
public class UncertaintyTests
{
    private static readonly DvmSpecTable Hp34401A = DvmSpecTable.LoadForModel("34401A");
    private static readonly UncertaintyConvention Convention = UncertaintyConvention.Default;

    private static ReadingStatistics Stats(double mean, double sigma, int n) => new(n, mean, sigma, mean, mean);

    private static MeasurementUncertainty Reading(double range, double mean, double sigma, int n) =>
        MeasurementUncertainty.Of(Hp34401A.Limit(DvmFunction.DcVoltage, range, mean, SpecPeriod.Year1), Stats(mean, sigma, n), Convention);

    [Fact]
    public void One_mW_reading_on_the_1_V_range()
    {
        // spec = 0.0040% × 0.145 V + 0.0007% × 1 V = 12.8 µV
        // u_B  = 12.8 / √3 = 7.39008 µV
        // u_A  = 1 µV / √10 = 0.31623 µV
        // u_c  = √(7.39008² + 0.31623²) = 7.39685 µV;  U = 2 u_c = 14.7937 µV
        var u = Reading(1, 0.145, 1e-6, 10);
        Assert.Equal(7.39008e-6, u.TypeB, 11);
        Assert.Equal(0.31623e-6, u.TypeA, 11);
        Assert.Equal(7.39685e-6, u.Combined, 11);
        Assert.Equal(14.7937e-6, u.Expanded, 10);

        // 143.00–147.00 mV: half-width 2 mV, TUR = 2 mV / 14.7937 µV = 135.2
        var assessment = Assessment.Of(u.Value, u.Expanded, new Tolerance(0.143, 0.147), Convention);
        Assert.Equal(135.19, assessment.Tur, 2);
        Assert.Equal(Verdict.Pass, assessment.Verdict);
        Assert.False(assessment.LowTur);
    }

    [Fact]
    public void Ten_mW_ratio_rss_and_linear()
    {
        // V(100 mW) = 15.8 V on 100 V, s = 0.1 mV, N = 10:
        //   spec 0.0045% × 15.8 + 0.0006% × 100 = 1.311 mV; u_B = 756.90 µV; u_A = 31.62 µV; u_c = 757.57 µV → 47.947 ppm
        // V(10 mW) = 1.464 V on 10 V, s = 5 µV, N = 10:
        //   spec 0.0035% × 1.464 + 0.0005% × 10 = 101.24 µV; u_B = 58.451 µV; u_A = 1.581 µV; u_c = 58.472 µV → 39.940 ppm
        var numerator = Reading(100, 15.8, 0.1e-3, 10);
        var denominator = Reading(10, 1.464, 5e-6, 10);
        Assert.Equal(47.947e-6, numerator.RelativeCombined, 9);
        Assert.Equal(39.940e-6, denominator.RelativeCombined, 9);

        // Ratio 10.79235. RSS: √(47.947² + 39.940²) = 62.403 ppm → U = 2 × 10.79235 × 62.403e-6 = 1.34695e-3
        var rss = RatioUncertainty.Of(numerator, denominator, Convention);
        Assert.Equal(10.79235, rss.Value, 5);
        Assert.Equal(62.403e-6, rss.RelativeCombined, 9);
        Assert.Equal(1.34695e-3, rss.Expanded, 8);

        // Linear: 47.947 + 39.940 = 87.887 ppm → U = 1.89702e-3
        var linear = RatioUncertainty.Of(numerator, denominator, Convention with { RatioCombination = RatioCombination.Linear });
        Assert.Equal(87.887e-6, linear.RelativeCombined, 9);
        Assert.Equal(1.89702e-3, linear.Expanded, 8);

        // Limits 10.768–10.815: half-width 0.0235, TUR = 0.0235 / 1.34695e-3 = 17.45
        var assessment = Assessment.Of(rss.Value, rss.Expanded, new Tolerance(10.768, 10.815), Convention);
        Assert.Equal(17.45, assessment.Tur, 2);
        Assert.Equal(Verdict.Pass, assessment.Verdict);
    }

    [Theory]
    // Limits 10–20, U = 1.
    [InlineData(15.0, Verdict.Pass)]
    [InlineData(11.0, Verdict.Pass)]           // 10–12: just inside
    [InlineData(19.0, Verdict.Pass)]
    [InlineData(10.5, Verdict.Indeterminate)]  // inside, but U crosses the lower limit
    [InlineData(9.5, Verdict.Indeterminate)]   // outside, but U reaches back inside
    [InlineData(20.9, Verdict.Indeterminate)]
    [InlineData(8.9, Verdict.Fail)]
    [InlineData(21.1, Verdict.Fail)]
    public void Guard_band_verdicts(double value, Verdict expected)
    {
        var assessment = Assessment.Of(value, 1, new Tolerance(10, 20), Convention);
        Assert.Equal(expected, assessment.Verdict);
        Assert.Equal(5, assessment.Tur);
    }

    [Fact]
    public void Simple_acceptance_is_kept_alongside_the_verdict()
    {
        Assert.True(Assessment.Of(10.5, 1, new Tolerance(10, 20), Convention).InsideLimits);
        Assert.False(Assessment.Of(9.5, 1, new Tolerance(10, 20), Convention).InsideLimits);
    }

    [Fact]
    public void A_tur_below_4_to_1_is_flagged()
    {
        Assert.True(Assessment.Of(15, 1.3, new Tolerance(10, 20), Convention).LowTur);    // 3.85:1
        Assert.False(Assessment.Of(15, 1.25, new Tolerance(10, 20), Convention).LowTur);  // 4:1
    }

    [Fact]
    public void No_limits_is_info()
    {
        var assessment = Assessment.Of(15.8, 1e-3, null, Convention);
        Assert.Equal(Verdict.Info, assessment.Verdict);
        Assert.Null(assessment.InsideLimits);
        Assert.True(double.IsNaN(assessment.Tur));
    }

    [Fact]
    public void One_reading_has_no_type_a_term()
    {
        var u = Reading(1, 0.145, 0, 1);
        Assert.Equal(0, u.TypeA);
        Assert.Equal(u.TypeB, u.Combined);
    }

    [Fact]
    public void Temperature_outside_18_to_28_C_widens_the_type_b_term()
    {
        // At 30 °C the 1 mW spec is 12.8 + 3.45 = 16.25 µV → u_B = 9.3819 µV.
        var spec = Hp34401A.Limit(DvmFunction.DcVoltage, 1, 0.145, SpecPeriod.Year1, ambientC: 30);
        var u = MeasurementUncertainty.Of(spec, Stats(0.145, 0, 1), Convention);
        Assert.Equal(9.3819e-6, u.TypeB, 10);
    }
}

public class DvmDetailsTests
{
    [Fact]
    public void Calibration_due_date_and_past_due()
    {
        var dvm = new DvmDetails("HP", "34401A", "3146A12345", "11-5-2", new DateOnly(2025, 10, 1), 12);
        Assert.Equal(new DateOnly(2026, 10, 1), dvm.DueDate);
        Assert.Equal(CalibrationState.InDate, dvm.StateOn(new DateOnly(2026, 10, 1)));
        Assert.Equal(CalibrationState.PastDue, dvm.StateOn(new DateOnly(2026, 10, 2)));
        Assert.Equal(CalibrationState.NotEntered, (dvm with { CalibrationDate = null }).StateOn(new DateOnly(2026, 10, 2)));
    }

    [Theory]
    [InlineData("0", "3146A12345", "3146A12345")]
    [InlineData("", "2823A01234", "2823A01234")]
    [InlineData("MY12345678", "", "MY12345678")]
    public void Serial_comes_from_config_when_the_bus_does_not_report_one(string reported, string configured, string expected) =>
        Assert.Equal(expected, DvmDetails.ChooseSerial(reported, configured));
}
