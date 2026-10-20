using Verify11683A.App.Procedures;
using Verify11683A.App.Reports;

namespace Verify11683A.App.Commands;

/// <summary>Runs Part 4 on its own: the Option H01 REF EXT transfer check (informational).</summary>
public static class Part4Command
{
    public static int Run(AppConfig config, CommandLine options)
    {
        using var session = VerificationSession.Start(config, options, "part4", "Part 4", "Results");
        var result = Part4Procedure.Run(session, oneMilliwattVolts: null);
        if (result is null)
            return 1;
        session.Screens.Begin("Part 4", "Part 4 results");
        session.Screens.Report(() => ConsoleReport.Part4(result));
        session.Screens.Pause();
        session.SaveAndSummarise(session.CreateRun(null, null, part4: result), offerActuals: false);
        return result.AllWithin ? 0 : 1;
    }
}
