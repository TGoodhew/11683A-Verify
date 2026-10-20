using System.Globalization;
using System.Text.RegularExpressions;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Instruments.Visa;
using Verify11683A.Meters;

namespace Verify11683A.Instruments.PowerMeter;

/// <summary>
/// A power meter read during Part 4. Zero and CAL are done by the operator from the meter's front panel, as the
/// E4418B Service Guide's procedure does; the app only reads the result.
/// </summary>
public interface IPowerMeter : IDisposable
{
    /// <summary>What the report says the meter was, e.g. "Agilent E4418B s/n GB12345678, fw A1.06".</summary>
    string Description { get; }

    /// <summary>False when the operator types the readings in.</summary>
    bool OverBus { get; }

    /// <summary>Confirms the meter is the expected model and fills in <see cref="Description"/>.</summary>
    void Identify();

    /// <summary>The present reading, in watts.</summary>
    double ReadWatts();
}

/// <summary>
/// A power meter whose front-panel steps the app makes over the bus (meter-test), so the operator only sets the
/// 11683A: Preset, the display, range, filter and cal factor, the zero and the CAL.
/// </summary>
public interface IPowerMeterControl
{
    /// <summary>The manuals' first step, Preset; the meter then reads in watts.</summary>
    void Preset();

    /// <summary>
    /// Sets what <paramref name="settings"/> names, and the cal factor (null: 100%). With
    /// <see cref="MeterSettings.StoreReference"/> the present input becomes the dB REL reference.
    /// </summary>
    /// <exception cref="NotSupportedException">A setting the meter has no bus code for.</exception>
    void Apply(MeterSettings settings, double? calFactorPercent);

    /// <summary>Zeroes on whatever is at the sensor input (the 11683A in STANDBY), and returns when it is done.</summary>
    void Zero();

    /// <summary>False when CAL is a front-panel adjustment (436A CAL ADJ).</summary>
    bool CanCalibrate { get; }

    /// <summary>CAL against the 1 mW at the sensor input (the 11683A in CALIBRATE), not the meter's own POWER REF.</summary>
    void Calibrate(double referenceCalFactorPercent);

    /// <summary>False when the CAL FACTOR is a front-panel switch (436A): the operator sets it.</summary>
    bool CanSetCalFactor { get; }

    /// <summary>One settled reading in the display's units: watts, dBm or dB.</summary>
    double Read();
}

public sealed class PowerMeterException(string message) : InstrumentException(message);

/// <summary>
/// HP/Agilent E4418B/E4419B (and E4416A/E4417A) over SCPI. Reads FETCh? in whatever units the meter displays
/// (UNIT:POWer? says which), so the front-panel set-up is left as the operator made it. On the two-channel E4417A
/// and E4419B, FETC&lt;n&gt;? reads window n (1 upper, 2 lower), whose channel is whatever the window shows, so the
/// query names the channel with a source list: FETC2? DEF,DEF,(@2) is channel B (Programming Guide pp. 2-22 to 2-23).
/// </summary>
/// <remarks>
/// Under control (Programming Guide, E4418-90066): SYST:PRES then single triggered readings with the trigger delay
/// on, so READ? returns only once the filter is full (pp. 93–95, 176–177). CAL, SENS, INIT and TRIG take the channel
/// and UNIT, DISP:WIND and READ the window; after Preset window n shows channel n (p. 414). Zero is
/// CAL:ZERO:AUTO ONCE and CAL is CAL:AUTO ONCE, which calibrates on the sensor input taken as 1 mW × RCF (pp. 39–40,
/// 237); CAL:ALL is not used, because it would zero with the 11683A's 1 mW applied.
/// </remarks>
public sealed class E441xPowerMeter(IVisaSession session, int channel = 1) : IPowerMeter, IPowerMeterControl
{
    /// <summary>The meter fills its filter at about 20 readings a second (Programming Guide pp. 93–95).</summary>
    private const double ReadingsPerSecond = 20;

    private bool _twoChannel;
    private bool _controlled;
    private int _filter = 4;

    public string Description { get; private set; } = "E441x power meter";
    public bool OverBus => true;
    public bool CanCalibrate => true;
    public bool CanSetCalFactor => true;

