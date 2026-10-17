using Spectre.Console;
using Verify11683A.App.Procedures;
using Verify11683A.App.Reports;

namespace Verify11683A.App.Commands;

/// <summary>Runs Part 4 on its own: the Option H01 REF EXT transfer check (informational).</summary>
public static class Part4Command
{
    public static int Run(AppConfig config, CommandLine options)
    {
        using var session = VerificationSession.Start(config, options, "part4");
        var result = Part4Procedure.Run(session, oneMilliwattVolts: null);
        if (result is null)
            return 1;
        AnsiConsole.WriteLine();
        ConsoleReport.Part4(result);
        var run = session.CreateRun(null, null, part4: result);
        var path = session.Save(run);
        AnsiConsole.WriteLine();
        ConsoleReport.Summary(run, path);
        ConsoleUi.SessionLogLine(session.Log);
        return result.AllWithin ? 0 : 1;
    }
}
