using System.Text;
using Spectre.Console;
using Verify11683A.App;
using Verify11683A.App.Commands;

Console.OutputEncoding = Encoding.UTF8;

// Ctrl+C ends the process without running finally blocks: put a bus DC calibrator in Standby first, and leave the
// alternate screen so the message can be read.
Console.CancelKeyPress += (_, _) =>
{
    var failed = Verify11683A.Instruments.Visa.EmergencyStop.RunAll();
    Console.Write("\u001b[?1049l");
    Console.WriteLine();
    Console.WriteLine("Stopped (Ctrl+C). Nothing from this run was saved.");
    foreach (var f in failed)
        Console.WriteLine($"Could not do: {f}. Put it in Standby by hand.");
};

try
{
    var options = CommandLine.Parse(args);
    if (options.Help)
    {
        AnsiConsole.WriteLine(CommandLine.Usage);
        return 0;
    }

    if (options.RegeneratePdf is { } json)
    {
        var pdf = Verify11683A.App.Reports.PdfReport.WriteBeside(Verify11683A.Results.ResultsFile.Load(json), json);
        AnsiConsole.MarkupLine($"Report written: {Markup.Escape(Path.GetFullPath(pdf))}");
        return 0;
    }

    if (options.Print is { } printJson)
        return Printing.PrintSaved(printJson, options.Printer);

    if (options.ExportActuals is { } source)
    {
        var text = File.ReadAllText(source);
        var run = Verify11683A.Results.ResultsFile.Deserialize(text);
        if (Verify11683A.Results.CalibratorActuals.WhyNotExportable(run) is { } reason)
        {
            AnsiConsole.MarkupLine($"[red]Not exported: {Markup.Escape(reason)}[/]");
            return 1;
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var written = Verify11683A.Results.CalibratorActuals.From(run, Path.GetFileName(source), text).Save(directory);
        AnsiConsole.MarkupLine($"Actuals written: {Markup.Escape(written)}");
        return 0;
    }

    var config = AppConfig.Load(options.ConfigPath);
    options.ApplyTo(config);

    if (options.Command is { } command)
        return RunCommand(command, config, options);

    if (Console.IsInputRedirected)
        throw new UsageException("Name a command: the menu needs an operator at the console.");

    // The menu: run a command, then come back, until Exit.
    while (true)
    {
        var choice = ChooseFromMenu(options);
        if (choice == "exit")
            return 0;
        try
        {
            // Fresh config each time, so an edit to appsettings.local.json takes effect without restarting.
            var fresh = AppConfig.Load(options.ConfigPath);
            options.ApplyTo(fresh);
            RunCommand(choice, fresh, options);
        }
        catch (Exception ex)
        {
            ConsoleUi.Failure(ex);
        }
        AnsiConsole.WriteLine();
        ConsoleUi.Prompt("Press Enter to return to the menu");
        AnsiConsole.Clear();
    }
}
catch (UsageException ex)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
    AnsiConsole.WriteLine();
    AnsiConsole.WriteLine(CommandLine.Usage);
    return 2;
}
catch (Exception ex)
{
    // 1 is kept for a test that ran and did not pass; 3 means the run itself failed.
    ConsoleUi.Failure(ex);
    return 3;
}

static int RunCommand(string command, AppConfig config, CommandLine options) => command switch
{
    "dvm-check" => DvmCheckCommand.Run(config, options),
    "spec-table" => SpecTableCommand.Run(config, options),
    "wiring" => WiringCommand(),
    "print" => PrintCommand(config),
    "verify" => VerifyCommand.Run(config, options),
    "part1" => Part1Command.Run(config, options),
    "part2" => Part2Command.Run(config, options),
    "adjust" => AdjustCommand.Run(config, options),
    "part4" => Part4Command.Run(config, options),
    "meter-test" => MeterTestCommand.Run(config, options),
    "exit" => 0,
    _ => throw new UsageException($"Unknown command '{command}'."),
};

static int WiringCommand()
{
    Verify11683A.App.Reports.Wiring.Write();
    return 0;
}

static int PrintCommand(AppConfig config)
{
    var saved = Printing.SavedResults(config.ResultsDirectory);
    if (saved.Count == 0)
    {
        AnsiConsole.MarkupLine($"[yellow]No saved results in {Markup.Escape(Path.GetFullPath(config.ResultsDirectory))}.[/]");
        return 1;
    }
    var names = saved.Take(20).Select(Path.GetFileName).OfType<string>().ToList();
    var name = ConsoleUi.Choose("Which report? (newest first)", names, names[0]);
    return Printing.PrintSaved(saved[names.IndexOf(name)], null);
}

static string ChooseFromMenu(CommandLine options)
{
    AnsiConsole.Write(new FigletText("11683A-Verify").Color(Color.SteelBlue));
    if (options.IsSimulation)
        AnsiConsole.MarkupLine($"[yellow]Simulation: {Markup.Escape(options.SimulateScenario!)}[/]");
    if (options.IsSimulation && ConsoleUi.Interactive)
        AnsiConsole.MarkupLine("[grey]Walkthrough: the app shows every bench prompt; answering it turns the simulated 11683A's knobs.[/]");
    var choices = new Dictionary<string, string>
    {
        ["DVM check (step 1 bench check)"] = "dvm-check",
        ["Verify: Part 1 then Part 2"] = "verify",
        ["Part 1: DC voltage and ratio test"] = "part1",
        ["Part 2: four-wire resistance test"] = "part2",
        ["Part 4: Option H01 REF EXT check (power meter)"] = "part4",
        ["Power meter performance test (435A, 435B, 436A, 437B, 438A, E4418B, E4419B)"] = "meter-test",
        ["Adjust: Power Supply Adjustment (A2R1), then Part 1"] = "adjust",
        ["DVM spec table and test-point limits"] = "spec-table",
        ["Wiring diagram"] = "wiring",
        ["Print a saved report"] = "print",
        ["Exit"] = "exit",
    };
    var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("What would you like to do?").AddChoices(choices.Keys));
    return choices[choice];
}
