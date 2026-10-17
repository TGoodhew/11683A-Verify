using System.Text;
using Spectre.Console;
using Verify11683A.App;
using Verify11683A.App.Commands;

Console.OutputEncoding = Encoding.UTF8;

try
{
    var options = CommandLine.Parse(args);
    if (options.Help)
    {
        AnsiConsole.WriteLine(CommandLine.Usage);
        return 0;
    }

    var config = AppConfig.Load(options.ConfigPath);
    options.ApplyTo(config);

    var command = options.Command ?? ChooseFromMenu(options);
    return command switch
    {
        "dvm-check" => DvmCheckCommand.Run(config, options),
        "spec-table" => SpecTableCommand.Run(config, options),
        "wiring" => WiringCommand(),
        "verify" => VerifyCommand.Run(config, options),
        "part1" => Part1Command.Run(config, options),
        "part2" => Part2Command.Run(config, options),
        "exit" => 0,
        _ => throw new ArgumentException($"Unknown command '{command}'."),
    };
}
catch (ArgumentException ex)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
    AnsiConsole.WriteLine();
    AnsiConsole.WriteLine(CommandLine.Usage);
    return 2;
}
catch (Exception ex)
{
    AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
    return 1;
}

static int WiringCommand()
{
    Verify11683A.App.Reports.Wiring.Write();
    return 0;
}

static string ChooseFromMenu(CommandLine options)
{
    AnsiConsole.Write(new FigletText("11683A-Verify").Color(Color.SteelBlue));
    if (options.IsSimulation)
        AnsiConsole.MarkupLine($"[yellow]Simulation: {Markup.Escape(options.SimulateScenario!)}[/]");
    var choices = new Dictionary<string, string>
    {
        ["DVM check (step 1 bench check)"] = "dvm-check",
        ["Verify: Part 1 then Part 2"] = "verify",
        ["Part 1: DC voltage and ratio test"] = "part1",
        ["Part 2: four-wire resistance test"] = "part2",
        ["DVM spec table and test-point limits"] = "spec-table",
        ["Wiring diagram"] = "wiring",
        ["Exit"] = "exit",
    };
    var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("What would you like to do?").AddChoices(choices.Keys));
    return choices[choice];
}
