using Verify11683A.Instruments.Dvm;
using Verify11683A.Tests.Fakes;

namespace Verify11683A.Tests;

public class Keithley2001Tests
{
    /// <summary>A 2001 that accepts everything and reports DC volts on the 200 mV range at NPLC 10, 7½ digits.</summary>
    private static ScriptedVisaSession Meter() =>
        new ScriptedVisaSession()
            .Answer("*IDN?", "KEITHLEY INSTRUMENTS INC.,MODEL 2001,0587153,B17 /A02")
            .Answer(":CONF?", "\"VOLT:DC\"")
            .Answer(":SENS:VOLT:DC:RANG?", "2.000000E-01")
            .Answer(":SENS:VOLT:DC:NPLC?", "1.000000E+01")
            .Answer(":SENS:VOLT:DC:DIG?", "8")
            .Answer(":SENS:VOLT:DC:AVER?", "0")
            .Answer(":SENS:VOLT:DC:AVER:COUN?", "10")
            .Answer(":SENS:VOLT:DC:AVER:ADV?", "0")
            .Answer(":SYST:AZER:STAT?", "1")
            .Answer(":SYST:ERR?", "0,\"No error\"")
            .Answer(":READ?", "+1.45012340E-01");

    /// <summary>The same meter on 4-wire ohms at <paramref name="range"/>.</summary>
    private static ScriptedVisaSession OhmsMeter(string range, string offsetCompensation) =>
        Meter()
            .Answer(":CONF?", "\"FRES\"")
            .Answer(":SENS:FRES:RANG?", range)
            .Answer(":SENS:FRES:NPLC?", "1.000000E+01")
            .Answer(":SENS:FRES:DIG?", "8")
            .Answer(":SENS:FRES:AVER?", "0")
            .Answer(":SENS:FRES:AVER:ADV?", "0")
            .Answer(":SENS:FRES:OCOM?", offsetCompensation);

    [Fact]
    public void Configure_dc_volts_sends_CONF_first_and_autozero_after_it()
    {
        var session = Meter();
        var config = new Keithley2001(session).Configure(DvmFunction.DcVoltage, 0.2, 10);

        Assert.Equal(
        [
            ":CONF:VOLT:DC", ":SENS:VOLT:DC:RANG 0.2", ":SENS:VOLT:DC:NPLC 10", ":SENS:VOLT:DC:DIG 8",
            ":SENS:VOLT:DC:AVER:ADV OFF", ":SENS:VOLT:DC:AVER OFF", ":SYST:AZER:TYPE SYNC", ":SYST:AZER:STAT ON",
        ], session.Commands);
        Assert.True(config.HighImpedance);
        Assert.Equal(">10 GΩ", config.InputResistance);
        Assert.Equal(10, config.Nplc);
    }

    [Fact]
    public void Nplc_100_is_ten_10_PLC_readings_on_the_repeating_filter()
    {
        var session = Meter()
            .Answer(":SENS:VOLT:DC:AVER?", "1");
        var config = new Keithley2001(session).Configure(DvmFunction.DcVoltage, 0.2, 100);

        Assert.Contains(":SENS:VOLT:DC:NPLC 10", session.Commands);
        Assert.Contains(":SENS:VOLT:DC:AVER:TCON REP", session.Commands);
        Assert.Contains(":SENS:VOLT:DC:AVER:COUN 10", session.Commands);
        Assert.Contains(":SENS:VOLT:DC:AVER ON", session.Commands);
        Assert.Equal(100, config.Nplc);
    }

