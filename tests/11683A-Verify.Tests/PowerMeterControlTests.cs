using Verify11683A.Instruments.PowerMeter;
using Verify11683A.Instruments.Visa;
using Verify11683A.Meters;
using Verify11683A.Tests.Fakes;

namespace Verify11683A.Tests;

/// <summary>The bus sequences that set up, zero and CAL each meter, as its manual gives them.</summary>
public class PowerMeterControlTests
{
    private static readonly Action<TimeSpan> NoSleep = _ => { };

    private static ScriptedVisaSession E441x(string model = "E4418B") => new ScriptedVisaSession()
        .Answer("*IDN?", $"Agilent Technologies,{model},GB12345678,A1.06.00")
        .Answer("SYST:ERR?", "+0,\"No error\"")
        .Answer("*OPC?", "1")
        .Answer("SENS1:CORR:CSET1:STAT?", "0")
        .Answer("SENS2:CORR:CSET1:STAT?", "0");

    [Fact]
    public void E441x_turns_a_sensor_table_off_only_when_one_is_on()
    {
        // With none selected, STAT OFF is refused with -221 "Settings conflict" (E4419B A2.09.01 on the bench).
        var none = E441x();
        new E441xPowerMeter(none).Preset();
        Assert.DoesNotContain("SENS1:CORR:CSET1:STAT OFF", none.Sent);

        var active = E441x().Answer("SENS1:CORR:CSET1:STAT?", "1");
        new E441xPowerMeter(active).Preset();
        Assert.Contains("SENS1:CORR:CSET1:STAT OFF", active.Sent);
    }

    [Fact]
    public void E441x_presets_to_single_settled_readings_then_zeroes_and_CALs_on_the_sensor_input()
    {
        var session = E441x().Answer("READ1?", "+3.16200000E-006");
        var meter = new E441xPowerMeter(session);
        meter.Identify();
        meter.Preset();
        meter.Apply(new MeterSettings { Display = MeterDisplay.Watts, Filter = 512, Resolution = 4 }, null);
        meter.Zero();
        meter.Calibrate(100);
        Assert.Equal(3.162e-6, meter.Read(), 12);

        Assert.Equal([
            "SYST:PRES", "*CLS", "INIT1:CONT OFF;:TRIG1:SOUR IMM;:TRIG1:DEL:AUTO ON", "UNIT1:POW W",
            "UNIT1:POW W", "SENS1:AVER:STAT ON;:SENS1:AVER:COUN:AUTO OFF;:SENS1:AVER:COUN 512", "DISP:WIND1:RES 4", "SENS1:CORR:CFAC 100",
            "CAL1:ZERO:AUTO ONCE",
            "CAL1:RCF 100", "CAL1:AUTO ONCE",
            "INIT1:CONT OFF",
        ], session.Commands);
        Assert.DoesNotContain(session.Sent, c => c.StartsWith("CAL1:ALL", StringComparison.Ordinal));   // would zero with 1 mW applied
    }

    [Fact]
    public void E441x_waits_long_enough_for_a_512_reading_filter()
    {
        var session = E441x().Answer("READ1?", "+1.0E-003");
        var meter = new E441xPowerMeter(session);
        meter.Identify();
        meter.Apply(new MeterSettings { Filter = 512 }, null);
        session.TimeoutMilliseconds = 20000;
        meter.Read();
        Assert.Equal(20000, session.TimeoutMilliseconds);   // restored after the read
    }

    [Fact]
    public void E441x_clears_a_stuck_reading_so_a_retry_starts_clean()
    {
        // E4419B on the bench, channel B: a READ2? at filter 512 never answered, and the Retry queued behind it.
        var session = E441x("E4419B").TimeOutOnce("READ2? DEF,DEF,(@2)").Answer("READ2? DEF,DEF,(@2)", "-1.0E-009");
        var meter = new E441xPowerMeter(session, 2);
        meter.Identify();
        meter.Apply(new MeterSettings { Filter = 512 }, null);
        session.TimeoutMilliseconds = 20000;
        session.AnswerOnce("SYST:ERR?", "-213,\"Init ignored\"");

        var ex = Assert.Throws<InstrumentTimeoutException>(() => meter.Read());
        Assert.Equal(1, session.Clears);
        Assert.Contains("device clear", ex.Message);
        Assert.Contains("-213,\"Init ignored\"", ex.Message);
        Assert.Equal(20000, session.TimeoutMilliseconds);

        Assert.Equal(-1e-9, meter.Read());   // the retry
    }

