using Spectre.Console;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

namespace Verify11683A.App;

/// <summary>Console prompts and markup shared by the commands.</summary>
public static class ConsoleUi
{
    public static void Prompt(string message)
    {
        AnsiConsole.Markup($"[blue]{Markup.Escape(message)}[/] ");
        if (!Console.IsInputRedirected)
            Console.ReadLine();
        else
            AnsiConsole.WriteLine();
    }

    /// <summary>Yes/no; yes when input is redirected, so scripted and simulated runs go straight through.</summary>
    public static bool Confirm(string question, bool defaultValue = true) =>
        Console.IsInputRedirected ? defaultValue : AnsiConsole.Confirm(question, defaultValue);

    /// <summary>A choice; <paramref name="whenRedirected"/> when input is redirected.</summary>
    public static string Choose(string title, IReadOnlyList<string> choices, string whenRedirected) =>
        Console.IsInputRedirected
            ? whenRedirected
            : AnsiConsole.Prompt(new SelectionPrompt<string>().Title(Markup.Escape(title)).AddChoices(choices));

    public static void SessionLogLine(SessionLog log) =>
        AnsiConsole.MarkupLine($"[grey]Session log: {Markup.Escape(Path.GetFullPath(log.Path))}[/]");

    public static string CalibrationText(DvmDetails dvm, DateOnly today) => dvm.StateOn(today) switch
    {
        CalibrationState.NotEntered => "[yellow]not set: add dvm.calibrationDate to appsettings.local.json[/]",
        CalibrationState.PastDue => $"{dvm.CalibrationDate:yyyy-MM-dd} [red](due {dvm.DueDate:yyyy-MM-dd}: PAST DUE)[/]",
        _ => $"{dvm.CalibrationDate:yyyy-MM-dd} [grey](due {dvm.DueDate:yyyy-MM-dd})[/]",
    };

    public static string VerdictMarkup(Verdict verdict) => verdict switch
    {
        Verdict.Pass => "[green bold]PASS[/]",
        Verdict.Fail => "[red bold]FAIL[/]",
        Verdict.Indeterminate => "[yellow bold]INDETERMINATE[/]",
        _ => "[blue]INFO[/]",
    };

    public static string TurMarkup(Assessment assessment) =>
        double.IsNaN(assessment.Tur) ? "—"
        : double.IsPositiveInfinity(assessment.Tur) ? "∞"
        : assessment.LowTur ? $"[yellow]{assessment.Tur:0.0}:1[/]"
        : $"{assessment.Tur:0.0}:1";
}