    public void Identify()
    {
        // e.g. "Agilent Technologies,E4418B,GB12345678,A1.06.00"
        var raw = session.Query("*IDN?").Trim();
        var fields = raw.Split(',');
        if (fields.Length < 4 || !Regex.IsMatch(fields[1], "E441[6-9]"))
            throw new PowerMeterException($"{session.ResourceName}: expected an E4416A–E4419B, but *IDN? returned '{raw}'.");
        _twoChannel = Regex.IsMatch(fields[1], "E441[79]");
        if (channel != 1 && !_twoChannel)
            throw new PowerMeterException($"{session.ResourceName}: the {fields[1].Trim()} has one channel, so there is no channel {channel}.");
        Description = $"{fields[0].Trim()} {fields[1].Trim()} s/n {fields[2].Trim()}, fw {fields[3].Trim()}, channel {channel}";
    }

    public double ReadWatts()
    {
        var unit = session.Query($"UNIT{channel}:POW?").Trim().Trim('"').ToUpperInvariant();
        var value = ScpiNumber.Parse(session.Query(_twoChannel ? $"FETC{channel}? DEF,DEF,(@{channel})" : $"FETC{channel}?"), "a power reading");
        CheckError();
        return unit.StartsWith("DBM", StringComparison.Ordinal) ? DbmToWatts(value) : value;
    }

    public void Preset()
    {
        _controlled = true;
        session.Write("SYST:PRES");
        session.Write("*CLS");
        _filter = 4;
        // Single triggered readings: READ? starts one and returns it once the filter is full.
        session.Write($"INIT{channel}:CONT OFF;:TRIG{channel}:SOUR IMM;:TRIG{channel}:DEL:AUTO ON");
        // Preset leaves a sensor table selected, and with one the cal factor cannot be set (pp. 330–332).
        session.Write($"SENS{channel}:CORR:CSET1:STAT OFF");
        session.Write($"UNIT{channel}:POW W");
        CheckError();
    }

    public void Apply(MeterSettings settings, double? calFactorPercent)
    {
        if (settings.Display is { } display)
            session.Write($"UNIT{channel}:POW " + display switch
            {
                MeterDisplay.Watts => "W",
                MeterDisplay.Dbm => "DBM",
                _ => throw new NotSupportedException("The app does not use the E441x's relative mode."),
            });
        if (settings.Range is > 0)
            throw new NotSupportedException("The E441x has no fixed ranges for the app to set.");
        if (settings.StoreReference)
            throw new NotSupportedException("The app does not use the E441x's relative mode.");
        if (settings.Filter is { } filter)
        {
            session.Write(filter == 0
                ? $"SENS{channel}:AVER:STAT ON;:SENS{channel}:AVER:COUN:AUTO ON"
                : $"SENS{channel}:AVER:STAT ON;:SENS{channel}:AVER:COUN:AUTO OFF;:SENS{channel}:AVER:COUN {filter}");
            _filter = filter == 0 ? 4 : filter;
        }
        if (settings.Resolution is { } resolution)
            session.Write($"DISP:WIND{channel}:RES {resolution}");
        session.Write($"SENS{channel}:CORR:CFAC {Number(calFactorPercent ?? 100)}");
        CheckError();
    }

    public void Zero() => Overlapped($"CAL{channel}:ZERO:AUTO ONCE", "zero");

    public void Calibrate(double referenceCalFactorPercent)
    {
        session.Write($"CAL{channel}:RCF {Number(referenceCalFactorPercent)}");
        Overlapped($"CAL{channel}:AUTO ONCE", "CAL");
    }

    public double Read()
    {
        var timeout = session.TimeoutMilliseconds;
        // Twice the filter's fill time, and never under the configured timeout.
        session.TimeoutMilliseconds = Math.Max(timeout, (int)(2000 * _filter / ReadingsPerSecond) + 10000);
        try
        {
            var value = ScpiNumber.Parse(session.Query(_twoChannel ? $"READ{channel}? DEF,DEF,(@{channel})" : $"READ{channel}?"), "a power reading");
            CheckError();
            return value;
        }
        finally
        {
            session.TimeoutMilliseconds = timeout;
        }
    }

