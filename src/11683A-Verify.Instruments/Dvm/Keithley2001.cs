using Verify11683A.Instruments.Visa;

namespace Verify11683A.Instruments.Dvm;

/// <summary>
/// Keithley 2001 (7½ digits) over SCPI. Commands are from the Model 2001 Operator's Manual (2001-900-01 Rev. K),
/// Section 4; page numbers below are the manual's. The gpib tools have no 2001 entry, so every command still has to
/// be tried on the real instrument.
/// </summary>
/// <remarks>
/// <para>
/// Its NPLC stops at 10 (p. 4-119). A longer integration, such as the app's default 100, is 10 PLC readings averaged by
/// the repeating digital filter (pp. 4-137 to 4-142), and <see cref="DvmConfiguration.Nplc"/> reports the total, so 100
/// is ten 10 PLC readings. The advanced filter, on after *RST (p. 4-141), is turned off: it would let a step through
/// without averaging.
/// </para>
/// <para>
/// A range is set by the reading it must hold, and the 2001 picks the most sensitive range that holds it (p. 4-125),
/// so <see cref="Configure"/> sends the range's full scale and reads it back. Its ranges go 5% past full scale (2.1 V
/// on 2 V), not the 34401A's 20%. The input is >10 GΩ up to 20 V with nothing to select (SPEC-2001 Rev. H, p. 2).
/// </para>
/// </remarks>
public sealed class Keithley2001(IVisaSession session, IClock? clock = null) : IDvm
{
    /// <summary>The highest range with the >10 GΩ input; 200 V and 1000 V are 10 MΩ ±1%.</summary>
    public const double HighImpedanceMaxRange = 20.0;

    /// <summary>The longest single integration (p. 4-119).</summary>
    public const double MaximumNplc = 10;

    /// <summary>The largest digital filter count (p. 4-138).</summary>
    public const int MaximumFilterCount = 100;

    /// <summary>Offset compensation is specified on the 20 Ω to 20 kΩ ranges (SPEC-2001 Rev. H, p. 7, note 32).</summary>
    public const double OffsetCompensationMaxRange = 2e4;

    private static readonly double[] DcVoltageRanges = [0.2, 2, 20, 200, 1000];

    // Four-wire ohms stop at 200 kΩ (p. 4-124: up to 2.1e5).
    private static readonly double[] FourWireRanges = [20, 200, 2e3, 2e4, 2e5];

    private readonly IClock _clock = clock ?? SystemClock.Instance;

    public string Model => "2001";

    public string ResourceName => session.ResourceName;

    public DvmConfiguration? Configuration { get; private set; }

    /// <summary>Every range reads to 2.1 of 2 (1000 V to 1100 V).</summary>
    public double OverrangeFactor => 1.05;

    public DvmIdentity Identify()
    {
        // "KEITHLEY INSTRUMENTS INC.,MODEL 2001,xxxxxxx,yyyyy/zzzzz" (p. 4-42): serial, then digital/display ROM revisions.
        var raw = session.Query("*IDN?").Trim();
        var fields = raw.Split(',');
        if (fields.Length < 4 || !fields[1].Contains("2001", StringComparison.Ordinal))
            throw new DvmException($"{ResourceName}: expected a Keithley 2001, but *IDN? returned '{raw}'.");
        var model = fields[1].Trim();
        if (model.StartsWith("MODEL ", StringComparison.OrdinalIgnoreCase))
            model = model["MODEL ".Length..].Trim();
        return new DvmIdentity(fields[0].Trim(), model, fields[2].Trim(), fields[3].Trim(), raw);
    }

    /// <summary>
    /// Device clear, *RST, then one-shot readings in ASCII with the reading alone: at power-on every element is sent,
    /// and *RST only goes back to the reading alone in one fetch format (p. 4-86).
    /// </summary>
    public void Reset()
    {
        session.Clear();
        session.Write("*RST");
        session.Write("*CLS");
        session.Write(":INIT:CONT OFF");
        session.Write(":ABOR");
        session.Write(":FORM:DATA ASC");
        session.Write(":FORM:ELEM READ");
        Configuration = null;
        ThrowIfErrors("reset");
    }

    public IReadOnlyList<double> Ranges(DvmFunction function) => function switch
    {
        DvmFunction.DcVoltage => DcVoltageRanges,
        DvmFunction.FourWireResistance => FourWireRanges,
        _ => throw new ArgumentOutOfRangeException(nameof(function)),
    };

