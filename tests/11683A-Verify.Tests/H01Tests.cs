using Verify11683A.Calibrator;
using Verify11683A.H01;
using Verify11683A.Instruments.PowerMeter;
using Verify11683A.Instruments.Simulation;
using Verify11683A.Results;
using Verify11683A.Tests.Fakes;

namespace Verify11683A.Tests;

/// <summary>Table 2-4 of the E4418B Service Guide (p. 2-14, PDF p. 54) as loaded, and how it is used.</summary>
public class H01TableTests
{
    private static readonly H01Table Table = H01Table.LoadDefault();

    [Fact]
    public void All_ten_rows_load_with_the_procedures_settings()
    {
        Assert.Equal(10, Table.Rows.Count);
        Assert.Equal("100mW", Table.RangeSetting);
        Assert.Equal(0.14493, Table.CalibrationVolts);
        Assert.Equal(0.002, Table.TransferTolerance);
        Assert.Equal(458.31e-6, Table.Rows[0].SettingVolts);
        Assert.Equal(15.588, Table.Rows[^1].SettingVolts);
        Assert.Equal(99.54e-3, Table.Rows[^1].LowerWatts);
        Assert.Equal(16, Table.Rows[^1].Filter);
    }

    [Fact]
    public void Every_row_maps_to_its_own_power()
    {
        foreach (var row in Table.Rows)
            Assert.Equal(row.EffectiveWatts, Table.EffectiveWatts(row.SettingVolts), row.EffectiveWatts * 1e-9);
    }

    [Fact]
    public void Linear_up_to_1_mW_and_not_above()
    {
        // Up to 1 mW the rows are linear (to the table's rounding): 14.493 mV is 100 µW, 144.93 mV is 1 mW.
        Assert.Equal(316.2278e-6, Table.EffectiveWatts(0.045831), 1e-12);
        Assert.Equal(1e-3 * 0.145 / 0.14493, Table.EffectiveWatts(0.145), 1e-9);
        // Scaling 1 mW by 459.12/144.93 would give 3.1679 mW; the table says 3.1623 mW.
        Assert.Equal(3.1623e-3, Table.EffectiveWatts(0.45912), 1e-9);
        Assert.NotEqual(1e-3 * 0.45912 / 0.14493, Table.EffectiveWatts(0.45912), 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => Table.EffectiveWatts(16));
    }

    [Theory]
    [InlineData("1.0003 mW", 1.0003e-3)]
    [InlineData("999.8uW", 999.8e-6)]
    [InlineData("999.8 µW", 999.8e-6)]
    [InlineData("0.0010003", 1.0003e-3)]
    [InlineData("1.0003e-3 W", 1.0003e-3)]
    [InlineData("0 dBm", 1e-3)]
    [InlineData("20dBm", 0.1)]
    public void Typed_power_readings(string text, double watts) =>
        Assert.Equal(watts, PowerText.ParseWatts(text)!.Value, watts * 1e-9);

    [Theory]
    [InlineData("")]
    [InlineData("one mW")]
    [InlineData("1.0 mV")]
    public void Not_a_power(string text) => Assert.Null(PowerText.ParseWatts(text));
}

public class TransferCheckTests
{
    [Fact]
    public void Agreement_is_corrected_for_the_dc_calibrator_being_off_target()
    {
        // INT 1.0000 mW. EXT read 1.0007 mW with the calibrator 10 ppm high (145.00145 mV for 145.00 mV):
        // raw +0.0700%; corrected 1.0007 × (1/1.00001) − 1 = +0.0690%.
        var check = new TransferCheck(1.0000e-3, 0.14500, 0.14500145, 1.0007e-3, 0.002);
        Assert.Equal(7.00e-4, check.RawAgreement, 9);
        Assert.Equal(6.90e-4, check.Agreement, 7);
        Assert.True(check.WithinTolerance);
        Assert.False((check with { ExtWatts = 1.0025e-3 }).WithinTolerance);
    }