    /// <summary>An overlapped command (about 10 s), waited for with *OPC?, then the error queue (-231 for a zero or CAL error).</summary>
    private void Overlapped(string command, string what)
    {
        var timeout = session.TimeoutMilliseconds;
        session.TimeoutMilliseconds = Math.Max(timeout, 60000);
        try
        {
            session.Write(command);
            session.Query("*OPC?");
        }
        finally
        {
            session.TimeoutMilliseconds = timeout;
        }
        var error = session.Query("SYST:ERR?").Trim();
        if (!IsNoError(error))
            throw new PowerMeterException($"{session.ResourceName}: the {what} failed: {error}");
    }

    private void CheckError()
    {
        var error = session.Query("SYST:ERR?").Trim();
        if (!IsNoError(error))
            throw new PowerMeterException($"{session.ResourceName}: {error}");
    }

    private static bool IsNoError(string error) => error.StartsWith("+0", StringComparison.Ordinal) || error.StartsWith("0", StringComparison.Ordinal);

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public void Dispose() => PowerMeterSession.Close(session, _controlled ? $"INIT{channel}:CONT ON" : null);

    internal static double DbmToWatts(double dbm) => Math.Pow(10, dbm / 10) * 1e-3;
}

/// <summary>
/// The 437B's and 438A's zero and CAL: started with CS (clear status) in front, then watched by serial poll until
/// the status byte's Cal/Zero Complete bit (2) is set, or Measurement Error (8). Nothing else may be sent meanwhile:
/// any command aborts a CAL (437B Operating Manual p. 3-49, 438A manual p. 3-49).
/// </summary>
internal static class CalZeroWait
{
    public const int CalZeroComplete = 2;
    public const int MeasurementError = 8;

    public static void Run(IVisaSession session, string statement, string what, Action<TimeSpan> sleep, TimeSpan limit)
    {
        session.Write(statement);
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(500);
        while (waited < limit)
        {
            sleep(step);
            waited += step;
            var status = session.ReadStatusByte();
            if ((status & MeasurementError) != 0)
                throw new PowerMeterException($"{session.ResourceName}: the {what} failed (Measurement Error in the status byte); see the meter's display.");
            if ((status & CalZeroComplete) != 0)
                return;
        }
        throw new PowerMeterException($"{session.ResourceName}: the {what} did not finish within {limit.TotalSeconds:0} s.");
    }
}

/// <summary>
/// HP 437B over its HP-IB codes. LN selects watts (the E4418B procedure reads in watts too), TR2 takes one settled
/// reading, and TR3 returns the meter to free run.
/// </summary>
/// <remarks>
/// Under control (437B Operating Manual 00437-90047): PR presets (units dBm, so LN follows; cal factor from the sensor
/// table, so KB is always sent; p. 3-73). CSZE zeroes all five ranges in 5–20 s (pp. 3-106 to 3-108); CSCL100EN
/// calibrates on the sensor input with REF CF 100% (pp. 3-46 to 3-50). RMnEN holds range n and RA autoranges
/// (p. 3-94); FA is the auto filter and FMnEN a manual one (p. 3-61). An error reading is 9.00XXE+40, XX the code
/// (p. 3-37).
/// </remarks>
public sealed class Hp437BPowerMeter(IVisaSession session, Action<TimeSpan>? sleep = null) : IPowerMeter, IPowerMeterControl
{
    private readonly Action<TimeSpan> _sleep = sleep ?? Thread.Sleep;
    private bool _controlled;

    public string Description { get; private set; } = "HP 437B";
    public bool OverBus => true;
    public bool CanCalibrate => true;
    public bool CanSetCalFactor => true;

    public void Identify()
    {
        // "HEWLETT-PACKARD,437B,,X.X"
        var raw = session.Query("*IDN?").Trim();
        if (!raw.Contains("437B", StringComparison.OrdinalIgnoreCase))
            throw new PowerMeterException($"{session.ResourceName}: expected an HP 437B, but *IDN? returned '{raw}'.");
        var fields = raw.Split(',');
        Description = "HP 437B" + (fields.Length > 3 && fields[3].Trim() != "" ? $", fw {fields[3].Trim()}" : "");
    }

    public double ReadWatts()
    {
        session.Write("LN");
        var value = ScpiNumber.Parse(session.Query("TR2"), "a power reading");
        session.Write("TR3");
        return value;
    }

    public void Preset()
    {
        _controlled = true;
        session.Write("PRLN");
    }

