namespace Verify11683A.Instruments.Dvm;

/// <summary>
/// The DVM must read its FRONT terminals, where the wiring puts the cable. Neither DVM can select them over the bus,
/// so the operator is asked to press the switch. On REAR the input floats: the bench saw about 11 mV, which the ohms
/// safety check refuses with no hint of the cause.
/// </summary>
public static class TerminalsCheck
{
    /// <summary>What to tell the operator, or null when the DVM is on its front terminals.</summary>
    public static string? Problem(string model, DvmTerminals terminals) => terminals switch
    {
        DvmTerminals.Front => null,
        DvmTerminals.Rear => $"The {model} is reading its REAR terminals. Press the front-panel {Button(model)} button so FRONT is selected.",
        DvmTerminals.Open => $"The {model} reports its input terminals OPEN. Press the front-panel {Button(model)} button so FRONT is selected.",
        _ => $"The {model} did not say which terminals it is reading. Check that its front-panel {Button(model)} button selects FRONT.",
    };

    /// <summary>The 34401A's button is Front/Rear; the 3458A's is Terminals (3458A User's Guide p. 254).</summary>
    public static string Button(string model) => model.Contains("3458", StringComparison.Ordinal) ? "Terminals" : "Front/Rear";

    /// <summary>
    /// Reads the switch until it is on Front. Each time it is not, <paramref name="waitForOperator"/> is given the message
    /// and returns once the operator has pressed the button, or false to give up. A reply the driver could not read is
    /// returned at once: the operator cannot change it. Returns the last reading.
    /// </summary>
    public static DvmTerminals Ensure(IDvm dvm, Func<string, bool> waitForOperator, Action<string>? log = null)
    {
        while (true)
        {
            var terminals = dvm.ReadTerminals();
            var problem = Problem(dvm.Model, terminals);
            log?.Invoke($"Terminals: {terminals.ToString().ToUpperInvariant()}" + (problem is null ? "" : $": {problem}"));
            if (problem is null || terminals == DvmTerminals.Unknown || !waitForOperator(problem))
                return terminals;
        }
    }
}