    [Fact]
    public void A_table_point_is_judged_on_the_tables_limits_without_scaling()
    {
        var row = H01Table.LoadDefault().Rows.Single(r => r.SettingVolts == 1.4588);
        var inside = new H01PointResult(row, 1.45881, 10.03e-3);
        Assert.True(inside.WithinLimits);
        Assert.Equal(6.855e-6, inside.SourceError, 1e-9);
        Assert.Equal(3e-3, inside.Deviation, 9);
        Assert.False(new H01PointResult(row, 1.4588, 10.05e-3).WithinLimits);
    }
}

public class PowerMeterDriverTests
{
    [Fact]
    public void E441x_reads_in_the_units_the_meter_shows()
    {
        var session = new ScriptedVisaSession()
            .Answer("*IDN?", "Agilent Technologies,E4418B,GB12345678,A1.06.00")
            .Answer("SYST:ERR?", "+0,\"No error\"")
            .AnswerOnce("UNIT1:POW?", "DBM").AnswerOnce("FETC1?", "+1.00000000E+000")
            .AnswerOnce("UNIT1:POW?", "W").AnswerOnce("FETC1?", "+1.00030000E-003");
        var meter = new E441xPowerMeter(session);
        meter.Identify();
        Assert.Equal("Agilent Technologies E4418B s/n GB12345678, fw A1.06.00, channel 1", meter.Description);
        Assert.Equal(1.2589254e-3, meter.ReadWatts(), 9);   // +1 dBm
        Assert.Equal(1.0003e-3, meter.ReadWatts(), 12);
        Assert.Empty(session.Commands);                      // queries only: the set-up is left alone
    }

    [Fact]
    public void E441x_reports_a_meter_error()
    {
        var session = new ScriptedVisaSession()
            .Answer("UNIT1:POW?", "W").Answer("FETC1?", "9.91E+37").Answer("SYST:ERR?", "-230,\"Data corrupt or stale\"");
        Assert.Throws<PowerMeterException>(() => new E441xPowerMeter(session).ReadWatts());
    }

    [Fact]
    public void Hp437B_selects_watts_takes_a_settled_reading_and_returns_to_free_run()
    {
        var session = new ScriptedVisaSession().Answer("*IDN?", "HEWLETT-PACKARD,437B,,2.1").Answer("TR2", "1.0002E-03");
        var meter = new Hp437BPowerMeter(session);
        meter.Identify();
        Assert.Equal(1.0002e-3, meter.ReadWatts());
        Assert.Equal(["*IDN?", "LN", "TR2", "TR3"], session.Sent);
        Assert.Equal("HP 437B, fw 2.1", meter.Description);
    }

    [Fact]
    public void Hp438A_reads_sensor_a_in_watts()
    {
        var session = new ScriptedVisaSession().Answer("?ID", "HP438A,VER1.20").Answer("TR2", "9.9987E-04");
        var meter = new Hp438APowerMeter(session);
        meter.Identify();
        Assert.Equal(9.9987e-4, meter.ReadWatts());
        Assert.Equal(["?ID", "AP", "LN", "TR2", "TR3"], session.Sent);
    }

    [Fact]
    public void The_wrong_meter_is_refused()
    {
        var session = new ScriptedVisaSession().Answer("*IDN?", "HEWLETT-PACKARD,34401A,0,11-5-2");
        Assert.Throws<PowerMeterException>(() => new E441xPowerMeter(session).Identify());
        Assert.Throws<PowerMeterException>(() => new Hp437BPowerMeter(session).Identify());
        Assert.Throws<ArgumentException>(() => PowerMeters.Open("432A", session, 1));
    }
}

/// <summary>The simulated 11683A and power meter behave as the E4418B procedure expects.</summary>
public class H01SimulationTests
{
    private static readonly CalibratorLimits Limits = CalibratorLimits.LoadDefault();

