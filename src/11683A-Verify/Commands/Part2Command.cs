using Verify11683A.App.Procedures;
using Verify11683A.App.Reports;
using Verify11683A.Uncertainty;

namespace Verify11683A.App.Commands;

/// <summary>Runs Part 2 on its own: four-wire resistance with FUNCTION in STANDBY.</summary>
public static class Part2Command
{
    public static int Run(AppConfig config, CommandLine options)
    {
        using var session = VerificationSession.Start(config, options, "part2", "Wiring", "Warm-up", "Part 2", "Results");
        Preparation.Run(session);
        var result = Part2Procedure.Run(session);
        session.Screens.Begin("Part 2", "Part 2 results");
        session.Screens.Report(() => ConsoleReport.Part2(result, session.Limits, session.Convention));
        session.Screens.Pause();
        session.SaveAndSummarise(session.CreateRun(null, result), offerActuals: false);
        return result.Complete && result.Verdict == Verdict.Pass ? 0 : 1;
    }
}
