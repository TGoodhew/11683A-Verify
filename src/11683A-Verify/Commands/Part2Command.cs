using Spectre.Console;
using Verify11683A.App.Procedures;
using Verify11683A.App.Reports;
using Verify11683A.Uncertainty;

namespace Verify11683A.App.Commands;

/// <summary>Runs Part 2 on its own: four-wire resistance with FUNCTION in STANDBY.</summary>
public static class Part2Command
{
    public static int Run(AppConfig config, CommandLine options)
    {
        using var session = VerificationSession.Start(config, options, "part2");
        var result = Part2Procedure.Run(session);
        AnsiConsole.WriteLine();
        ConsoleReport.Part2(result, session.Limits, session.Convention);
        ConsoleUi.SessionLogLine(session.Log);
        return result.Complete && result.Verdict == Verdict.Pass ? 0 : 1;
    }
}