    private static (Simulated11683A Calibrator, SimulatedPowerMeter Meter, SimulatedClock Clock) Bench(SimulationScenario? scenario = null)
    {
        var clock = new SimulatedClock();
        var calibrator = new Simulated11683A(Limits, scenario ?? SimulationScenario.Get("nominal"), clock);
        return (calibrator, new SimulatedPowerMeter(calibrator) { NoiseFraction = 0 }, clock);
    }

    [Fact]
    public void Int_at_1_mW_reads_1_mW_after_zero_and_cal()
    {
        var (calibrator, meter, _) = Bench();
        calibrator.SetRange("1mW");
        meter.Zero();
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        meter.Cal();
        Assert.Equal(1e-3, meter.ReadWatts(), 12);
    }

    [Fact]
    public void Ext_fed_with_the_int_1_mW_voltage_agrees_within_0_05_percent()
    {
        var (calibrator, meter, _) = Bench();
        calibrator.SetRange("1mW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        meter.Cal();
        var intWatts = meter.ReadWatts();

        calibrator.LinePowered = false;
        calibrator.SetRange("100mW");
        calibrator.SetReference(ReferenceSwitch.Ext);
        calibrator.ExternalSourceVolts = calibrator.SettledVolts(RangeSwitch.Get("1mW"));   // 145.00 mV
        var extWatts = meter.ReadWatts();

        // About 145.00 / 144.93 − 1 = +0.048%, from the table.
        Assert.Equal(H01Table.LoadDefault().EffectiveWatts(0.145) / 1e-3 - 1, extWatts / intWatts - 1, 9);
        Assert.InRange(extWatts / intWatts - 1, 4.7e-4, 4.9e-4);
    }

    [Fact]
    public void Int_needs_line_power_and_ext_does_not()
    {
        var (calibrator, _, _) = Bench();
        calibrator.SetRange("1mW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        calibrator.LinePowered = false;
        Assert.Equal(0, calibrator.OutputPowerWatts());
        calibrator.SetRange("100mW");
        calibrator.SetReference(ReferenceSwitch.Ext);
        calibrator.ExternalSourceVolts = 0.14493;
        Assert.Equal(1e-3, calibrator.OutputPowerWatts(), 12);
    }

    [Fact]
    public void Every_table_row_reads_its_power_after_cal_at_144_93_mV()
    {
        var (calibrator, meter, _) = Bench();
        calibrator.SetRange("100mW");
        calibrator.SetReference(ReferenceSwitch.Ext);
        calibrator.LinePowered = false;
        meter.Zero();
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        calibrator.ExternalSourceVolts = 0.14493;
        meter.Cal();
        foreach (var row in H01Table.LoadDefault().Rows)
        {
            calibrator.ExternalSourceVolts = row.SettingVolts;
            var reading = meter.ReadWatts();
            Assert.InRange(reading, row.LowerWatts, row.UpperWatts);
        }
    }

    [Fact]
    public void The_dvm_on_the_ref_ext_tee_reads_the_dc_calibrator()
    {
        var (calibrator, _, clock) = Bench();
        var dvm = new SimulatedDvm(calibrator, clock);
        calibrator.DvmAt = DvmPoint.RefExtTee;
        calibrator.ExternalSourceVolts = 0.145012;
        dvm.Configure(DvmFunction.DcVoltage, 1, 10);
        Assert.Equal(0.145012, dvm.Read().Value, 5);
    }

    [Fact]
    public void An_ext_transfer_error_shows_in_the_agreement()
    {
        var (calibrator, meter, _) = Bench(SimulationScenario.Get("nominal") with { ExtTransferError = 0.004 });
        calibrator.SetRange("1mW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        meter.Cal();
        var intWatts = meter.ReadWatts();
        calibrator.SetRange("100mW");
        calibrator.SetReference(ReferenceSwitch.Ext);
        calibrator.ExternalSourceVolts = 0.145;
        Assert.Equal(H01Table.LoadDefault().EffectiveWatts(0.145) / 1e-3 * 1.004 - 1, meter.ReadWatts() / intWatts - 1, 9);
    }
}