    [Fact]
    public void E441x_recovery_reads_the_error_queue_to_the_end()
    {
        // Eight errors queued: all are read, so none is left to fail the retry's own SYST:ERR? check.
        var session = E441x("E4419B").TimeOutOnce("READ2? DEF,DEF,(@2)").Answer("READ2? DEF,DEF,(@2)", "-1.0E-009");
        var meter = new E441xPowerMeter(session, 2);
        meter.Identify();
        for (var i = 0; i < 4; i++)
            session.AnswerOnce("SYST:ERR?", "-213,\"Init ignored\"").AnswerOnce("SYST:ERR?", "-420,\"Query UNTERMINATED\"");

        var ex = Assert.Throws<InstrumentTimeoutException>(() => meter.Read());
        Assert.Contains("-213,\"Init ignored\" (×4); -420,\"Query UNTERMINATED\" (×4)", ex.Message);
        Assert.Equal(-1e-9, meter.Read());   // the retry is not failed by a stale error
    }

    [Fact]
    public void E441x_recovery_clears_a_queue_that_will_not_empty()
    {
        var session = E441x("E4419B").TimeOutOnce("READ2? DEF,DEF,(@2)").Answer("READ2? DEF,DEF,(@2)", "-1.0E-009");
        var meter = new E441xPowerMeter(session, 2);
        meter.Identify();
        for (var i = 0; i < 31; i++)
            session.AnswerOnce("SYST:ERR?", "-350,\"Queue overflow\"");

        var ex = Assert.Throws<InstrumentTimeoutException>(() => meter.Read());
        Assert.Contains("did not empty after 31 errors", ex.Message);
        Assert.Contains("*CLS", session.Sent);
    }

    [Fact]
    public void E441x_turns_free_run_off_before_every_reading()
    {
        // A front-panel key puts the meter in local, which sets free run, and READ? then hangs with -213 INIT ignored.
        var session = E441x("E4419B").Answer("READ2? DEF,DEF,(@2)", "-1.0E-009");
        var meter = new E441xPowerMeter(session, 2);
        meter.Identify();
        meter.Read();
        meter.Read();
        var sent = session.Sent.Where(c => c.StartsWith("INIT2", StringComparison.Ordinal) || c.StartsWith("READ2", StringComparison.Ordinal));
        Assert.Equal(["INIT2:CONT OFF", "READ2? DEF,DEF,(@2)", "INIT2:CONT OFF", "READ2? DEF,DEF,(@2)"], sent);
    }

    [Fact]
    public void E4419B_channel_B_is_set_up_and_read_on_channel_2()
    {
        var session = E441x("E4419B").Answer("READ2? DEF,DEF,(@2)", "+1.0E-003");
        var meter = new E441xPowerMeter(session, 2);
        meter.Identify();
        meter.Preset();
        meter.Zero();
        Assert.Equal(1e-3, meter.Read());
        Assert.Contains("CAL2:ZERO:AUTO ONCE", session.Sent);
        Assert.Contains("UNIT2:POW W", session.Sent);
    }

    [Fact]
    public void E441x_reports_a_failed_zero()
    {
        var session = E441x().Answer("SYST:ERR?", "-231,\"Data Questionable;ZERO ERROR\"");
        var ex = Assert.Throws<PowerMeterException>(() => new E441xPowerMeter(session).Zero());
        Assert.Contains("ZERO ERROR", ex.Message);
    }

    [Fact]
    public void Hp437B_zeroes_and_CALs_with_CS_first_and_watches_the_status_byte_without_sending_anything()
    {
        var session = new ScriptedVisaSession().Answer("TR2", "+1.0002E-03");
        session.StatusBytes.Enqueue(0);
        session.StatusBytes.Enqueue(0);
        session.StatusBytes.Enqueue(2);   // Cal/Zero Complete
        session.StatusBytes.Enqueue(2);
        var meter = new Hp437BPowerMeter(session, NoSleep);
        meter.Preset();
        meter.Apply(new MeterSettings { Display = MeterDisplay.Watts, Range = 0, Filter = 0 }, null);
        meter.Zero();
        meter.Calibrate(100);
        meter.Apply(new MeterSettings { Range = 3 }, null);
        Assert.Equal(1.0002e-3, meter.Read());

        Assert.Equal(["PRLN", "LNRAFAKB100.0EN", "CSZE", "CSCL100.0EN", "RM3ENKB100.0EN", "TR2", "TR3"], session.Sent);
        Assert.Equal(4, session.SerialPolls);
    }

    [Fact]
    public void A_measurement_error_during_a_zero_is_reported()
    {
        var session = new ScriptedVisaSession();
        session.StatusBytes.Enqueue(8);
        Assert.Throws<PowerMeterException>(() => new Hp437BPowerMeter(session, NoSleep).Zero());
    }

