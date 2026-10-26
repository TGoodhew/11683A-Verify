using Verify11683A.Specs;

namespace Verify11683A.Tests;

/// <summary>
/// Spec limits worked by hand from the manuals' tables. 34401A: Service Guide p. 12 (PDF p. 20).
/// 3458A: User's Guide Appendix A pp. 284 and 286 (PDF pp. 286 and 288).
/// Keithley 2001: SPEC-2001 Rev. H pp. 2 and 7, and the 2001 Calibration Manual's Table 1-2 (p. 1-5).
/// </summary>
public class DvmSpecTableTests
{
    private static readonly DvmSpecTable Hp34401A = DvmSpecTable.LoadForModel("34401A");
    private static readonly DvmSpecTable Hp3458A = DvmSpecTable.LoadForModel("3458A");
    private static readonly DvmSpecTable Keithley2001 = DvmSpecTable.LoadForModel("2001");

    [Theory]
    // The Calibration Manual's 1-year reading limits at 95% of each range, from 37 + 6, 25 + 2, 24 + 4, 38 + 3, 41 + 6 ppm.
    [InlineData(0.2, 0.19, 8.2e-6)]          // 189.9918 to 190.0082 mV
    [InlineData(2, 1.9, 51.5e-6)]            // 1.899949 to 1.900052 V
    [InlineData(20, 19, 536e-6)]             // 18.99946 to 19.00054 V
    [InlineData(200, 190, 7.82e-3)]          // 189.9922 to 190.0078 V
    [InlineData(1000, 1000, 47e-3)]          // 999.953 to 1000.047 V
    public void Keithley2001_1_year_matches_the_calibration_manual(double range, double volts, double limit)
    {
        var spec = Keithley2001.Limit(DvmFunction.DcVoltage, range, volts, SpecPeriod.Year1);
        Assert.Equal(limit, spec.Total, 0.006 * limit);
    }

    [Fact]
    public void Keithley2001_1_year_at_1_mW_on_the_200_mV_range()
    {
        // 37 ppm × 0.145 V + 6 ppm × 0.2 V = 5.365 µV + 1.2 µV = 6.565 µV
        var limit = Keithley2001.Limit(DvmFunction.DcVoltage, 0.2, 0.145, SpecPeriod.Year1);
        Assert.Equal(6.565e-6, limit.Total, 12);
    }

    [Fact]
    public void Hp34401A_1_year_at_1_mW_on_the_1_V_range()
    {
        // 0.0040% × 0.145 V + 0.0007% × 1 V = 5.8 µV + 7.0 µV = 12.8 µV
        var limit = Hp34401A.Limit(DvmFunction.DcVoltage, 1, 0.145, SpecPeriod.Year1);
        Assert.Equal(12.8e-6, limit.Total, 12);
        Assert.Equal(0, limit.AdderError);
        Assert.Equal(0, limit.TemperatureError);
    }

    [Fact]
    public void Hp34401A_1_year_at_100_mW_on_the_100_V_range()
    {
        // 0.0045% × 15.8 V + 0.0006% × 100 V = 711 µV + 600 µV = 1.311 mV
        var limit = Hp34401A.Limit(DvmFunction.DcVoltage, 100, 15.8, SpecPeriod.Year1);
        Assert.Equal(1.311e-3, limit.Total, 9);
    }

    [Fact]
    public void Hp34401A_1_year_at_3_uW_on_the_100_ohm_range()
    {
        // 0.010% × 31.65 Ω + 0.004% × 100 Ω = 3.165 mΩ + 4.000 mΩ = 7.165 mΩ
        var limit = Hp34401A.Limit(DvmFunction.FourWireResistance, 100, 31.65, SpecPeriod.Year1);
        Assert.Equal(7.165e-3, limit.Total, 9);
    }

    [Fact]
    public void Hp34401A_adds_the_temperature_coefficient_outside_18_to_28_C()
    {
        // 30 °C is 2 °C outside: (0.0005% × 0.145 V + 0.0001% × 1 V) × 2 = (0.725 µV + 1 µV) × 2 = 3.45 µV
        var limit = Hp34401A.Limit(DvmFunction.DcVoltage, 1, 0.145, SpecPeriod.Year1, ambientC: 30);
        Assert.Equal(3.45e-6, limit.TemperatureError, 12);
        Assert.Equal(12.8e-6 + 3.45e-6, limit.Total, 12);

        Assert.Equal(0, Hp34401A.Limit(DvmFunction.DcVoltage, 1, 0.145, SpecPeriod.Year1, ambientC: 23).TemperatureError);
        Assert.Equal(0, Hp34401A.Limit(DvmFunction.DcVoltage, 1, 0.145, SpecPeriod.Year1, ambientC: 18).TemperatureError);
    }

