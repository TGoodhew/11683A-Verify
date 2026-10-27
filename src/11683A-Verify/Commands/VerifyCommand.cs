using Spectre.Console;
using Verify11683A.App.Procedures;
using Verify11683A.App.Reports;
using Verify11683A.Results;
using Verify11683A.Uncertainty;

namespace Verify11683A.App.Commands;

/// <summary>The Range Switch Performance Test: Part 1, then Part 2, in one session.</summary>
public static class VerifyCommand
{
    public static int Run(AppConfig config, CommandLine options)
    {
        // The H01 REF EXT check is meter-test's Procedure 2, automated there; it is not part of this run.
        using var session = VerificationSession.Start(config, options, "verify", ["Wiring", "Warm-up", "Part 1", "Part 2", "Results"]);
        var screens = session.Screens;
        Preparation.Run(session);

        var part1 = Part1Procedure.Run(session);
        screens.Begin("Part 1", "Part 1 results");
        screens.Report(() => ConsoleReport.Part1(part1, session.Limits, session.Convention));

        Part2Result? part2 = null;
        if (part1.SupplyAdjustmentRecommended
            && !ConsoleUi.Confirm("The 1 mW voltage is outside its limits. Go on to Part 2 anyway?"))
        {
            screens.Skip("Part 2");
            AnsiConsole.MarkupLine("[yellow]Part 2 not run.[/]");
        }
        else
        {
            if (!part1.SupplyAdjustmentRecommended)
                screens.Pause("Press Enter for Part 2");
            part2 = Interrupted(session, "Part 2", () => Part2Procedure.Run(session));
            if (part2 is not null)
            {
                screens.Begin("Part 2", "Part 2 results");
                screens.Report(() => ConsoleReport.Part2(part2, session.Limits, session.Convention));
            }
        }

        AnsiConsole.WriteLine();
        screens.Pause();

        var run = session.CreateRun(part1, part2);
        session.SaveAndSummarise(run);
        return run.Complete && run.Verdict == Verdict.Pass ? 0 : 1;
    }

    /// <summary>
    /// A later part that an instrument error (or the operator's Stop) ends: it is reported as not run, and what the
    /// earlier parts measured is still saved.
    /// </summary>
    private static T? Interrupted<T>(VerificationSession session, string part, Func<T?> run) where T : class
    {
        try
        {
            return run();
        }
        catch (Exception ex) when (ex is Instruments.Visa.InstrumentException or OperatorStopException)
        {
            session.Log.Write($"{part} stopped: {ex.Message}");
            AnsiConsole.MarkupLine($"[red]✗ {part} stopped: {Markup.Escape(ex.Message)}[/] [grey]The earlier parts are still saved.[/]");
            session.Screens.Skip(part);
            return null;
        }
    }
}
