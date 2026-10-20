using Verify11683A.Instruments.DcSource;

namespace Verify11683A.Instruments.Simulation;

/// <summary>
/// A simulated 5440B on the simulated 11683A's REF EXT input. On its DIVIDER terminals the REF EXT load pulls the
/// output a little low (the divider's source resistance is about 500 Ω), so the app's trim against the DVM has work to do.
/// </summary>
public sealed class SimulatedDcSource(Simulated11683A target, bool useDivider = true) : IDcSource
{
    /// <summary>How far low the divider output lands under load, as a fraction.</summary>
    public double DividerLoading { get; init; } = 60e-6;

    public string Description => "SIMULATED Fluke 5440B";

    public void Identify() => Standby();

    public DcTerminals TerminalsFor(double volts) =>
        useDivider && Math.Abs(volts) <= Fluke5440B.DividerMaximumVolts ? DcTerminals.Divider : DcTerminals.Output;

    public void Output(double volts) =>
        target.ExternalSourceVolts = volts * (TerminalsFor(volts) == DcTerminals.Divider ? 1 - DividerLoading : 1);

    public void Standby() => target.ExternalSourceVolts = 0;

    public void Dispose() => Standby();
}
