using System.Globalization;
using Spectre.Console;
using Verify11683A.App.Procedures;
using Verify11683A.App.Reports;
using Verify11683A.Uncertainty;

namespace Verify11683A.App.Commands;

/// <summary>
/// The Adjust menu. Part 3, the Power Supply Adjustment assist (A2R1), then an offer to rerun Part 1. The manual's
/// FET Balance Adjustment needs an oscilloscope and the A3 assembly removed, so it is not offered (see the README).
/// </summary>
public static class AdjustCommand
{
    public static int Run(AppConfig config, CommandLine options)
    {
        using var session = VerificationSession.Start(config, options, "adjust", "Part 3", "Part 1", "Results");
        var final = SupplyAdjustmentProcedure.Run(session);
        if (final is null)
            return 1;

        AnsiConsole.WriteLine();
        if (!ConsoleUi.Confirm("Rerun Part 1 now to confirm the ratios and the 1 mW voltage?", defaultValue: true))
        {
            session.Screens.Skip("Part 1");
            session.Screens.Skip("Results");
            session.Screens.Finish();
            AnsiConsole.MarkupLine("[yellow]Rerun Part 1 (part1 or verify) before relying on the unit.[/]");
            ConsoleUi.SessionLogLine(session.Log);
            return 0;
        }

        // A2R1 moved: put the cover back on and let it settle before the voltages are recorded.
        AnsiConsole.MarkupLine("[grey]Refit the top cover and let the 11683A settle for a few minutes before Part 1.[/]");
        session.Bench.Step("Press Enter to start Part 1");

        var part1 = Part1Procedure.Run(session);
        session.Screens.Begin("Part 1", "Part 1 results");
        session.Screens.Report(() => ConsoleReport.Part1(part1, session.Limits, session.Convention));
        session.Screens.Pause();
        session.SaveAndSummarise(session.CreateRun(part1, null,
            [string.Create(CultureInfo.InvariantCulture, $"Part 1 run after the Power Supply Adjustment: A2R1 left at {final.Volts * 1e3:0.000} mV ({final.State}).")]));
        return part1.Complete && part1.Verdict == Verdict.Pass ? 0 : 1;
    }
}
