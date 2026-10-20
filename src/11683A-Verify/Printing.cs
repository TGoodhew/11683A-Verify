using System.Text.Json;
using QuestPDF.Infrastructure;
using Spectre.Console;
using Verify11683A.App.Reports;
using Verify11683A.Meters;
using Verify11683A.Results;

namespace Verify11683A.App;

/// <summary>
/// Printing the report from the console: the offer at the end of a run, the choice of printer, and printing a saved
/// results file (--print, or the menu). What is printed is the same report as the PDF beside the results.
/// </summary>
public static class Printing
{
    /// <summary>At the end of a run, at a console: offers to print the report, then asks which printer.</summary>
    public static void Offer(Func<IDocument> document, string jobName, SessionLog? log)
    {
        if (!ConsoleUi.Interactive || !OperatingSystem.IsWindows())
            return;
        IReadOnlyList<string> printers;
        try
        {
            printers = ReportPrinter.Printers();
        }
        catch (Exception ex)
        {
            // The print spooler is stopped, or the printer list can't be read: the PDF is still there.
            AnsiConsole.MarkupLine($"[yellow]The printers can't be listed ({Markup.Escape(ex.Message)}); the PDF is beside the results.[/]");
            return;
        }
        if (printers.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No printer is installed, so the report can't be printed from here. The PDF is beside the results.[/]");
            return;
        }
        if (!ConsoleUi.Confirm("Print the report?", defaultValue: false))
            return;
        if (ChoosePrinter(printers) is { } printer)
            Print(document, printer, jobName, log);
    }

    /// <summary>Prints and says how it went. Returns false (after saying why) if it failed.</summary>
    public static bool Print(Func<IDocument> document, string printer, string jobName, SessionLog? log)
    {
        if (!OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[red]Printing needs Windows.[/]");
            return false;
        }
        try
        {
            var pages = 0;
            AnsiConsole.Status().Start($"Printing on {Markup.Escape(printer)}",
                _ => pages = OperatingSystem.IsWindows() ? ReportPrinter.Print(document(), printer, jobName) : 0);
            AnsiConsole.MarkupLine($"[green]✓[/] Sent {pages} page{(pages == 1 ? "" : "s")} to {Markup.Escape(printer)}.");
            log?.Write($"Report {jobName} printed: {pages} page(s) to '{printer}'");
            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]The report could not be printed on {Markup.Escape(printer)}: {Markup.Escape(ex.Message)}[/]");
            log?.Write($"Report {jobName} not printed on '{printer}': {ex}");
            return false;
        }
    }

    /// <summary>
    /// Prints the report of a saved results file: a verification run or a power meter run. With no
    /// <paramref name="printer"/>, the operator chooses at a console; otherwise the Windows default printer is used.
    /// </summary>
    public static int PrintSaved(string path, string? printer)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Printing needs Windows.");
        var document = DocumentFor(path);
        if (printer is null)
        {
            var printers = ReportPrinter.Printers();
            if (ConsoleUi.Interactive && printers.Count > 0)
                printer = ChoosePrinter(printers);
            else
                printer = ReportPrinter.DefaultPrinter() ?? throw new OperatorException("There is no default printer: name one with --printer.");
            if (printer is null)
                return 0;
        }
        AnsiConsole.MarkupLine($"Report: {Markup.Escape(Path.GetFullPath(path))}");
        return Print(document, printer, Path.GetFileNameWithoutExtension(path), null) ? 0 : 1;
    }

    /// <summary>The report for a saved results file, chosen by its schema.</summary>
    public static Func<IDocument> DocumentFor(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var schema = json.RootElement.TryGetProperty("schema", out var s) ? s.GetString() : null;
        var name = Path.GetFileName(path);
        return schema switch
        {
            VerificationRun.CurrentSchema => () => PdfReport.Build(ResultsFile.Load(path), name),
            MeterRun.CurrentSchema => () => MeterReport.Build(MeterRun.Load(path), name),
            _ => throw new OperatorException($"{name} is not a results file (schema '{schema}')."),
        };
    }

    /// <summary>Saved results files with a report, newest first: verification runs and power meter runs.</summary>
    public static IReadOnlyList<string> SavedResults(string directory) =>
        !Directory.Exists(directory)
            ? []
            : Directory.EnumerateFiles(directory, "*.json")
                .Where(f => !Path.GetFileName(f).Equals(CalibratorActuals.FileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

    private const string DontPrint = "Don't print";

    /// <summary>
    /// Any installed printer, the Windows default first and marked; or null if the operator changes their mind.
    /// Redirected, the default.
    /// </summary>
    private static string? ChoosePrinter(IReadOnlyList<string> printers)
    {
        var preferred = OperatingSystem.IsWindows() ? ReportPrinter.DefaultPrinter() : null;
        var labels = printers.ToDictionary(p => p == preferred ? $"{p} (default)" : p);
        var choice = ConsoleUi.Choose("Which printer?", [.. labels.Keys, DontPrint], labels.Keys.First());
        return choice == DontPrint ? null : labels[choice];
    }
}