    [Fact]
    public void Hp3458A_1_year_on_the_10_V_range_includes_the_footnote_adders()
    {
        // 8 ppm × 4.7136 V + 0.05 ppm × 10 V        = 37.709 µV + 0.5 µV
        // + no MATH NULL, < 4 min: 0.25 ppm × 10 V  = 2.5 µV
        // + factory traceability: 2 ppm × 4.7136 V  = 9.427 µV
        var limit = Hp3458A.Limit(DvmFunction.DcVoltage, 10, 4.7136, SpecPeriod.Year1);
        Assert.Equal(38.2088e-6, limit.BaseError, 10);
        Assert.Equal(2.5e-6 + 9.4272e-6, limit.AdderError, 10);
        Assert.Equal(2, limit.AppliedAdders.Count);
    }

    [Fact]
    public void Hp3458A_1_year_four_wire_ohms_on_the_100_ohm_range()
    {
        // 12 ppm × 31.65 Ω + 5 ppm × 100 Ω = 379.8 µΩ + 500 µΩ, plus 3 ppm × 31.65 Ω traceability = 94.95 µΩ
        var limit = Hp3458A.Limit(DvmFunction.FourWireResistance, 100, 31.65, SpecPeriod.Year1);
        Assert.Equal(879.8e-6, limit.BaseError, 10);
        Assert.Equal(94.95e-6, limit.AdderError, 10);
    }

    [Theory]
    // Spot checks of transcribed values against the printed tables.
    [InlineData("34401A", DvmFunction.DcVoltage, 0.1, "1y", 0.0050, 0.0035)]
    [InlineData("34401A", DvmFunction.DcVoltage, 10, "90d", 0.0020, 0.0005)]
    [InlineData("34401A", DvmFunction.DcVoltage, 100, "24h", 0.0020, 0.0006)]
    [InlineData("34401A", DvmFunction.FourWireResistance, 1e3, "1y", 0.010, 0.001)]
    [InlineData("34401A", DvmFunction.FourWireResistance, 1e4, "24h", 0.0020, 0.0005)]
    [InlineData("3458A", DvmFunction.DcVoltage, 0.1, "1y", 9, 3)]
    [InlineData("3458A", DvmFunction.DcVoltage, 1, "90d", 4.6, 0.3)]
    [InlineData("3458A", DvmFunction.DcVoltage, 100, "2y", 14, 0.3)]
    [InlineData("3458A", DvmFunction.FourWireResistance, 100, "1y", 12, 5)]
    [InlineData("3458A", DvmFunction.FourWireResistance, 1e4, "24h", 2, 0.2)]
    [InlineData("2001", DvmFunction.DcVoltage, 2, "24h", 7, 2)]
    [InlineData("2001", DvmFunction.DcVoltage, 20, "90d", 18, 4)]
    [InlineData("2001", DvmFunction.FourWireResistance, 200, "1y", 56, 7)]
    [InlineData("2001", DvmFunction.FourWireResistance, 2e4, "2y", 80, 4.5)]
    public void Tables_hold_the_printed_values(string model, DvmFunction function, double range, string period, double reading, double rangeTerm)
    {
        var table = model switch { "34401A" => Hp34401A, "3458A" => Hp3458A, _ => Keithley2001 };
        var term = table.RangeEntry(function, range).Accuracy[period];
        Assert.Equal(reading, term.Reading);
        Assert.Equal(rangeTerm, term.Range);
    }

    [Fact]
    public void Units_scale_percent_and_ppm()
    {
        Assert.Equal(1e-2, Hp34401A.UnitScale);
        Assert.Equal(1e-6, Hp3458A.UnitScale);
    }

    [Fact]
    public void Hp34401A_has_no_2_year_column()
    {
        Assert.Throws<KeyNotFoundException>(() => Hp34401A.Limit(DvmFunction.DcVoltage, 1, 0.145, SpecPeriod.Year2));
    }

    [Fact]
    public void Both_tables_cover_every_range_the_verification_uses()
    {
        foreach (var table in new[] { Hp34401A, Hp3458A })
        {
            foreach (var r in new[] { 0.1, 1, 10, 100 })
                Assert.NotNull(table.RangeEntry(DvmFunction.DcVoltage, r));
            foreach (var r in new[] { 100.0, 1e3, 1e4 })
                Assert.NotNull(table.RangeEntry(DvmFunction.FourWireResistance, r));
        }
    }

    [Theory]
    [InlineData("24h", SpecPeriod.Hours24)]
    [InlineData("90d", SpecPeriod.Days90)]
    [InlineData("1y", SpecPeriod.Year1)]
    [InlineData("2y", SpecPeriod.Year2)]
    public void Spec_period_keys_round_trip(string key, SpecPeriod period)
    {
        Assert.Equal(period, SpecPeriodExtensions.Parse(key));
        Assert.Equal(key, period.Key());
    }
}
