using Verify11683A.Instruments.Visa;

namespace Verify11683A.Instruments.Dvm;

/// <summary>
/// HP/Agilent 34401A over SCPI. Commands are from the 34401A User's Guide, Chapter 4/5 (cross-checked
/// against the GPIB-MCP command database).
/// </summary>
/// <remarks>
/// CONFigure and MEASure? turn INPut:IMPedance:AUTO OFF (User's Guide p. 53, PDF p. 61), so
/// <see cref="Configure"/> sends INP:IMP:AUTO ON after every CONF and reads it back. Setting it once at
/// start-up would leave the 11683A loaded with 10 MΩ on the 0.1, 1 and 10 V ranges after the first range change.
/// </remarks>
public sealed class Hp34401A(IVisaSession session, IClock? clock = null) : IDvm
{
    /// <summary>The highest range on which the >10 GΩ input is available.</summary>
    public const double HighImpedanceMaxRange = 10.0;

    private static readonly double[] DcVoltageRanges = [0.1, 1, 10, 100, 1000];
    private static readonly double[] FourWireRanges = [100, 1e3, 1e4, 1e5, 1e6, 1e7];

    private readonly IClock _clock = clock ?? SystemClock.Instance;

    public string Model => "34401A";

    public string ResourceName => session.ResourceName;

    public DvmConfiguration? Configuration { get; private set; }

    public DvmIdentity Identify()
    {
        // e.g. "HEWLETT-PACKARD,34401A,0,11-5-2". The 34401A always reports serial 0.
        var raw = session.Query("*IDN?").Trim();
        var fields = raw.Split(',');
        if (fields.Length < 4 || !fields[1].Contains("34401A", StringComparison.OrdinalIgnoreCase))
            throw new DvmException($"{ResourceName}: expected an HP 34401A, but *IDN? returned '{raw}'.");
        return new DvmIdentity(fields[0].Trim(), fields[1].Trim(), fields[2].Trim(), fields[3].Trim(), raw);
    }

    public void Reset()
    {
        session.Clear();
        session.Write("*RST");
        session.Write("*CLS");
        Configuration = null;
        ThrowIfErrors("reset");
    }

    public IReadOnlyList<double> Ranges(DvmFunction function) => function switch
    {
        DvmFunction.DcVoltage => DcVoltageRanges,
        DvmFunction.FourWireResistance => FourWireRanges,
        _ => throw new ArgumentOutOfRangeException(nameof(function)),
    };

    public DvmConfiguration Configure(DvmFunction function, double range, double nplc)
    {
        if (!Ranges(function).Any(r => ScpiNumber.SameRange(r, range)))
            throw new ArgumentOutOfRangeException(nameof(range), $"34401A has no {function} range {range}.");

        Configuration = null;
        var r = ScpiNumber.Format(range);
        var n = ScpiNumber.Format(nplc);
        var highZ = false;

        switch (function)
        {
            case DvmFunction.DcVoltage:
                session.Write($"CONF:VOLT:DC {r}");
                session.Write($"VOLT:DC:NPLC {n}");
                session.Write("ZERO:AUTO ON");
                highZ = range <= HighImpedanceMaxRange;
                if (highZ)
                    session.Write("INP:IMP:AUTO ON");
                break;

            case DvmFunction.FourWireResistance:
                session.Write($"CONF:FRES {r}");
                session.Write($"FRES:NPLC {n}");
                session.Write("ZERO:AUTO ON");
                break;
        }

        // Read back what the meter actually did.
        var (confFunction, confRange) = ParseConf(session.Query("CONF?"));
        var expectedFunction = function == DvmFunction.DcVoltage ? "VOLT" : "FRES";
        if (confFunction != expectedFunction || !ScpiNumber.SameRange(confRange, range))
            throw new DvmException($"{ResourceName}: asked for {expectedFunction} {r}, but CONF? reports {confFunction} {ScpiNumber.Format(confRange)}.");

        var nplcQuery = function == DvmFunction.DcVoltage ? "VOLT:DC:NPLC?" : "FRES:NPLC?";
        var nplcRead = ScpiNumber.Parse(session.Query(nplcQuery), "NPLC");
        if (!ScpiNumber.SameRange(nplcRead, nplc))
            throw new DvmException($"{ResourceName}: asked for NPLC {n}, but the meter reports {ScpiNumber.Format(nplcRead)}.");

        if (session.Query("ZERO:AUTO?").Trim() != "1")
            throw new DvmException($"{ResourceName}: autozero did not turn on.");

        if (highZ && session.Query("INP:IMP:AUTO?").Trim() != "1")
            throw new DvmException($"{ResourceName}: INP:IMP:AUTO did not turn on; the 11683A would see 10 MΩ instead of >10 GΩ.");

        ThrowIfErrors($"configure {expectedFunction} {r}");

        var inputResistance = function switch
        {
            DvmFunction.DcVoltage when highZ => ">10 GΩ",
            DvmFunction.DcVoltage => "10 MΩ ±1%",
            _ => "n/a (4-wire ohms)",
        };
        return Configuration = new DvmConfiguration(function, range, nplc, AutoZero: true, highZ, inputResistance);
    }

    public DvmReading Read()
    {
        if (Configuration is null)
            throw new InvalidOperationException("Configure the 34401A before reading it.");
        var value = ScpiNumber.Parse(session.Query("READ?"), "a reading");
        return new DvmReading(value, ScpiNumber.IsOverload(value), _clock.Now);
    }

    public IReadOnlyList<string> ReadErrors()
    {
        var errors = new List<string>();
        // The queue holds at most 20 errors; the bound stops a misbehaving bus from looping forever.
        for (var i = 0; i < 25; i++)
        {
            var reply = session.Query("SYST:ERR?").Trim();
            if (reply.StartsWith("+0,", StringComparison.Ordinal) || reply.StartsWith("0,", StringComparison.Ordinal))
                break;
            errors.Add(reply);
        }
        return errors;
    }

    public IReadOnlyList<KeyValuePair<string, string>> ReadBackSettings()
    {
        var settings = new List<KeyValuePair<string, string>>
        {
            new("CONF?", session.Query("CONF?").Trim()),
        };
        if (Configuration?.Function == DvmFunction.FourWireResistance)
            settings.Add(new("FRES:NPLC?", session.Query("FRES:NPLC?").Trim()));
        else
            settings.Add(new("VOLT:DC:NPLC?", session.Query("VOLT:DC:NPLC?").Trim()));
        settings.Add(new("ZERO:AUTO?", session.Query("ZERO:AUTO?").Trim()));
        settings.Add(new("INP:IMP:AUTO?", session.Query("INP:IMP:AUTO?").Trim()));
        settings.Add(new("ROUT:TERM?", session.Query("ROUT:TERM?").Trim()));
        return settings;
    }

    public void Dispose() => session.Dispose();

    /// <summary>Parses a CONF? reply such as "VOLT +1.000000E+00,+1.000000E-06".</summary>
    internal static (string Function, double Range) ParseConf(string reply)
    {
        var text = reply.Trim().Trim('"');
        var space = text.IndexOf(' ');
        if (space < 0)
            throw new DvmException($"Could not read CONF? reply '{reply}'.");
        var function = text[..space].Trim();
        var range = ScpiNumber.Parse(text[(space + 1)..].Split(',')[0], "the CONF? range");
        return (function, range);
    }

    private void ThrowIfErrors(string during)
    {
        var errors = ReadErrors();
        if (errors.Count > 0)
            throw new DvmException($"{ResourceName}: error during {during}: {string.Join("; ", errors)}");
    }
}
