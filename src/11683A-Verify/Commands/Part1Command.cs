using Verify11683A.App.Procedures;
using Verify11683A.App.Reports;
using Verify11683A.Uncertainty;

namespace Verify11683A.App.Commands;

/// <summary>Runs Part 1 on its own: the voltage and ratio test.</summary>
public static class Part1Command
{
    public static int Run(AppConfig config, CommandLine options)
    {
        using var session = VerificationSession.Start(config, options, "part1", "Wiring", "Warm-up", "Part 1", "Results");
        Preparation.Run(session);
        var result = Part1Procedure.Run(session);
        session.Screens.Begin("Part 1", "Part 1 results");
        session.Screens.Report(() => ConsoleReport.Part1(result, session.Limits, session.Convention));
        session.Screens.Pause();
        session.SaveAndSummarise(session.CreateRun(result, null));
        return result.Complete && result.Verdict == Verdict.Pass ? 0 : 1;
    }
}
