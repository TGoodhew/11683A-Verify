using System.Globalization;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Instruments.Visa;

namespace Verify11683A.Instruments.DcSource;

/// <summary>Which pair of the DC calibrator's front-panel terminals carries the output.</summary>
public enum DcTerminals
{
    Output,

    /// <summary>The 5440B's DIVIDER terminals: ±2.2 V at most, finer resolution and less noise, about 500 Ω.</summary>
    Divider,
}

/// <summary>
/// A DC calibrator the app sets over the bus, feeding the 11683A's REF EXT input (Part 4, and the E4418B's Procedure
/// 2). With none, the operator sets the calibrator by hand.
/// </summary>
public interface IDcSource : IDisposable
{
    /// <summary>What the log and report say the calibrator was, e.g. "Fluke 5440B, software 2.1".</summary>
    string Description { get; }

    /// <summary>Confirms the calibrator is the expected model and sets it up: Standby at 0 V, Voltage mode, internal sense and guard.</summary>
    void Identify();

    /// <summary>The terminals <see cref="Output"/> uses for <paramref name="volts"/>, so the operator can be told to move the leads first.</summary>
    DcTerminals TerminalsFor(double volts);

    /// <summary>Sets <paramref name="volts"/> and turns the output on (Operate), then waits for it to settle.</summary>
    void Output(double volts);

    /// <summary>Output off (Standby) and set to 0 V.</summary>
    void Standby();
}

public sealed class DcSourceException(string message) : InstrumentException(message);

/// <summary>
/// Fluke 5440B over its own device-dependent messages (5440B User Manual, Table 4-7, pp. 4-16 to 4-19), not SCPI.
/// Messages in one statement are separated by commas. There is no identity query: GVRS returns the software version.
/// </summary>
public sealed class Fluke5440B(IVisaSession session, bool useDivider = true, Action<TimeSpan>? sleep = null) : IDcSource
{
    /// <summary>The largest output the app will set: above ±22 V the 5440B trips to Standby (p. 4-10), and Part 4 needs 15.588 V at most.</summary>
    public const double MaximumVolts = 22;

    /// <summary>The DIVIDER terminals' span (p. 2-6).</summary>
    public const double DividerMaximumVolts = 2.2;

    /// <summary>Settling within 2 ppm of a change takes 5 s up to 22 V (p. 1-7). The Output Settled bit only means ±5%.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);

    private const int OutputSettledBit = 16;     // Serial Poll Status Byte, Figure 4-6
    private const int VoltageModeCode = 1;       // Setup Status, Table 4-5
    private const int InternalSenseCode = 16;
    private const int InternalGuardCode = 64;
    private const int RearOutputCode = 128;

    private readonly Action<TimeSpan> _sleep = sleep ?? Thread.Sleep;

    public string Description { get; private set; } = "Fluke 5440B";

