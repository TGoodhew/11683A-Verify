using Verify11683A.Instruments.Dvm;
using Verify11683A.Tests.Fakes;

namespace Verify11683A.Tests;

public class Hp34401ATests
{
    /// <summary>A 34401A that accepts everything and reports the requested settings.</summary>
    private static ScriptedVisaSession Meter(string conf = "\"VOLT +1.000000E+00,+1.000000E-06\"") =>
        new ScriptedVisaSession()
            .Answer("*IDN?", "HEWLETT-PACKARD,34401A,0,11-5-2")
            .Answer("CONF?", conf)
            .Answer("VOLT:DC:NPLC?", "+1.00000000E+02")
            .Answer("FRES:NPLC?", "+1.00000000E+02")
            .Answer("ZERO:AUTO?", "1")
            .Answer("INP:IMP:AUTO?", "1")
            .Answer("SYST:ERR?", "+0,\"No error\"")
            .Answer("READ?", "+1.45012340E-01");

    [Fact]
    public void Configure_dc_volts_sends_the_commands_in_order_with_high_impedance_after_CONF()
    {
        var session = Meter();
        new Hp34401A(session).Configure(DvmFunction.DcVoltage, 1, 100);

        Assert.Equal(["CONF:VOLT:DC 1", "VOLT:DC:NPLC 100", "ZERO:AUTO ON", "INP:IMP:AUTO ON"], session.Commands);
    }

    [Fact]
    public void Every_configure_re_sends_INP_IMP_AUTO_ON_because_CONF_turns_it_off()
    {
        var session = Meter();
        var dvm = new Hp34401A(session);
        session.Answer("CONF?", "\"VOLT +1.000000E-01,+1.000000E-07\"");
        dvm.Configure(DvmFunction.DcVoltage, 0.1, 100);
        session.Answer("CONF?", "\"VOLT +1.000000E+01,+1.000000E-05\"");
        dvm.Configure(DvmFunction.DcVoltage, 10, 100);

        var commands = session.Commands.ToList();
        Assert.Equal(2, commands.Count(c => c == "INP:IMP:AUTO ON"));
        // Each INP:IMP:AUTO ON follows its own CONF.
        Assert.True(commands.IndexOf("INP:IMP:AUTO ON") > commands.IndexOf("CONF:VOLT:DC 0.1"));
        Assert.True(commands.LastIndexOf("INP:IMP:AUTO ON") > commands.IndexOf("CONF:VOLT:DC 10"));
    }

    [Fact]
    public void The_100_V_range_has_no_high_impedance_input_and_reports_10_megohm()
    {
        var session = Meter("\"VOLT +1.000000E+02,+1.000000E-04\"");
        var config = new Hp34401A(session).Configure(DvmFunction.DcVoltage, 100, 100);

        Assert.DoesNotContain("INP:IMP:AUTO ON", session.Commands);
        Assert.DoesNotContain("INP:IMP:AUTO?", session.Sent);
        Assert.False(config.HighImpedance);
        Assert.Equal("10 MΩ ±1%", config.InputResistance);
    }

    [Fact]
    public void Configure_throws_when_the_high_impedance_input_did_not_take()
    {
        var session = Meter().Answer("INP:IMP:AUTO?", "0");
        var ex = Assert.Throws<DvmException>(() => new Hp34401A(session).Configure(DvmFunction.DcVoltage, 1, 100));
        Assert.Contains("INP:IMP:AUTO", ex.Message);
    }

    [Fact]
    public void Configure_throws_when_CONF_reports_another_range()
    {
        var session = Meter("\"VOLT +1.000000E+01,+1.000000E-05\"");
        Assert.Throws<DvmException>(() => new Hp34401A(session).Configure(DvmFunction.DcVoltage, 1, 100));
    }

    [Fact]
    public void Configure_throws_and_reports_queued_errors()
    {
        var session = Meter()
            .AnswerOnce("SYST:ERR?", "-113,\"Undefined header\"")
            .AnswerOnce("SYST:ERR?", "+0,\"No error\"");
        var ex = Assert.Throws<DvmException>(() => new Hp34401A(session).Configure(DvmFunction.DcVoltage, 1, 100));
        Assert.Contains("-113", ex.Message);
    }

    [Fact]
    public void Configure_four_wire_ohms()
    {
        var session = Meter("\"FRES +1.000000E+04,+1.000000E-02\"");
        var config = new Hp34401A(session).Configure(DvmFunction.FourWireResistance, 1e4, 100);

        Assert.Equal(["CONF:FRES 10000", "FRES:NPLC 100", "ZERO:AUTO ON"], session.Commands);
        Assert.Equal(DvmFunction.FourWireResistance, config.Function);
    }

    [Fact]
    public void Configure_rejects_a_range_the_meter_does_not_have()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Hp34401A(Meter()).Configure(DvmFunction.DcVoltage, 3, 100));
    }

    [Fact]
    public void Read_parses_the_reading_and_flags_overload()
    {
        var session = Meter();
        var dvm = new Hp34401A(session);
        dvm.Configure(DvmFunction.DcVoltage, 1, 100);

        var reading = dvm.Read();
        Assert.Equal(0.14501234, reading.Value, 12);
        Assert.False(reading.Overload);

        session.Answer("READ?", "+9.90000000E+37");
        Assert.True(dvm.Read().Overload);
    }

    [Fact]
    public void Identify_parses_IDN_and_rejects_another_model()
    {
        var identity = new Hp34401A(Meter()).Identify();
        Assert.Equal("34401A", identity.Model);
        Assert.Equal("11-5-2", identity.Firmware);
        Assert.Equal("0", identity.Serial);

        var wrong = Meter().Answer("*IDN?", "HEWLETT-PACKARD,34420A,0,1-1-1");
        Assert.Throws<DvmException>(() => new Hp34401A(wrong).Identify());
    }

    [Fact]
    public void Read_errors_drains_the_queue()
    {
        var session = Meter()
            .AnswerOnce("SYST:ERR?", "-222,\"Data out of range\"")
            .AnswerOnce("SYST:ERR?", "-113,\"Undefined header\"");
        var errors = new Hp34401A(session).ReadErrors();
        Assert.Equal(2, errors.Count);
    }

    [Theory]
    [InlineData("\"VOLT +1.000000E+00,+1.000000E-06\"", "VOLT", 1.0)]
    [InlineData("\"FRES +1.000000E+02,+1.000000E-04\"", "FRES", 100.0)]
    [InlineData("VOLT +1.000000E-01,+1.000000E-07", "VOLT", 0.1)]
    public void ParseConf_reads_function_and_range(string reply, string function, double range)
    {
        var (f, r) = Hp34401A.ParseConf(reply);
        Assert.Equal(function, f);
        Assert.Equal(range, r, 12);
    }
}
