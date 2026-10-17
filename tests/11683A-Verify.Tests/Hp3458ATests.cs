using Verify11683A.Instruments.Dvm;
using Verify11683A.Tests.Fakes;

namespace Verify11683A.Tests;

public class Hp3458ATests
{
    private static ScriptedVisaSession Meter() =>
        new ScriptedVisaSession()
            .Answer("ID?", "HP3458A")
            .Answer("REV?", "9,2")
            .Answer("ERRSTR?", "0,\"NO ERROR\"")
            .Answer("TRIG SGL", "1.45012345E-1");

    [Fact]
    public void Configure_dc_volts_holds_the_trigger_and_selects_the_high_impedance_input()
    {
        var session = Meter();
        var config = new Hp3458A(session).Configure(DvmFunction.DcVoltage, 1, 100);

        Assert.Equal(["TRIG HOLD", "FUNC DCV,1", "NPLC 100", "AZERO ON", "FIXEDZ OFF", "NDIG 8"], session.Commands);
        Assert.True(config.HighImpedance);
    }

    [Fact]
    public void Configure_four_wire_ohms_uses_offset_compensation()
    {
        var session = Meter();
        new Hp3458A(session).Configure(DvmFunction.FourWireResistance, 100, 100);

        Assert.Equal(["TRIG HOLD", "FUNC OHMF,100", "NPLC 100", "AZERO ON", "OCOMP ON", "NDIG 8"], session.Commands);
    }

    [Fact]
    public void Read_triggers_a_single_reading()
    {
        var session = Meter();
        var dvm = new Hp3458A(session);
        dvm.Configure(DvmFunction.DcVoltage, 1, 100);
        var reading = dvm.Read();

        Assert.Equal("TRIG SGL", session.Sent[^1]);
        Assert.Equal(0.145012345, reading.Value, 12);
    }

    [Fact]
    public void Reset_presets_and_holds_the_trigger()
    {
        var session = Meter();
        new Hp3458A(session).Reset();

        Assert.Equal(1, session.Clears);
        Assert.Equal(["PRESET NORM", "TRIG HOLD", "END ALWAYS"], session.Commands);
    }

    [Fact]
    public void Configure_reports_errors_from_ERRSTR()
    {
        var session = Meter().AnswerOnce("ERRSTR?", "1024,\"INVALID RANGE\"");
        var ex = Assert.Throws<DvmException>(() => new Hp3458A(session).Configure(DvmFunction.DcVoltage, 1, 100));
        Assert.Contains("INVALID RANGE", ex.Message);
    }

    [Fact]
    public void Identify_includes_the_firmware_revision()
    {
        var identity = new Hp3458A(Meter()).Identify();
        Assert.Equal("3458A", identity.Model);
        Assert.Equal("9,2", identity.Firmware);
    }
}
