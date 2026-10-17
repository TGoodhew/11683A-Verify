using Verify11683A.Instruments.Visa;

namespace Verify11683A.Instruments.Dvm;

/// <summary>
/// HP/Agilent 3458A in its native (non-SCPI) language. Commands are from the 3458A User's Guide command
/// reference, cross-checked against the GPIB-MCP command database. Readings are single-triggered:
/// TRIG HOLD after configuring, then TRIG SGL for each reading.
/// </summary>
/// <remarks>
/// The 90-day/1-year/2-year specifications require an ACAL within 24 hours and ±1 °C, so run
/// ACAL DCV and ACAL OHMS at the test temperature before verifying. The app does not run ACAL itself;
/// ACAL OHMS takes about ten minutes.
/// </remarks>
public sealed class Hp3458A(IVisaSession session, IClock? clock = null) : IDvm
{
    private static readonly double[] DcVoltageRanges = [0.1, 1, 10, 100, 1000];
    private static readonly double[] FourWireRanges = [10, 100, 1e3, 1e4, 1e5, 1e6, 1e7];

    private readonly IClock _clock = clock ?? SystemClock.Instance;

    public string Model => "3458A";

    public string ResourceName => session.ResourceName;

    public DvmConfiguration? Configuration { get; private set; }

    public DvmIdentity Identify()
    {
        // ID? returns "HP3458A"; REV? returns "<master>,<slave>". The 3458A cannot report its serial.
        var raw = session.Query("ID?").Trim();
        if (!raw.Contains("3458A", StringComparison.OrdinalIgnoreCase))
            throw new DvmException($"{ResourceName}: expected an HP 3458A, but ID? returned '{raw}'.");
        var revision = session.Query("REV?").Trim();
        return new DvmIdentity("HP", "3458A", "", revision, $"{raw}; REV {revision}");
    }

    public void Reset()
    {
        session.Clear();
        session.Write("PRESET NORM");
        // PRESET NORM leaves TRIG SYN, which reads whenever the meter is addressed to talk. Hold until Read().
        session.Write("TRIG HOLD");
        // EOI with each reading, so reads finish at the reading rather than at the timeout.
        session.Write("END ALWAYS");
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
            throw new ArgumentOutOfRangeException(nameof(range), $"3458A has no {function} range {range}.");

        Configuration = null;
        var r = ScpiNumber.Format(range);
        var name = function == DvmFunction.DcVoltage ? "DCV" : "OHMF";

        session.Write("TRIG HOLD");
        session.Write($"FUNC {name},{r}");
        session.Write($"NPLC {ScpiNumber.Format(nplc)}");
        session.Write("AZERO ON");
        if (function == DvmFunction.DcVoltage)
            session.Write("FIXEDZ OFF");   // >10 GΩ on the 100 mV, 1 V and 10 V ranges
        else
            session.Write("OCOMP ON");     // the ohms specifications assume OCOMP ON
        session.Write("NDIG 8");

        ThrowIfErrors($"configure {name} {r}");

        var highZ = function == DvmFunction.DcVoltage && range <= 10;
        var inputResistance = function switch
        {
            DvmFunction.DcVoltage when highZ => ">10 GΩ",
            DvmFunction.DcVoltage => "10 MΩ ±1%",
            _ => "n/a (4-wire ohms, OCOMP ON)",
        };
        return Configuration = new DvmConfiguration(function, range, nplc, AutoZero: true, highZ, inputResistance);
    }

    public DvmReading Read()
    {
        if (Configuration is null)
            throw new InvalidOperationException("Configure the 3458A before reading it.");
        session.Write("TRIG SGL");
        var value = ScpiNumber.Parse(session.ReadLine(), "a reading");
        return new DvmReading(value, ScpiNumber.IsOverload(value), _clock.Now);
    }

    public IReadOnlyList<string> ReadErrors()
    {
        var errors = new List<string>();
        // ERRSTR? reads and clears one error at a time; "0,\"NO ERROR\"" when the registers are clear.
        for (var i = 0; i < 32; i++)
        {
            var reply = session.Query("ERRSTR?").Trim();
            if (reply.StartsWith("0,", StringComparison.Ordinal) || reply == "0")
                break;
            errors.Add(reply);
        }
        return errors;
    }

    public IReadOnlyList<KeyValuePair<string, string>> ReadBackSettings()
    {
        string[] queries = ["FUNC?", "NPLC?", "AZERO?", "FIXEDZ?", "OCOMP?", "NDIG?", "TRIG?", "END?"];
        return queries.Select(q => new KeyValuePair<string, string>(q, session.Query(q).Trim())).ToList();
    }

    public void Dispose() => session.Dispose();

    private void ThrowIfErrors(string during)
    {
        var errors = ReadErrors();
        if (errors.Count > 0)
            throw new DvmException($"{ResourceName}: error during {during}: {string.Join("; ", errors)}");
    }
}
