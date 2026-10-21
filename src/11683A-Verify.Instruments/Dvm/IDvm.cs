using Verify11683A.Instruments.Visa;

namespace Verify11683A.Instruments.Dvm;

/// <summary>What the DVM reports about itself. Serial is empty when the instrument cannot report it.</summary>
public sealed record DvmIdentity(string Manufacturer, string Model, string Serial, string Firmware, string Raw)
{
    public override string ToString() =>
        $"{Manufacturer} {Model}" + (Serial is "" or "0" ? "" : $" s/n {Serial}") + (Firmware == "" ? "" : $" fw {Firmware}");
}

/// <summary>One reading. Overload readings carry the instrument's overload value and are never used as results.</summary>
public readonly record struct DvmReading(double Value, bool Overload, DateTimeOffset Time);

/// <summary>The configuration a driver applied and then confirmed by reading it back.</summary>
public sealed record DvmConfiguration(
    DvmFunction Function,
    double Range,
    double Nplc,
    bool AutoZero,
    bool HighImpedance,
    string InputResistance);

/// <summary>
/// Which input terminals the DVM's front-panel switch selects. Neither DVM can set it over the bus, and the wiring
/// uses the front terminals. <see cref="Unknown"/> is a reply the driver could not read.
/// </summary>
public enum DvmTerminals
{
    Unknown,
    Front,
    Rear,

    /// <summary>The 3458A's TERM OPEN (numeric 0). Its manual says TERM? reports only FRONT or REAR.</summary>
    Open,
}

/// <summary>A DVM failure: unexpected identity, rejected command, or a configuration that did not read back.</summary>
public sealed class DvmException(string message) : InstrumentException(message);

/// <summary>
/// A DVM that measures the 11683A DC REFERENCE OUTPUT. Implemented by the 34401A, the 3458A and the
/// simulator. Every result is taken on a fixed range; autorange is never used.
/// </summary>
public interface IDvm : IDisposable
{
    /// <summary>The model, which is also the key of its spec table (data/dvm-specs-&lt;model&gt;.json).</summary>
    string Model { get; }

    string ResourceName { get; }

    DvmConfiguration? Configuration { get; }

    /// <summary>Queries the identity and confirms the instrument is the expected model.</summary>
    DvmIdentity Identify();

    /// <summary>Device clear, reset to a known state, and an empty error queue.</summary>
    void Reset();

    /// <summary>The fixed ranges the driver accepts for a function, smallest first.</summary>
    IReadOnlyList<double> Ranges(DvmFunction function);

    /// <summary>
    /// Fixed range, the given integration time, autozero on, and (DCV) the high input impedance on every range
    /// that has it. Reads the settings back and throws <see cref="DvmException"/> if they did not take.
    /// </summary>
    DvmConfiguration Configure(DvmFunction function, double range, double nplc);

    /// <summary>Triggers and returns one reading on the present configuration.</summary>
    DvmReading Read();

    /// <summary>Reads and clears the instrument's error queue. Empty when there are no errors.</summary>
    IReadOnlyList<string> ReadErrors();

    /// <summary>Queries the front-panel Front/Rear terminals switch. It cannot be set over the bus.</summary>
    DvmTerminals ReadTerminals();

    /// <summary>The instrument's own report of its present settings, for the bench check.</summary>
    IReadOnlyList<KeyValuePair<string, string>> ReadBackSettings();
}