    [Theory]
    [InlineData(0.5, 0.5, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(100, 10, 10)]
    [InlineData(30, 10, 3)]
    [InlineData(5000, 10, 100)]   // the filter holds 100 at most
    public void Integration_splits_long_NPLC_into_filter_readings(double nplc, double expectedNplc, int expectedCount)
    {
        var (n, count) = Keithley2001.Integration(nplc);
        Assert.Equal(expectedNplc, n);
        Assert.Equal(expectedCount, count);
    }

    [Fact]
    public void The_200_V_range_has_no_high_impedance_input_and_reports_10_megohm()
    {
        var session = Meter().Answer(":SENS:VOLT:DC:RANG?", "2.000000E+02");
        var config = new Keithley2001(session).Configure(DvmFunction.DcVoltage, 200, 10);

        Assert.False(config.HighImpedance);
        Assert.Equal("10 MΩ ±1%", config.InputResistance);
    }

    [Fact]
    public void Configure_throws_when_the_meter_reports_another_range()
    {
        var session = Meter().Answer(":SENS:VOLT:DC:RANG?", "2.000000E+00");
        var ex = Assert.Throws<DvmException>(() => new Keithley2001(session).Configure(DvmFunction.DcVoltage, 0.2, 10));
        Assert.Contains("range", ex.Message);
    }

    [Fact]
    public void Configure_throws_when_the_advanced_filter_stays_on()
    {
        var session = Meter().Answer(":SENS:VOLT:DC:AVER:ADV?", "1");
        var ex = Assert.Throws<DvmException>(() => new Keithley2001(session).Configure(DvmFunction.DcVoltage, 0.2, 10));
        Assert.Contains("advanced filter", ex.Message);
    }

    [Fact]
    public void Configure_throws_when_autozero_is_off()
    {
        var session = Meter().Answer(":SYST:AZER:STAT?", "0");
        Assert.Throws<DvmException>(() => new Keithley2001(session).Configure(DvmFunction.DcVoltage, 0.2, 10));
    }

    [Fact]
    public void Configure_throws_and_reports_queued_errors()
    {
        var session = Meter()
            .AnswerOnce(":SYST:ERR?", "-113,\"Undefined header\"");
        var ex = Assert.Throws<DvmException>(() => new Keithley2001(session).Configure(DvmFunction.DcVoltage, 0.2, 10));
        Assert.Contains("Undefined header", ex.Message);
    }

    [Fact]
    public void Four_wire_ohms_turns_offset_compensation_on_up_to_20_kilohm()
    {
        var session = OhmsMeter("2.000000E+04", "1");
        var config = new Keithley2001(session).Configure(DvmFunction.FourWireResistance, 2e4, 10);

        Assert.Contains(":SENS:FRES:OCOM ON", session.Commands);
        Assert.Contains("offset compensation on", config.InputResistance);
    }

    [Fact]
    public void Four_wire_ohms_on_200_kilohm_has_no_offset_compensation()
    {
        var session = OhmsMeter("2.000000E+05", "0");
        new Keithley2001(session).Configure(DvmFunction.FourWireResistance, 2e5, 10);

        Assert.Contains(":SENS:FRES:OCOM OFF", session.Commands);
    }

    [Fact]
    public void Configure_rejects_a_range_the_meter_does_not_have()
    {
        var dvm = new Keithley2001(Meter());
        Assert.Throws<ArgumentOutOfRangeException>(() => dvm.Configure(DvmFunction.DcVoltage, 1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => dvm.Configure(DvmFunction.FourWireResistance, 2e6, 10));
    }

    [Fact]
    public void Read_parses_the_reading_and_flags_overflow()
    {
        var session = Meter();
        var dvm = new Keithley2001(session);
        dvm.Configure(DvmFunction.DcVoltage, 0.2, 10);

        Assert.Equal(0.14501234, dvm.Read().Value, 12);
        session.Answer(":READ?", "+9.9E37");
        Assert.True(dvm.Read().Overload);
    }

    [Theory]
    [InlineData("+1.45012340E-01", 0.14501234)]
    [InlineData("+1.45012340E-01NVDC", 0.14501234)]
    [InlineData("-4.58310000E-04NVDC,+0001234.123456secs", -0.00045831)]
    public void ParseReading_drops_units_and_other_elements(string reply, double expected) =>
        Assert.Equal(expected, Keithley2001.ParseReading(reply), 12);

    [Fact]
    public void ParseReading_counts_an_overflow_status_as_overload() =>
        Assert.True(ScpiNumber.IsOverload(Keithley2001.ParseReading("+2.10000000E-01OVDC")));

    [Fact]
    public void Identify_parses_IDN_and_rejects_another_model()
    {
        var id = new Keithley2001(Meter()).Identify();
        Assert.Equal("2001", id.Model);
        Assert.Equal("0587153", id.Serial);
        Assert.Equal("B17 /A02", id.Firmware);

        var other = Meter().Answer("*IDN?", "HEWLETT-PACKARD,34401A,0,11-5-2");
        Assert.Throws<DvmException>(() => new Keithley2001(other).Identify());
    }

    [Fact]
    public void Reset_selects_one_shot_ASCII_readings_with_the_reading_alone()
    {
        var session = Meter();
        new Keithley2001(session).Reset();

        Assert.Equal(1, session.Clears);
        Assert.Equal(["*RST", "*CLS", ":INIT:CONT OFF", ":ABOR", ":FORM:DATA ASC", ":FORM:ELEM READ"], session.Commands);
    }

    [Theory]
    [InlineData("1", DvmTerminals.Front)]
    [InlineData("0", DvmTerminals.Rear)]
    [InlineData("2", DvmTerminals.Unknown)]
    public void FRSW_reads_the_inputs_switch(string reply, DvmTerminals expected)
    {
        var session = Meter().Answer(":SYST:FRSW?", reply);
        Assert.Equal(expected, new Keithley2001(session).ReadTerminals());
    }

    [Fact]
    public void It_reads_5_percent_past_each_range() => Assert.Equal(1.05, ((IDvm)new Keithley2001(Meter())).OverrangeFactor);
}
