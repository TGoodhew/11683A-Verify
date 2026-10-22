using System.Text.Json;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Spectre.Console;
using Verify11683A.App.Reports;
using Verify11683A.Meters;
using Verify11683A.Results;

namespace Verify11683A.App;

/// <summary>
/// Printing the report from the console: the offer at the end of a run, the choice of printer, and printing a saved
/// results file (--print, or the menu). What is printed is the same report as the PDF beside the results, and the
/// operator is told where that PDF is, so a printout is never the only copy.
/// </summary>
public static class Printing
{
    /// <summary>The report's PDF beside <paramref name="resultsPath"/> (same name, .pdf), if it is there.</summary>
    public static string? PdfBeside(string resultsPath)
    {
        var pdf = Path.GetFullPath(Path.ChangeExtension(resultsPath, ".pdf"));
        return File.Exists(pdf) ? pdf : null;
    }

    /// <summary>" The report is saved as a PDF: …" to end a message, or "" if there is no PDF.</summary>
    private static string PdfNote(string? pdf) => pdf is null ? "" : $" The report is saved as a PDF: {pdf}";

    /// <summary>At the end of a run, at a console: offers to print the report, then asks which printer.</summary>
    public static void Offer(Func<IDocument> document, string resultsPath, SessionLog? log)
    {
        if (!ConsoleUi.Interactive || !OperatingSystem.IsWindows())
            return;
        var pdf = PdfBeside(resultsPath);
        IReadOnlyList<string> printers;
        try
        {
            printers = ReportPrinter.Printers();
        }
        catch (Exception ex)
        {
            // The print spooler is stopped, or the printer list can't be read: the PDF is still there.
            AnsiConsole.MarkupLine($"[yellow]The printers can't be listed ({Markup.Escape(ex.Message)}).{Markup.Escape(PdfNote(pdf))}[/]");
            return;
        }
        if (printers.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]No printer is installed, so the report can't be printed from here.{Markup.Escape(PdfNote(pdf))}[/]");
            return;
        }
        if (!ConsoleUi.Confirm("Print the report?", defaultValue: false))
            return;
        if (ChoosePrinter(printers) is { } printer)
            Print(document, printer, Path.GetFileNameWithoutExtension(resultsPath), log, pdf);
    }

    /// <summary>Prints and says how it went, and where the PDF is. Returns false (after saying why) if it failed.</summary>
    public static bool Print(Func<IDocument> document, string printer, string jobName, SessionLog? log, string? pdf)
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
            if (pdf is not null)
                AnsiConsole.MarkupLine($"[grey]The same report is saved as a PDF: {Markup.Escape(pdf)}[/]");
            log?.Write($"Report {jobName} printed: {pages} page(s) to '{printer}'");
            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]The report could not be printed on {Markup.Escape(printer)}: {Markup.Escape(ex.Message)}[/]");
            if (pdf is not null)
                AnsiConsole.MarkupLine($"[grey]The report is saved as a PDF: {Markup.Escape(pdf)}[/]");
            log?.Write($"Report {jobName} not printed on '{printer}': {ex}");
            return false;
        }
    }

    /// <summary>
    /// Prints the report of a saved results file: a verification run or a power meter run. With no
    /// <paramref name="printer"/>, the operator chooses at a console; otherwise the Windows default printer is used.
    /// A results file without its PDF gets one first, beside it, so the printout is not the only copy.
    /// </summary>
    public static int PrintSaved(string path, string? printer)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Printing needs Windows.");
        var document = DocumentFor(path);
        var pdf = PdfFor(path, document);
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
        return Print(document, printer, Path.GetFileNameWithoutExtension(path), null, pdf) ? 0 : 1;
    }

    /// <summary>
    /// The report's PDF beside the results file, written now (and said so) if it is not there; null (after saying why)
    /// if it can't be written.
    /// </summary>
    public static string? PdfFor(string resultsPath, Func<IDocument> document)
    {
        if (PdfBeside(resultsPath) is { } existing)
            return existing;
        var pdf = Path.GetFullPath(Path.ChangeExtension(resultsPath, ".pdf"));
        try
        {
            document().GeneratePdf(pdf);
            AnsiConsole.MarkupLine($"[grey]It had no PDF beside it; one is saved now: {Markup.Escape(pdf)}[/]");
            return pdf;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[yellow]The PDF could not be saved beside it ({Markup.Escape(ex.Message)}); printing anyway.[/]");
            return null;
        }
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
