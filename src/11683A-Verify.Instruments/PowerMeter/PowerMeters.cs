using System.Text.RegularExpressions;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Instruments.Visa;

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

public sealed class PowerMeterException(string message) : Exception(message);

/// <summary>
/// HP/Agilent E4418B/E4419B (and E4416A/E4417A) over SCPI. Reads FETCh? in whatever units the meter displays
/// (UNIT:POWer? says which), so the front-panel set-up is left as the operator made it.
/// </summary>
public sealed class E441xPowerMeter(IVisaSession session, int channel = 1) : IPowerMeter
{
    public string Description { get; private set; } = "E441x power meter";
    public bool OverBus => true;

    public void Identify()
    {
        // e.g. "Agilent Technologies,E4418B,GB12345678,A1.06.00"
        var raw = session.Query("*IDN?").Trim();
        var fields = raw.Split(',');
        if (fields.Length < 4 || !Regex.IsMatch(fields[1], "E441[6-9]"))
            throw new PowerMeterException($"{session.ResourceName}: expected an E4416A–E4419B, but *IDN? returned '{raw}'.");
        Description = $"{fields[0].Trim()} {fields[1].Trim()} s/n {fields[2].Trim()}, fw {fields[3].Trim()}, channel {channel}";
    }

    public double ReadWatts()
    {
        var unit = session.Query($"UNIT{channel}:POW?").Trim().Trim('"').ToUpperInvariant();
        var value = ScpiNumber.Parse(session.Query($"FETC{channel}?"), "a power reading");
        var error = session.Query("SYST:ERR?").Trim();
        if (!error.StartsWith("+0", StringComparison.Ordinal) && !error.StartsWith("0", StringComparison.Ordinal))
            throw new PowerMeterException($"{session.ResourceName}: {error}");
        return unit.StartsWith("DBM", StringComparison.Ordinal) ? DbmToWatts(value) : value;
    }

    public void Dispose() => session.Dispose();

    internal static double DbmToWatts(double dbm) => Math.Pow(10, dbm / 10) * 1e-3;
}

/// <summary>
/// HP 437B over its HP-IB codes. LN selects watts (the E4418B procedure reads in watts too), TR2 takes one settled
/// reading, and TR3 returns the meter to free run.
/// </summary>
public sealed class Hp437BPowerMeter(IVisaSession session) : IPowerMeter
{
    public string Description { get; private set; } = "HP 437B";
    public bool OverBus => true;

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

    public void Dispose() => session.Dispose();
}

/// <summary>HP 438A, sensor A, over its HP-IB codes: AP, LN (watts), TR2 for one settled reading, TR3 back to free run.</summary>
public sealed class Hp438APowerMeter(IVisaSession session) : IPowerMeter
{
    public string Description { get; private set; } = "HP 438A";
    public bool OverBus => true;

    public void Identify()
    {
        // "HP438A,VERX.XX"
        var raw = session.Query("?ID").Trim();
        if (!raw.Contains("438A", StringComparison.OrdinalIgnoreCase))
            throw new PowerMeterException($"{session.ResourceName}: expected an HP 438A, but ?ID returned '{raw}'.");
        Description = "HP 438A sensor A" + (raw.Contains(',') ? $", {raw[(raw.IndexOf(',') + 1)..].Trim()}" : "");
    }

    public double ReadWatts()
    {
        session.Write("AP");
        session.Write("LN");
        var value = ScpiNumber.Parse(session.Query("TR2"), "a power reading");
        session.Write("TR3");
        return value;
    }

    public void Dispose() => session.Dispose();
}

public static class PowerMeters
{
    /// <summary>The models with a bus driver. "manual" means the operator types the readings in.</summary>
    public static readonly IReadOnlyList<string> BusModels = ["E4418B", "E4419B", "437B", "438A"];

    public static IPowerMeter Open(string model, IVisaSession session, int channel) => model.ToUpperInvariant() switch
    {
        "E4418B" or "E4419B" or "E4416A" or "E4417A" => new E441xPowerMeter(session, channel),
        "437B" => new Hp437BPowerMeter(session),
        "438A" => new Hp438APowerMeter(session),
        _ => throw new ArgumentException($"No bus driver for power meter '{model}'. Use {string.Join(", ", BusModels)} or manual."),
    };
}
