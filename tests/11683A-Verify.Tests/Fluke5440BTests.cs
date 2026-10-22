using Verify11683A.Instruments.DcSource;
using Verify11683A.Tests.Fakes;

namespace Verify11683A.Tests;

public class Fluke5440BTests
{
    /// <summary>A 5440B in Voltage mode, internal sense and guard, Standby (GSTS 81, as read on the bench), with no errors.</summary>
    private static ScriptedVisaSession Calibrator(string status = "81") =>
        new ScriptedVisaSession()
            .Answer("GVRS", " 2.1")
            .Answer("GSTS", status)
            .Answer("GERR", "0")
            .Answer("GSPB", "16");

    private static readonly List<TimeSpan> Slept = [];

    private static Fluke5440B Open(ScriptedVisaSession session, bool useDivider = true) =>
        new(session, useDivider, Slept.Add);

    [Fact]
    public void Identify_sets_standby_at_0_V_voltage_mode_and_internal_sense_and_guard_then_checks_them()
    {
        var session = Calibrator();
        var source = Open(session);
        source.Identify();

        Assert.Equal(["GVRS", "GSTS", "STBY,BSTO,DIVN,SOUT 0,ISNS,IGRD", "GERR", "GSTS"], session.Sent);
        Assert.Equal("Fluke 5440B, software 2.1", source.Description);
    }

    [Fact]
    public void Identify_rejects_an_instrument_that_does_not_answer_GVRS_with_a_version()
    {
        var session = Calibrator().Answer("GVRS", "HEWLETT-PACKARD,34401A,0,11-5-2");
        Assert.Throws<DcSourceException>(() => Open(session).Identify());
    }

    [Fact]
    public void Identify_takes_it_out_of_boost_and_external_guard()
    {
        // Voltage Boost with external sense and guard (4), then Voltage mode, internal sense and guard (81) once set.
        var session = Calibrator().AnswerOnce("GSTS", "4").AnswerOnce("GSTS", "81");
        Open(session).Identify();
        Assert.Contains("STBY,BSTO,DIVN,SOUT 0,ISNS,IGRD", session.Sent);
    }

    [Fact]
    public void Identify_says_so_if_the_settings_do_not_take()
    {
        var session = Calibrator().AnswerOnce("GSTS", "81").AnswerOnce("GSTS", "17");   // still external guard
        var ex = Assert.Throws<DcSourceException>(() => Open(session).Identify());
        Assert.Contains("internal guard", ex.Message);
    }

    [Fact]
    public void Identify_refuses_the_rear_outputs_and_sends_nothing()
    {
        var session = Calibrator(status: "209");   // Voltage, internal sense and guard, rear output
        var ex = Assert.Throws<DcSourceException>(() => Open(session).Identify());
        Assert.Contains("rear terminals", ex.Message);
        Assert.Equal(["GVRS", "GSTS"], session.Sent);
    }

    [Theory]
    [InlineData(0.14493, true, "DIVY,SOUT 0.14493,OPER")]
    [InlineData(0.00045831, true, "DIVY,SOUT 0.00045831,OPER")]
    [InlineData(1.4588, true, "DIVY,SOUT 1.4588,OPER")]
    [InlineData(4.6872, true, "DIVN,SOUT 4.6872,OPER")]
    [InlineData(15.588, true, "DIVN,SOUT 15.588,OPER")]
    [InlineData(0.14493, false, "DIVN,SOUT 0.14493,OPER")]
    public void Output_uses_the_divider_at_2_2_V_and_below_when_allowed(double volts, bool useDivider, string statement)
    {
        var session = Calibrator();
        Open(session, useDivider).Output(volts);
        Assert.Equal(statement, session.Sent[0]);
        Assert.Equal("GERR", session.Sent[1]);
    }

    [Theory]
    [InlineData(0.000458312345, true, "0.00045831")]   // divider 220 mV range: 0.01 µV
    [InlineData(0.14493000449, true, "0.14493")]
    [InlineData(1.45880004, true, "1.4588")]           // divider 2.2 V range: 0.1 µV
    [InlineData(1.45880006, true, "1.4588001")]
    [InlineData(15.5880004, false, "15.588")]          // 22 V range: 1 µV
    [InlineData(0.1450087999, false, "0.145009")]
    public void Settings_are_rounded_to_the_range_resolution(double volts, bool divider, string text) =>
        Assert.Equal(text, Fluke5440B.Format(volts, divider));

    [Fact]
    public void Output_waits_for_the_settled_bit_then_the_2_ppm_settling_time()
    {
        Slept.Clear();
        var session = Calibrator().AnswerOnce("GSPB", "0").AnswerOnce("GSPB", "8");
        Open(session).Output(0.14493);

        Assert.Equal(3, session.Sent.Count(c => c == "GSPB"));
        Assert.Equal(Fluke5440B.SettleTime, Slept[^1]);
    }

    [Fact]
    public void Output_above_22_V_is_refused_before_anything_is_sent()
    {
        var session = Calibrator();
        Assert.Throws<DcSourceException>(() => Open(session).Output(23));
        Assert.Empty(session.Sent);
    }

    [Fact]
    public void A_remote_error_after_a_statement_is_reported()
    {
        var session = Calibrator().Answer("GERR", "34");
        var ex = Assert.Throws<DcSourceException>(() => Open(session).Standby());
        Assert.Contains("error 34", ex.Message);
    }

    [Fact]
    public void Dispose_leaves_the_calibrator_in_standby_at_0_V()
    {
        var session = Calibrator();
        Open(session).Dispose();
        Assert.Equal("STBY,SOUT 0", session.Sent[0]);
    }
}