    public void Apply(MeterSettings settings, double? calFactorPercent)
    {
        if (settings.Display is MeterDisplay.DbRelative || settings.StoreReference)
            throw new NotSupportedException("The app does not use the 437B's relative mode.");
        if (settings.Resolution is not null)
            throw new NotSupportedException("The app does not set the 437B's resolution.");
        var codes = settings.Display switch { MeterDisplay.Watts => "LN", MeterDisplay.Dbm => "LG", _ => "" };
        if (settings.Range is { } range)
            codes += range == 0 ? "RA" : $"RM{range}EN";
        if (settings.Filter is { } filter)
            codes += filter == 0 ? "FA" : $"FM{filter}EN";
        session.Write(codes + $"KB{Percent(calFactorPercent ?? 100)}EN");
    }

    public void Zero() => CalZeroWait.Run(session, "CSZE", "zero", _sleep, TimeSpan.FromSeconds(30));

    public void Calibrate(double referenceCalFactorPercent) =>
        CalZeroWait.Run(session, $"CSCL{Percent(referenceCalFactorPercent)}EN", "CAL", _sleep, TimeSpan.FromSeconds(30));

    public double Read()
    {
        var value = HpReading(session.Query("TR2"), "437B");
        session.Write("TR3");
        return value;
    }

    internal static string Percent(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>A TR2 reply; 9.00XXE+40 is error XX (437B p. 3-37: 17 is UP RANGE, 15 PLEASE ZERO).</summary>
    internal static double HpReading(string reply, string model)
    {
        var value = ScpiNumber.Parse(reply, "a power reading");
        if (value >= 9e40)
        {
            var code = (int)Math.Round((value / 1e40 - 9) * 1e4);
            throw new PowerMeterException($"{model} reading error" + (code > 0 ? $" {code:00}" : "") + (code is 17 or 18 ? " (over range)" : "") + $" ('{reply.Trim()}').");
        }
        return value;
    }

    public void Dispose() => PowerMeterSession.Close(session, _controlled ? "TR3" : null);
}

/// <summary>HP 438A over its HP-IB codes: AP or BP (sensor), LN (watts), TR2 for one settled reading, TR3 back to free run.</summary>
/// <remarks>
/// Under control (438A manual 00438-90028): PR presets both sensors (watts, cal factor 100%, auto range and filter,
/// sensor A; p. 3-69), so AP or BP follows it, selecting the sensor for the display and for ZE, CL, KB and RM
/// (p. 3-76). CSZE zeroes (pp. 3-86 to 3-87); CSCL-100EN calibrates on an external reference, the 11683A, the minus
/// sign meaning external (p. 3-48, p. 4-6). RL1 turns REL on with the next reading as its reference and RL0 off; a
/// sensor select would turn it off (pp. 3-74 to 3-75), so AP/BP are not resent.
/// </remarks>
public sealed class Hp438APowerMeter(IVisaSession session, char sensor = 'A', Action<TimeSpan>? sleep = null) : IPowerMeter, IPowerMeterControl
{
    private readonly Action<TimeSpan> _sleep = sleep ?? Thread.Sleep;
    private bool _relative;
    private bool _controlled;

    public string Description { get; private set; } = "HP 438A";
    public bool OverBus => true;
    public bool CanCalibrate => true;
    public bool CanSetCalFactor => true;

    public void Identify()
    {
        // "HP438A,VERX.XX"
        var raw = session.Query("?ID").Trim();
        if (!raw.Contains("438A", StringComparison.OrdinalIgnoreCase))
            throw new PowerMeterException($"{session.ResourceName}: expected an HP 438A, but ?ID returned '{raw}'.");
        Description = $"HP 438A sensor {sensor}" + (raw.Contains(',') ? $", {raw[(raw.IndexOf(',') + 1)..].Trim()}" : "");
    }

    public double ReadWatts()
    {
        session.Write(sensor == 'B' ? "BP" : "AP");
        session.Write("LN");
        var value = ScpiNumber.Parse(session.Query("TR2"), "a power reading");
        session.Write("TR3");
        return value;
    }

    public void Preset()
    {
        _controlled = true;
        session.Write("PR" + (sensor == 'B' ? "BP" : "AP") + "LN");
        _relative = false;
    }

