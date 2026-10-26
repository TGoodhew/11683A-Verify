using Spectre.Console;
using Verify11683A.App.Procedures;
using Verify11683A.App.Reports;
using Verify11683A.Meters;
using Verify11683A.Uncertainty;

namespace Verify11683A.App.Commands;

/// <summary>
/// Power meter performance tests with the 11683A: the operator picks a meter, its definition is loaded from
/// data/meters/&lt;model&gt;.json, and its tests are run row by row.
/// </summary>
public static class MeterTestCommand
{
    public static int Run(AppConfig config, CommandLine options)
    {
        var definitions = MeterDefinition.LoadAll();
        var definition = options.Meter is { } model
            ? definitions.FirstOrDefault(d => string.Equals(d.Model, model, StringComparison.OrdinalIgnoreCase))
              ?? throw new UsageException($"No meter definition for '{model}'. Available: {string.Join(", ", definitions.Select(d => d.Model))}.")
            : Choose(definitions);

        if (!options.IsSimulation && !ConsoleUi.Interactive)
            throw new OperatorException("A power meter test on the real bench needs you at the console (input and output not redirected): "
                + "it would otherwise go on without your confirmations.");
        using var bench = Bench.Open(config, options, $"meter-{definition.Model}");
        using var screens = new Screens($"{definition.Title} performance test" + (bench.IsSimulation ? $" (simulated: {bench.Simulator!.Scenario.Name})" : ""),
            ["Setup", "Results"])
        {
            Identity = $"with {config.Calibrator.Model} s/n {config.Calibrator.Serial}",
        };
        var run = new MeterTestProcedure(config, options, bench, definition, screens).Run();
        screens.Finish();
        if (run.Tests.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No tests were run.[/]");
            return 1;
        }

        foreach (var line in ResultLog.Meter(run))
            bench.Log.Write(line);
        var path = Saving.WithFallback(run.Save, config.ResultsDirectory, bench.Log);
        bench.Log.Write($"Meter results saved to {Path.GetFullPath(path)}");
        try
        {
            MeterReport.WriteBeside(run, path);
        }
        catch (Exception ex)
        {
            bench.Log.Write($"PDF record failed: {ex}");
            AnsiConsole.MarkupLine($"[red]The PDF record could not be written: {Markup.Escape(ex.Message)}[/]");
        }
        Saving.CopyLog(bench.Log, Path.ChangeExtension(path, ".log"));
        AnsiConsole.WriteLine();
        MeterReport.Summary(run, path);
        // An H01 test leaves the 11683A on REF EXT and unplugged; everything else needs it back on INT.
        if (definition.Tests.Any(t => t.NeedsH01 && run.Tests.Any(r => r.TestId == t.Id)))
            AnsiConsole.Write(new Panel(new Markup("Set the 11683A's rear [bold]REF switch back to INT[/], take the DC calibrator off REF EXT, "
                + "and plug the 11683A's power cord back in.")).Header(" Put the 11683A back ").BorderColor(Color.Yellow));
        Printing.Offer(() => MeterReport.Build(MeterRun.Load(path), Path.GetFileName(path)), path, bench.Log);
        ConsoleUi.SessionLogLine(bench.Log);
        return run.Complete && run.Verdict == Verdict.Pass ? 0 : 1;
    }

    private static MeterDefinition Choose(IReadOnlyList<MeterDefinition> definitions)
    {
        if (!ConsoleUi.Interactive)
            throw new UsageException($"Name the meter with --meter: {string.Join(", ", definitions.Select(d => d.Model))}.");
        return ConsoleUi.Ask(new SelectionPrompt<MeterDefinition>()
            .Title("Which power meter?")
            .UseConverter(d => Markup.Escape($"{d.Title}  ({d.Tests.Count} tests, {d.Tests.Sum(t => t.Points.Count)} rows; "
                + (d.BusDriver is null ? "readings via the DVM / typed" : $"read over the bus") + ")"))
            .AddChoices(definitions));
    }
}
