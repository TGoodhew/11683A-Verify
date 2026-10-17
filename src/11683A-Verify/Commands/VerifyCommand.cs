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
        using var session = VerificationSession.Start(config, options, "verify");
        Preparation.Run(session);

        var part1 = Part1Procedure.Run(session);
        AnsiConsole.WriteLine();
        ConsoleReport.Part1(part1, session.Limits, session.Convention);
        AnsiConsole.WriteLine();

        Part2Result? part2 = null;
        if (part1.SupplyAdjustmentRecommended
            && !ConsoleUi.Confirm("The 1 mW voltage is outside its limits. Go on to Part 2 anyway?"))
        {
            AnsiConsole.MarkupLine("[yellow]Part 2 not run.[/]");
        }
        else
        {
            part2 = Part2Procedure.Run(session);
            AnsiConsole.WriteLine();
            ConsoleReport.Part2(part2, session.Limits, session.Convention);
        }

        AnsiConsole.WriteLine();
        var run = session.CreateRun(part1, part2);
        var path = session.Save(run);
        AnsiConsole.WriteLine();
        ConsoleReport.Summary(run, path);
        ConsoleUi.SessionLogLine(session.Log);
        return run.Complete && run.Verdict == Verdict.Pass ? 0 : 1;
    }
}
