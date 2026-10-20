using Spectre.Console;

namespace Verify11683A.App.Reports;

/// <summary>The wiring diagram and the 4-wire cable rules (11683A manual p. 7 and Table 2; rear panel).</summary>
public static class Wiring
{
    public const string Diagram = """
         DVM front terminals                         11683A rear panel
         ┌────────────────────┐                      ┌───────────────────────────────┐
         │  HI        ───┐    │                      │  REF EXT ◎   (H01 input,      │
         │  HI Sense  ───┤    │                      │               Part 4 only)    │
         │               ├────┼──── 4-wire cable ────┼─► DC REFERENCE OUTPUT ◎       │
         │  LO        ───┤    │     ≤ 5 ft, pairs    │      centre: HI + HI Sense    │
         │  LO Sense  ───┘    │     joined at the    │      shell:  LO + LO Sense    │
         └────────────────────┘     BNC plug         │   100 MEG LOAD MIN            │
                                                     └───────────────────────────────┘
        """;

    public static readonly IReadOnlyList<string> Rules =
    [
        "Measure at DC REFERENCE OUTPUT, the lower BNC. REF EXT, the upper one, is the Option H01 input (Part 4 only).",
        "Run the 4-wire cable straight to the 11683A BNC and make the Kelvin split at the plug: no adapters or tees, "
            + "whose contact resistance would be measured with the 11683A in Part 2.",
        "Keep the cable to 5 ft or less (manual Table 2 and the note on p. 7).",
        "The output needs at least 100 MΩ: the DVM's >10 GΩ input is used on every range up to 10 V. "
            + "The 100 mW reading (~15.8 V) is on the 100 V range, 10 MΩ, as with the manual's HP 3455A.",
        "Ohms current is applied only in STANDBY, after a DC reading shows the output is grounded.",
    ];

    public static void Write()
    {
        AnsiConsole.Write(new Rule("[bold]Wiring[/]").LeftJustified());
        // The raw string has no final newline, so end the diagram's last line before the rules.
        AnsiConsole.Write(new Text(Diagram + "\n"));
        foreach (var rule in Rules)
            AnsiConsole.MarkupLine($"[grey]•[/] {Markup.Escape(rule)}");
        AnsiConsole.WriteLine();
    }
}
