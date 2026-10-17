using Verify11683A.App.Procedures;
using Verify11683A.App.Reports;
using Verify11683A.Uncertainty;

namespace Verify11683A.App.Commands;

/// <summary>Runs Part 1 on its own: the voltage and ratio test.</summary>
public static class Part1Command
{
    public static int Run(AppConfig config, CommandLine options)
    {
        using var session = VerificationSession.Start(config, options, "part1");
        var result = Part1Procedure.Run(session);
        Spectre.Console.AnsiConsole.WriteLine();
        ConsoleReport.Part1(result, session.Limits, session.Convention);
        session.Save(session.CreateRun(result, null));
        ConsoleUi.SessionLogLine(session.Log);
        return result.Complete && result.Verdict == Verdict.Pass ? 0 : 1;
    }
}