    /// <summary>
    /// The 2001's NPLC and filter count for <paramref name="nplc"/>: up to 10 as asked, beyond that 10 PLC readings
    /// averaged, as many as make up the total (at most 100).
    /// </summary>
    public static (double Nplc, int FilterCount) Integration(double nplc) =>
        nplc <= MaximumNplc ? (nplc, 1) : (MaximumNplc, (int)Math.Clamp(Math.Round(nplc / MaximumNplc), 1, MaximumFilterCount));

    public DvmConfiguration Configure(DvmFunction function, double range, double nplc)
    {
        if (!Ranges(function).Any(r => ScpiNumber.SameRange(r, range)))
            throw new ArgumentOutOfRangeException(nameof(range), $"2001 has no {function} range {range}.");

        Configuration = null;
        var name = function == DvmFunction.DcVoltage ? "VOLT:DC" : "FRES";
        var r = ScpiNumber.Format(range);
        var (integration, filterCount) = Integration(nplc);
        var n = ScpiNumber.Format(integration);
        // 7½ digits; below 1 PLC the reading cannot carry them.
        var digits = integration >= 1 ? 8 : 7;
        var offsetCompensation = function == DvmFunction.FourWireResistance && range <= OffsetCompensationMaxRange;

        // CONF: the function with its *RST settings, one-shot triggering, idle (pp. 4-54, 4-55). Autozero is set
        // after it, because CONF puts autozero back to its *RST value, and it can only change while idle (p. 4-194).
        session.Write($":CONF:{name}");
        session.Write($":SENS:{name}:RANG {r}");
        session.Write($":SENS:{name}:NPLC {n}");
        session.Write($":SENS:{name}:DIG {digits}");
        session.Write($":SENS:{name}:AVER:ADV OFF");
        if (filterCount > 1)
        {
            session.Write($":SENS:{name}:AVER:TCON REP");
            session.Write($":SENS:{name}:AVER:COUN {filterCount}");
            session.Write($":SENS:{name}:AVER ON");
        }
        else
        {
            session.Write($":SENS:{name}:AVER OFF");
        }
        if (function == DvmFunction.FourWireResistance)
            session.Write($":SENS:FRES:OCOM {(offsetCompensation ? "ON" : "OFF")}");
        // Synchronous: a zero with every reading, not every 200 ms (p. 4-193).
        session.Write(":SYST:AZER:TYPE SYNC");
        session.Write(":SYST:AZER:STAT ON");

        // Read back what the meter actually did.
        var conf = session.Query(":CONF?").Trim().Trim('"');
        if (!conf.Equals(name, StringComparison.OrdinalIgnoreCase))
            throw new DvmException($"{ResourceName}: asked for {name}, but CONF? reports {conf}.");

        var rangeRead = ScpiNumber.Parse(session.Query($":SENS:{name}:RANG?"), "the range");
        if (!ScpiNumber.SameRange(rangeRead, range))
            throw new DvmException($"{ResourceName}: asked for the {r} range, but the meter reports {ScpiNumber.Format(rangeRead)}.");

        var nplcRead = ScpiNumber.Parse(session.Query($":SENS:{name}:NPLC?"), "NPLC");
        if (!ScpiNumber.SameRange(nplcRead, integration))
            throw new DvmException($"{ResourceName}: asked for NPLC {n}, but the meter reports {ScpiNumber.Format(nplcRead)}.");

        var digitsRead = ScpiNumber.Parse(session.Query($":SENS:{name}:DIG?"), "the resolution");
        if ((int)Math.Round(digitsRead) != digits)
            throw new DvmException($"{ResourceName}: asked for DIG {digits}, but the meter reports {ScpiNumber.Format(digitsRead)}.");

        if (Flag(session.Query($":SENS:{name}:AVER?")) != filterCount > 1)
            throw new DvmException($"{ResourceName}: the digital filter did not turn {(filterCount > 1 ? "on" : "off")}.");
        if (filterCount > 1)
        {
            var countRead = ScpiNumber.Parse(session.Query($":SENS:{name}:AVER:COUN?"), "the filter count");
            if ((int)Math.Round(countRead) != filterCount)
                throw new DvmException($"{ResourceName}: asked for a filter count of {filterCount}, but the meter reports {ScpiNumber.Format(countRead)}.");
        }
        if (Flag(session.Query($":SENS:{name}:AVER:ADV?")))
            throw new DvmException($"{ResourceName}: the advanced filter did not turn off.");

        if (function == DvmFunction.FourWireResistance && Flag(session.Query(":SENS:FRES:OCOM?")) != offsetCompensation)
            throw new DvmException($"{ResourceName}: offset compensation did not turn {(offsetCompensation ? "on" : "off")}.");

        if (!Flag(session.Query(":SYST:AZER:STAT?")))
            throw new DvmException($"{ResourceName}: autozero did not turn on.");

        ThrowIfErrors($"configure {name} {r}");

        var highZ = function == DvmFunction.DcVoltage && range <= HighImpedanceMaxRange;
        var inputResistance = function switch
        {
            DvmFunction.DcVoltage when highZ => ">10 GΩ",
            DvmFunction.DcVoltage => "10 MΩ ±1%",
            _ when offsetCompensation => "n/a (4-wire ohms, offset compensation on)",
            _ => "n/a (4-wire ohms)",
        };
        return Configuration = new DvmConfiguration(function, range, integration * filterCount, AutoZero: true, highZ, inputResistance);
    }