    public void Apply(MeterSettings settings, double? calFactorPercent)
    {
        if (settings.Filter is not null || settings.Resolution is not null)
            throw new NotSupportedException("The app does not set the 438A's filter or resolution (Preset leaves the filter automatic).");
        var codes = "";
        var display = settings.StoreReference ? MeterDisplay.DbRelative : settings.Display;
        if (display is { } d)
        {
            if (d != MeterDisplay.DbRelative && _relative)
            {
                codes += "RL0";
                _relative = false;
            }
            codes += d == MeterDisplay.Watts ? "LN" : "LG";
            if (d == MeterDisplay.DbRelative && (settings.StoreReference || !_relative))
            {
                codes += "RL1";
                _relative = true;
            }
        }
        if (settings.Range is { } range)
            codes += range == 0 ? "RA" : $"RM{range}EN";
        session.Write(codes + $"KB{Hp437BPowerMeter.Percent(calFactorPercent ?? 100)}EN");
    }

    public void Zero() => CalZeroWait.Run(session, "CSZE", "zero", _sleep, TimeSpan.FromSeconds(30));

    public void Calibrate(double referenceCalFactorPercent) =>
        CalZeroWait.Run(session, $"CSCL-{Hp437BPowerMeter.Percent(referenceCalFactorPercent)}EN", "CAL", _sleep, TimeSpan.FromSeconds(60));

    public double Read()
    {
        var value = Hp437BPowerMeter.HpReading(session.Query("TR2"), "438A");
        session.Write("TR3");
        return value;
    }

    public void Dispose() => PowerMeterSession.Close(session, _controlled ? "TR3" : null);
}

/// <summary>
/// HP 436A over its HP-IB codes (436A manual Section III): 9A+T selects autorange, watts, cal factor disabled and a
/// triggered reading with settling time; the meter answers with a 14-character string (Table 3-4) such as
/// "PKA 1000E-06" = +1000 × 10⁻⁶ W. With <see cref="UseCalFactorSwitch"/> it sends 9A-T so the front-panel CAL FACTOR
/// switch applies.
/// </summary>
/// <remarks>
/// Under control: every reading sends range (1–5, 9 auto), mode (A watt, B dB REL, C dB REF, D dBm), cal factor
/// (+ disabled, - the front-panel switch) and T, in that order (Table 3-3, p. 3-17). Z zeroes remotely, read
/// until the value is under 2 counts, then readings until the auto-zero status (T, U) clears (Example 2, p. 3-18).
/// CAL ADJ and the CAL FACTOR switch are front-panel only (para 3-15).
/// </remarks>
public sealed partial class Hp436APowerMeter(IVisaSession session, Action<TimeSpan>? sleep = null) : IPowerMeter, IPowerMeterControl
{
    private readonly Action<TimeSpan> _sleep = sleep ?? Thread.Sleep;
    private char _range = '9';
    private char _mode = 'A';
    private char _calFactor = '+';

    public string Description => "HP 436A";
    public bool OverBus => true;
    public bool CanCalibrate => false;
    public bool CanSetCalFactor => false;

    /// <summary>Send '-' (cal factor enable) instead of '+', for the Calibration Factor test.</summary>
    public bool UseCalFactorSwitch { get; set; }

    /// <summary>The 436A has no identity query; a reading that parses is the check.</summary>
    public void Identify() => ReadWatts();

    public double ReadWatts() => Parse(session.Query(UseCalFactorSwitch ? "9A-T" : "9A+T"));

    /// <summary>
    /// Device clear: Watt, autorange, cal factor disabled, Hold (p. 3-23). The first reading after it can be stale
    /// (para 3-31), so one is taken and dropped after the 2.5 s the meter may take to respond.
    /// </summary>
    public void Preset()
    {
        session.Clear();
        _sleep(TimeSpan.FromSeconds(2.5));
        (_range, _mode, _calFactor) = ('9', 'A', '+');
        session.Query(Program);
    }

