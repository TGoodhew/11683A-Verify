using Spectre.Console;
using Verify11683A.Calibrator;
using Verify11683A.Instruments.Simulation;

namespace Verify11683A.App;

/// <summary>
/// The simulated 11683A's front panel at the console, for a simulation walkthrough: where the bench would have the
/// operator turn a knob, the walkthrough lets them choose the position, including wrong ones, to see what the app does.
/// </summary>
public static class SimulatorKnobs
{
    private const string OtherSwitches = "Other switches (FUNCTION, POLARITY, REF)…";

    public static string State(Simulated11683A sim) =>
        $"RANGE {sim.Range.Label}, FUNCTION {sim.Function.ToString().ToUpperInvariant()}, "
        + $"POLARITY {sim.Polarity.ToString().ToUpperInvariant()}, REF {sim.Reference.ToString().ToUpperInvariant()}";

    /// <summary>Turns the simulated RANGE switch. The positions still to measure are listed first.</summary>
    public static void ChooseRange(Simulated11683A sim, IReadOnlyList<string> remaining, IAnsiConsole? console = null)
    {
        console ??= AnsiConsole.Console;
        while (true)
        {
            var choices = remaining.Select(r => (Id: r, Text: $"{RangeSwitch.Get(r).Label}  (still to measure)"))
                .Concat(RangeSwitch.Positions.Where(p => !remaining.Contains(p.Id)).Reverse().Select(p => (p.Id, Text: p.Label)))
                .ToList();
            var pick = console.Prompt(new SelectionPrompt<string>()
                .Title($"[yellow]Simulator[/]: turn the 11683A RANGE switch to… [grey](now {Markup.Escape(State(sim))})[/]")
                .PageSize(14)
                .AddChoices(choices.Select(c => c.Text).Append(OtherSwitches)));
            if (pick == OtherSwitches)
            {
                Adjust(sim, console);
                continue;
            }
            sim.SetRange(choices.Single(c => c.Text == pick).Id);
            return;
        }
    }

    /// <summary>Lets the operator set any of the simulated switches until they choose Done.</summary>
    public static void Adjust(Simulated11683A sim, IAnsiConsole? console = null)
    {
        console ??= AnsiConsole.Console;
        while (true)
        {
            var pick = console.Prompt(new SelectionPrompt<string>()
                .Title($"[yellow]Simulator[/]: 11683A is at {Markup.Escape(State(sim))}. Change…")
                .AddChoices("Done", "RANGE", "FUNCTION", "POLARITY", "REF"));
            switch (pick)
            {
                case "Done":
                    return;
                case "RANGE":
                    sim.SetRange(console.Prompt(new SelectionPrompt<RangePosition>().Title("RANGE").PageSize(12)
                        .UseConverter(p => p.Label).AddChoices(RangeSwitch.Positions.Reverse())).Id);
                    break;
                case "FUNCTION":
                    sim.SetFunction(console.Prompt(new SelectionPrompt<FunctionSwitch>().Title("FUNCTION").AddChoices(Enum.GetValues<FunctionSwitch>())));
                    break;
                case "POLARITY":
                    sim.SetPolarity(console.Prompt(new SelectionPrompt<PolaritySwitch>().Title("POLARITY").AddChoices(Enum.GetValues<PolaritySwitch>())));
                    break;
                case "REF":
                    sim.SetReference(console.Prompt(new SelectionPrompt<ReferenceSwitch>().Title("REF").AddChoices(Enum.GetValues<ReferenceSwitch>())));
                    break;
            }
        }
    }
}
