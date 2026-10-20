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
        var h01 = config.Calibrator.Options.Contains("H01", StringComparison.OrdinalIgnoreCase);
        using var session = VerificationSession.Start(config, options, "verify",
            h01 ? ["Wiring", "Warm-up", "Part 1", "Part 2", "Part 4", "Results"] : ["Wiring", "Warm-up", "Part 1", "Part 2", "Results"]);
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
            part2 = Part2Procedure.Run(session);
            screens.Begin("Part 2", "Part 2 results");
            screens.Report(() => ConsoleReport.Part2(part2, session.Limits, session.Convention));
        }

        Part4Result? part4 = null;
        AnsiConsole.WriteLine();
        if (h01 && options.Part4)
            screens.Pause("Press Enter for Part 4");
        if (h01 && (options.Part4 || ConsoleUi.Confirm("Run Part 4, the Option H01 REF EXT check? It needs a power meter.", defaultValue: false)))
        {
            part4 = Part4Procedure.Run(session, part1.Point("1mW")?.Volts);
            if (part4 is not null)
            {
                screens.Begin("Part 4", "Part 4 results");
                screens.Report(() => ConsoleReport.Part4(part4));
                screens.Pause();
            }
        }
        else if (!h01)
        {
            screens.Pause();
        }
        if (h01 && part4 is null)
            screens.Skip("Part 4");

        var run = session.CreateRun(part1, part2, part4: part4);
        session.SaveAndSummarise(run);
        return run.Complete && run.Verdict == Verdict.Pass ? 0 : 1;
    }
}