    [Fact]
    public void A_zero_that_never_completes_times_out()
    {
        var session = new ScriptedVisaSession();
        Assert.Throws<PowerMeterException>(() => new Hp437BPowerMeter(session, NoSleep).Zero());
        Assert.Equal(60, session.SerialPolls);   // 30 s in 0.5 s steps
    }

    [Theory]
    [InlineData("9.0017E+40", "17")]
    [InlineData("9.0015E+40", "15")]
    public void Hp437B_error_readings_are_reported_with_their_code(string reply, string code)
    {
        var ex = Assert.Throws<PowerMeterException>(() => Hp437BPowerMeter.HpReading(reply, "437B"));
        Assert.Contains($"error {code}", ex.Message);
    }

    [Fact]
    public void Hp438A_selects_the_sensor_after_preset_and_CALs_on_the_external_reference()
    {
        var session = new ScriptedVisaSession().Answer("TR2", "-1.0000E+01");
        session.StatusBytes.Enqueue(2);
        session.StatusBytes.Enqueue(2);
        var meter = new Hp438APowerMeter(session, 'B', NoSleep);
        meter.Preset();
        meter.Zero();
        meter.Calibrate(100);
        meter.Apply(new MeterSettings { Display = MeterDisplay.Dbm }, null);
        Assert.Equal(-10, meter.Read());

        Assert.Equal(["PRBPLN", "CSZE", "CSCL-100.0EN", "LGKB100.0EN", "TR2", "TR3"], session.Sent);
    }

    [Fact]
    public void Hp438A_REL_takes_its_reference_once_and_is_turned_off_for_watts()
    {
        var session = new ScriptedVisaSession();
        var meter = new Hp438APowerMeter(session, 'A', NoSleep);
        meter.Apply(new MeterSettings { Display = MeterDisplay.DbRelative, StoreReference = true }, null);
        meter.Apply(new MeterSettings { Display = MeterDisplay.DbRelative }, null);
        meter.Apply(new MeterSettings { Display = MeterDisplay.Watts }, null);
        Assert.Equal(["LGRL1KB100.0EN", "LGKB100.0EN", "RL0LNKB100.0EN"], session.Sent);
    }

    [Fact]
    public void Hp436A_zeroes_remotely_and_reads_fixed_ranges_dBm_and_dB_REL()
    {
        var session = new ScriptedVisaSession()
            .AnswerOnce("Z1T", "TIA 0005E-09").AnswerOnce("Z1T", "TIA 0001E-09")
            .AnswerOnce("9A+T", "UIA 0000E-09").AnswerOnce("9A+T", "PIA 0000E-09")
            .Answer("2A+T", "QJA 0003E-09")
            .Answer("9D+T", "PKD-1000E-02")
            .Answer("9C+T", "PKC 0000E-02")
            .Answer("9B+T", "PJB-1000E-02");
        var meter = new Hp436APowerMeter(session, NoSleep);
        meter.Zero();
        meter.Apply(new MeterSettings { Display = MeterDisplay.Watts, Range = 2 }, null);
        Assert.Equal(3e-9, meter.Read(), 15);                     // under range on a fixed range is still a reading
        meter.Apply(new MeterSettings { Display = MeterDisplay.Dbm, Range = 0 }, null);
        Assert.Equal(-10, meter.Read(), 9);
        meter.Apply(new MeterSettings { Display = MeterDisplay.DbRelative, StoreReference = true }, null);
        Assert.Equal(-10, meter.Read(), 9);

        Assert.Equal(["Z1T", "Z1T", "9A+T", "9A+T", "2A+T", "9D+T", "9C+T", "9B+T"], session.Sent);
        Assert.False(meter.CanCalibrate);
        Assert.False(meter.CanSetCalFactor);
    }

    [Fact]
    public void Hp436A_uses_its_CAL_FACTOR_switch_only_on_cal_factor_rows()
    {
        var session = new ScriptedVisaSession().Answer("3A-T", "PKA 0990E-06");
        var meter = new Hp436APowerMeter(session, NoSleep);
        meter.Apply(new MeterSettings { Display = MeterDisplay.Watts, Range = 3 }, 99);
        Assert.Equal(0.99e-3, meter.Read(), 12);
    }

    [Fact]
    public void Hp436A_refuses_a_zero_with_power_applied()
    {
        var session = new ScriptedVisaSession().Answer("Z1T", "VIA 0500E-09");
        Assert.Throws<PowerMeterException>(() => new Hp436APowerMeter(session, NoSleep).Zero());
    }
}
