using Spectre.Console;
using Verify11683A.Instruments.Visa;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

namespace Verify11683A.App;

/// <summary>The operator chose to stop after an instrument error; what was measured so far is kept.</summary>
public sealed class OperatorStopException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Something the operator can put right (a setting, a printer, a console to answer prompts), shown as a message.</summary>
public sealed class OperatorException(string message) : Exception(message);

/// <summary>A command line the app can't run; the usage text follows the message.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>Console prompts and markup shared by the commands.</summary>
public static class ConsoleUi
{
    /// <summary>A person at a real console: live views and screen clears are used only then.</summary>
    public static bool Interactive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static void Prompt(string message)
    {
        DiscardTypeAhead();
        AnsiConsole.Markup($"[blue]{Markup.Escape(message)}[/] ");
        if (!Console.IsInputRedirected)
            Console.ReadLine();
        else
            AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Drops keys pressed while the app was busy, such as Enter pressed again during a long reading, so they can't answer
    /// the next prompt before the operator has done what it asks. Every prompt calls it first.
    /// </summary>
    public static void DiscardTypeAhead()
    {
        if (Console.IsInputRedirected)
            return;
        while (Console.KeyAvailable)
            Console.ReadKey(intercept: true);
    }

    /// <summary>Yes/no; yes when input is redirected, so scripted and simulated runs go straight through.</summary>
    public static bool Confirm(string question, bool defaultValue = true)
    {
        if (Console.IsInputRedirected)
            return defaultValue;
        DiscardTypeAhead();
        return AnsiConsole.Confirm(question, defaultValue);
    }

    /// <summary>
    /// Runs <paramref name="work"/> under a spinner saying what the app is doing, so the operator can see a step was
    /// taken and need not press anything; the status can be updated as it goes. Without a console it just runs.
    /// </summary>
    public static T Busy<T>(string doing, Func<Action<string>, T> work)
    {
        if (!Interactive)
            return work(_ => { });
        var started = DateTime.Now;
        string Text(string what) => $"{Markup.Escape(what)} [grey]({(DateTime.Now - started).TotalSeconds:0} s — working, no need to press anything)[/]";
        return AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start(Text(doing), ctx =>
        {
            // The seconds keep counting through a long instrument read, so it is plainly still going.
            var current = doing;
            using var tick = new Timer(_ => ctx.Status(Text(current)), null, 1000, 1000);
            return work(what =>
            {
                current = what;
                ctx.Status(Text(what));
            });
        });
    }

    /// <summary>A choice; <paramref name="whenRedirected"/> when input is redirected.</summary>
    public static string Choose(string title, IReadOnlyList<string> choices, string whenRedirected)
    {
        if (Console.IsInputRedirected)
            return whenRedirected;
        DiscardTypeAhead();
        return AnsiConsole.Prompt(new SelectionPrompt<string>().Title(Markup.Escape(title)).AddChoices(choices));
    }

    /// <summary>What to set before a step, as a panel: one knob or connection per row, so it can be checked at a glance.</summary>
    public static Panel Settings(string header, params (string Name, string Value)[] rows)
    {
        var grid = new Grid().AddColumn(new GridColumn().PadRight(3)).AddColumn();
        foreach (var (name, value) in rows)
            grid.AddRow($"[grey]{Markup.Escape(name)}[/]", $"[bold]{Markup.Escape(value)}[/]");
        return new Panel(grid).Header($" {Markup.Escape(header)} ").Border(BoxBorder.Rounded).BorderColor(Color.Blue);
    }

    public static void SessionLogLine(SessionLog log) =>
        AnsiConsole.MarkupLine($"[grey]Session log: {Markup.Escape(Path.GetFullPath(log.Path))}[/]");

    /// <summary>
    /// An error that ended a command. An instrument's or the operator's is shown as a message (the session log has
    /// the detail); anything else is a bug, so its stack trace is shown too.
    /// </summary>
    public static void Failure(Exception ex)
    {
        switch (ex)
        {
            case OperatorStopException:
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(ex.Message)}[/]");
                break;
            // Conditions of the bench, the files or the settings. The session log gets the stack trace regardless.
            case InstrumentException or OperatorException or IOException or UnauthorizedAccessException or InvalidDataException
                or System.Text.Json.JsonException or ArgumentException or NotSupportedException:
                AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
                break;
            default:
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
                break;
        }
        if (SessionLog.Latest is not { } log)
            return;
        try
        {
            // The session has closed its log by now; the error still belongs at the end of it.
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  ERROR {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        AnsiConsole.MarkupLine($"[grey]Session log: {Markup.Escape(Path.GetFullPath(log))}[/]");
    }

    /// <summary>
    /// Runs an instrument step; if it fails, says why and lets the operator retry it, skip it (when
    /// <paramref name="canSkip"/>; the result is then default) or stop. Without an operator the error stands.
    /// </summary>
    /// <exception cref="OperatorStopException">The operator chose to stop.</exception>
    public static T? Attempt<T>(string what, Func<T> step, SessionLog log, bool canSkip = true)
    {
        while (true)
        {
            try
            {
                return step();
            }
            catch (InstrumentException ex)
            {
                log.Write($"{what} failed: {ex.Message}");
                if (!Interactive)
                    throw;
                AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(what)}: {Markup.Escape(ex.Message)}[/]");
                var choices = canSkip ? new[] { "Retry", "Skip", "Stop" } : ["Retry", "Stop"];
                DiscardTypeAhead();
                var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("What now?").AddChoices(choices));
                log.Write($"Operator chose {choice}");
                if (choice == "Skip")
                    return default;
                if (choice == "Stop")
                    throw new OperatorStopException($"Stopped at: {what}.", ex);
            }
        }
    }

    public static void Attempt(string what, Action step, SessionLog log, bool canSkip = false) =>
        Attempt<object?>(what, () =>
        {
            step();
            return null;
        }, log, canSkip);

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