    public void Identify()
    {
        var version = session.Query("GVRS").Trim();
        if (!double.TryParse(version, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            throw new DcSourceException($"{session.ResourceName}: expected a Fluke 5440B, but GVRS returned '{version}'.");
        // Front or rear terminals is a change inside the 5440B (User Manual 2-27, Figure 2-8), not a bus setting, and
        // the leads go on the front OUTPUT and DIVIDER terminals.
        if ((Status() & RearOutputCode) != 0)
            throw new DcSourceException($"{session.ResourceName}: the 5440B's output is on its rear terminals; the app uses the front "
                + "OUTPUT and DIVIDER terminals. Change it back to the front (5440B User Manual 2-27, Figure 2-8).");
        // Everything else is set here (Table 4-7), Standby first so nothing comes out until Output: Voltage mode (boost
        // off), internal sense (REF EXT is a two-wire connection) and internal guard (GUARD on OUTPUT LO, which floats
        // once the GROUND-GUARD shorting link is off, as the Service Guide's Figure 2-3 needs).
        Send("STBY,BSTO,DIVN,SOUT 0,ISNS,IGRD");
        var status = Status();
        const int wanted = VoltageModeCode | InternalSenseCode | InternalGuardCode;
        if ((status & wanted) != wanted)
            throw new DcSourceException($"{session.ResourceName}: the 5440B did not take Voltage mode, internal sense and internal guard "
                + $"(GSTS {status}, expected {wanted} set; Table 4-5).");
        Description = $"Fluke 5440B, software {version}";
    }

    public DcTerminals TerminalsFor(double volts) =>
        useDivider && Math.Abs(volts) <= DividerMaximumVolts ? DcTerminals.Divider : DcTerminals.Output;

    public void Output(double volts)
    {
        if (Math.Abs(volts) > MaximumVolts)
            throw new DcSourceException($"{volts.ToString("G6", CultureInfo.InvariantCulture)} V is above the ±{MaximumVolts:0} V the app will set on the 5440B.");
        var divider = TerminalsFor(volts) == DcTerminals.Divider;
        Send($"{(divider ? "DIVY" : "DIVN")},SOUT {Format(volts, divider)},OPER");
        WaitSettled();
    }

    public void Standby() => Send("STBY,SOUT 0");

    /// <summary>The setup status (GSTS): the sum of the codes in Table 4-5.</summary>
    public int Status() => (int)ScpiNumber.Parse(session.Query("GSTS"), "the 5440B setup status");

    /// <summary>
    /// The output setting rounded to the range's resolution (p. 1-7): 0.01 µV on the divider's 220 mV range, 0.1 µV on
    /// its 2.2 V range, 1 µV up to 22 V. A mantissa may not have more than eight significant digits (p. 4-12).
    /// </summary>
    internal static string Format(double volts, bool divider)
    {
        var decimals = divider ? (Math.Abs(volts) <= 0.22 ? 8 : 7) : 6;
        return Math.Round(volts, decimals).ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture);
    }

    private void Send(string statement)
    {
        session.Write(statement);
        var code = session.Query("GERR").Trim();
        if (!double.TryParse(code, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value != 0)
            throw new DcSourceException($"{session.ResourceName}: the 5440B reported error {code} after '{statement}' (5440B User Manual, Section 2).");
    }

    /// <summary>Polls the Output Settled bit for up to 15 s, then allows the 5 s the 2 ppm settling takes.</summary>
    private void WaitSettled()
    {
        for (var i = 0; i < 30; i++)
        {
            if (((int)ScpiNumber.Parse(session.Query("GSPB"), "the 5440B serial poll byte") & OutputSettledBit) != 0)
                break;
            _sleep(TimeSpan.FromMilliseconds(500));
        }
        _sleep(SettleTime);
    }

    /// <summary>Told when Standby fails on closing, so the operator can be warned the output may still be on.</summary>
    public Action<string>? StandbyFailed { get; set; }

    /// <summary>Standby, then close. A failed Standby is reported to <see cref="StandbyFailed"/>, not thrown over the error being unwound.</summary>
    public void Dispose()
    {
        try
        {
            Standby();
        }
        catch (InstrumentException ex)
        {
            StandbyFailed?.Invoke(ex.Message);
        }
        finally
        {
            session.Dispose();
        }
    }
}

public static class DcSources
{
    /// <summary>The models with a bus driver. "manual" means the operator sets the calibrator by hand.</summary>
    public static readonly IReadOnlyList<string> BusModels = ["5440B"];

    public static bool IsManual(string model) => model is "" || model.Equals("manual", StringComparison.OrdinalIgnoreCase);

    public static IDcSource Open(string model, IVisaSession session, bool useDivider) => model.ToUpperInvariant() switch
    {
        "5440B" => new Fluke5440B(session, useDivider),
        _ => throw new ArgumentException($"No bus driver for DC calibrator '{model}'. Use {string.Join(", ", BusModels)} or manual."),
    };
}