    public DvmReading Read()
    {
        if (Configuration is null)
            throw new InvalidOperationException("Configure the 2001 before reading it.");
        // ABORt, INITiate, FETCh? (p. 4-55). An overflow reads +9.9E37 (p. 4-85).
        var value = ParseReading(session.Query(":READ?"));
        return new DvmReading(value, ScpiNumber.IsOverload(value), _clock.Now);
    }

    public IReadOnlyList<string> ReadErrors()
    {
        var errors = new List<string>();
        // The queue holds 10 messages (p. 4-193); the bound stops a misbehaving bus from looping forever.
        for (var i = 0; i < 12; i++)
        {
            var reply = session.Query(":SYST:ERR?").Trim();
            if (reply.StartsWith("0,", StringComparison.Ordinal) || reply.StartsWith("+0,", StringComparison.Ordinal))
                break;
            errors.Add(reply);
        }
        return errors;
    }

    public IReadOnlyList<KeyValuePair<string, string>> ReadBackSettings()
    {
        var name = Configuration?.Function == DvmFunction.FourWireResistance ? "FRES" : "VOLT:DC";
        List<string> queries =
        [
            ":CONF?", $":SENS:{name}:RANG?", $":SENS:{name}:NPLC?", $":SENS:{name}:DIG?", $":SENS:{name}:AVER?",
            $":SENS:{name}:AVER:COUN?", $":SENS:{name}:AVER:ADV?", ":SYST:AZER:STAT?", ":SYST:AZER:TYPE?", ":SYST:FRSW?",
        ];
        if (name == "FRES")
            queries.Add(":SENS:FRES:OCOM?");
        return queries.Select(q => new KeyValuePair<string, string>(q, session.Query(q).Trim())).ToList();
    }

    /// <summary>:SYST:FRSW? returns 1 for the front inputs, 0 for the rear (p. 4-192).</summary>
    public DvmTerminals ReadTerminals() => ParseTerminals(session.Query(":SYST:FRSW?"));

    public void Dispose() => session.Dispose();

    internal static DvmTerminals ParseTerminals(string reply) => reply.Trim() switch
    {
        "1" => DvmTerminals.Front,
        "0" => DvmTerminals.Rear,
        _ => DvmTerminals.Unknown,
    };

    /// <summary>
    /// The reading element, e.g. "+1.45012340E-01". With the units or status elements on, the number is followed by
    /// letters ("NVDC", p. 4-83); they are dropped, and an "O" (overflow) status counts as an overload.
    /// </summary>
    internal static double ParseReading(string reply)
    {
        var text = reply.Trim().Split(',')[0].Trim();
        var end = text.Length;
        while (end > 0 && char.IsLetter(text[end - 1]))
            end--;
        var suffix = text[end..];
        var value = ScpiNumber.Parse(text[..end], "a reading");
        return suffix.StartsWith('O') ? Math.CopySign(9.9e37, value) : value;
    }

    private static bool Flag(string reply) => reply.Trim() switch
    {
        "1" or "ON" => true,
        "0" or "OFF" => false,
        var other => throw new DvmException($"Could not read an on/off reply from '{other}'."),
    };

    private void ThrowIfErrors(string during)
    {
        var errors = ReadErrors();
        if (errors.Count > 0)
            throw new DvmException($"{ResourceName}: error during {during}: {string.Join("; ", errors)}");
    }
}
