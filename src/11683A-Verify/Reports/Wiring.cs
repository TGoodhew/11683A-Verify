using Spectre.Console;

namespace Verify11683A.App.Reports;

/// <summary>The DVM's connection to the 11683A for Parts 1 and 2 (11683A manual p. 7 and Table 2; rear panel).</summary>
public static class Wiring
{
    // Two pairs, each joined only at the BNC plug: HI + HI Sense on the centre pin, LO + LO Sense on the shell.
    public const string Diagram = """
         DVM front terminals                       11683A rear panel
         ┌───────────────┐                         ┌────────────────────────────┐
         │  HI        ───┼───┐                     │  REF EXT ◎        (nothing)│
         │  HI Sense  ───┼───┴─ centre ─┐  BNC     │                            │
         │               │              ├─────────►│  DC REFERENCE OUTPUT ◎     │
         │  LO        ───┼───┬─ shell ──┘          │                            │
         │  LO Sense  ───┼───┘                     └────────────────────────────┘
         └───────────────┘   4-wire, ≤ 5 ft
        """;

    /// <summary>The Connect panel's rows: each connection and the 11683A rear switches it needs.</summary>
    public static IReadOnlyList<(string Name, string Value)> Connections(bool h01)
    {
        var rows = new List<(string, string)>
        {
            ("DVM terminals", "FRONT"),
            ("DVM HI + HI Sense", "11683A DC REFERENCE OUTPUT centre (lower BNC)"),
            ("DVM LO + LO Sense", "11683A DC REFERENCE OUTPUT shell"),
            ("Cable", "4-wire, ≤ 5 ft, pairs joined at the BNC plug, no adapters or tees"),
        };
        if (h01)
        {
            rows.Add(("11683A REF EXT", "nothing connected"));
            rows.Add(("11683A REF switch", "INT (rear panel)"));
        }
        rows.Add(("11683A power", "plugged in, LINE ON"));
        return rows;
    }

    /// <param name="h01">The 11683A has Option H01, so REF EXT and the REF switch are on its rear panel.</param>
    /// <param name="heading">Write a "Wiring" heading first; not on the run's Wiring screen, which has its own.</param>
    public static void Write(bool h01, bool heading = true) => Write(AnsiConsole.Console, h01, heading);

    public static void Write(IAnsiConsole console, bool h01, bool heading = true)
    {
        if (heading)
            console.Write(new Rule("[bold]Wiring[/]").LeftJustified());
        // The raw string has no final newline, so end the diagram's last line before the panel.
        console.Write(new Text(Diagram + "\n"));
        console.Write(ConsoleUi.Settings("Connect", [.. Connections(h01)]));
        console.WriteLine();
    }
}
