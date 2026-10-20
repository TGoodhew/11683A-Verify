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
              ?? throw new ArgumentException($"No meter definition for '{model}'. Available: {string.Join(", ", definitions.Select(d => d.Model))}.")
            : Choose(definitions);

        using var bench = Bench.Open(config, options, $"meter-{definition.Model}");
        var run = new MeterTestProcedure(config, options, bench, definition).Run();
        if (run.Tests.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No tests were run.[/]");
            return 1;
        }

        foreach (var line in ResultLog.Meter(run))
            bench.Log.Write(line);
        var path = run.Save(config.ResultsDirectory);
        bench.Log.Write($"Meter results saved to {Path.GetFullPath(path)}");
        try
        {
            MeterReport.WriteBeside(run, path);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]The PDF record could not be written: {Markup.Escape(ex.Message)}[/]");
        }
        bench.Log.SaveCopy(Path.ChangeExtension(path, ".log"));
        AnsiConsole.WriteLine();
        MeterReport.Summary(run, path);
        ConsoleUi.SessionLogLine(bench.Log);
        return run.Complete && run.Verdict == Verdict.Pass ? 0 : 1;
    }

    private static MeterDefinition Choose(IReadOnlyList<MeterDefinition> definitions)
    {
        if (!ConsoleUi.Interactive)
            throw new ArgumentException($"Name the meter with --meter: {string.Join(", ", definitions.Select(d => d.Model))}.");
        return AnsiConsole.Prompt(new SelectionPrompt<MeterDefinition>()
            .Title("Which power meter?")
            .UseConverter(d => Markup.Escape($"{d.Title}  ({d.Tests.Count} tests, {d.Tests.Sum(t => t.Points.Count)} rows; "
                + (d.BusDriver is null ? "readings via the DVM / typed" : $"read over the bus") + ")"))
            .AddChoices(definitions));
    }
}