    public void Apply(MeterSettings settings, double? calFactorPercent)
    {
        if (settings.Filter is not null || settings.Resolution is not null)
            throw new NotSupportedException("The 436A has no filter or resolution setting.");
        if (settings.Range is { } range)
            _range = range == 0 ? '9' : (char)('0' + range);
        _calFactor = calFactorPercent is null ? '+' : '-';
        if (settings.Display is { } display)
            _mode = display switch { MeterDisplay.Watts => 'A', MeterDisplay.DbRelative => 'B', _ => 'D' };
        if (settings.StoreReference)
        {
            // dB [REF]: the present input becomes the reference (Example 1, p. 3-17); readings are then dB REL.
            Decode(session.Query($"{_range}C{_calFactor}T"), allowUnderRange: true);
            _mode = 'B';
        }
    }

    public void Zero()
    {
        for (var i = 0; i < 20; i++)
        {
            var r = Decode(session.Query("Z1T"), allowUnderRange: true);
            if (Math.Abs(r.Digits) < 2)
                break;
        }
        for (var i = 0; i < 10; i++)
        {
            var r = Decode(session.Query("9A+T"), allowUnderRange: true);
            if (r.Status is not ('T' or 'U'))
                return;
        }
        throw new PowerMeterException("436A: the auto-zero did not finish; check the 11683A is in STANDBY.");
    }

    public void Calibrate(double referenceCalFactorPercent) =>
        throw new NotSupportedException("The 436A's CAL ADJ is a front-panel adjustment.");

    public double Read() => Decode(session.Query(Program), allowUnderRange: true).Value;

    private string Program => $"{_range}{_mode}{_calFactor}T";

    [GeneratedRegex(@"^([P-V])([I-M])([A-D])([ +-])(\d{4})E-(\d{2})")]
    private static partial Regex Format();

    internal readonly record struct Reply(char Status, char Mode, int Digits, double Value);

    /// <summary>Decodes the output string: status, range, mode, sign, four digits, E, -, two-digit exponent.</summary>
    internal static Reply Decode(string reply, bool allowUnderRange)
    {
        var m = Format().Match(reply.TrimEnd('\r', '\n'));
        if (!m.Success)
            throw new PowerMeterException($"Could not read the 436A reply '{reply}'.");
        var status = m.Groups[1].Value[0];
        if (status is 'R' or 'V')
            throw new PowerMeterException(status == 'R' ? "436A over range." : "436A over range while zeroing: power is applied to the sensor.");
        if (status == 'S' && !allowUnderRange)
            throw new PowerMeterException($"436A under range in dBm or dB ('{reply}').");
        var sign = m.Groups[4].Value == "-" ? -1 : 1;
        var digits = sign * int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
        var exponent = int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture);
        return new Reply(status, m.Groups[3].Value[0], digits, digits * Math.Pow(10, -exponent));
    }

    /// <summary>A watt-mode reply, in watts.</summary>
    internal static double Parse(string reply)
    {
        var r = Decode(reply, allowUnderRange: false);
        if (r.Mode != 'A')
            throw new PowerMeterException($"436A is not in watt mode ('{reply}').");
        return r.Value;
    }

    public void Dispose() => session.Dispose();
}

public static class PowerMeters
{
    /// <summary>The models with a bus driver. "manual" means the operator types the readings in.</summary>
    public static readonly IReadOnlyList<string> BusModels = ["E4418B", "E4419B", "437B", "438A", "436A"];

    /// <param name="channel">1 or 2 (E4419B), or the 438A sensor (1 = A, 2 = B).</param>
    public static IPowerMeter Open(string model, IVisaSession session, int channel) => model.ToUpperInvariant() switch
    {
        "E4418B" or "E4419B" or "E4416A" or "E4417A" => new E441xPowerMeter(session, channel),
        "437B" => new Hp437BPowerMeter(session),
        "438A" => new Hp438APowerMeter(session, channel == 2 ? 'B' : 'A'),
        "436A" => new Hp436APowerMeter(session),
        _ => throw new ArgumentException($"No bus driver for power meter '{model}'. Use {string.Join(", ", BusModels)} or manual."),
    };
}

/// <summary>Closing a meter the app controlled: back to free run where it was held, without hiding an earlier error.</summary>
internal static class PowerMeterSession
{
    public static void Close(IVisaSession session, string? freeRun)
    {
        try
        {
            if (freeRun is not null)
                session.Write(freeRun);
        }
        catch (InstrumentException)
        {
            // The meter isn't answering; its front panel's LOCAL key puts it back.
        }
        finally
        {
            session.Dispose();
        }
    }
}
