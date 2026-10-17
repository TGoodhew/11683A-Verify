using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Spectre.Console;
using Verify11683A.App.Procedures;
using Verify11683A.Meters;
using Verify11683A.Uncertainty;
using PdfColors = QuestPDF.Helpers.Colors;

namespace Verify11683A.App.Reports;

/// <summary>Console tables and the PDF record for a power meter performance test, laid out like the manuals' test records.</summary>
public static class MeterReport
{
    static MeterReport() => QuestPDF.Settings.License = LicenseType.Community;

    public static Table Table(MeterTestResult result)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .Title($"[bold]{Markup.Escape(result.Title)}[/]" + (result.Channel is "" or "A" ? "" : $" — channel {Markup.Escape(result.Channel)}"))
            .AddColumn("Row").AddColumn("11683A").AddColumn(new TableColumn("Min").RightAligned())
            .AddColumn(new TableColumn("Actual").RightAligned()).AddColumn(new TableColumn("Max").RightAligned()).AddColumn("Unit").AddColumn("");
        foreach (var p in result.Points)
        {
            var point = p.Point;
            table.AddRow(
                Markup.Escape(point.Label),
                Markup.Escape(point.Calibrator is { } c ? Calibrator(c) : "—"),
                MeterTestProcedure.Fmt(point.Min, point),
                p.Reading is { } r ? $"[bold]{MeterTestProcedure.Fmt(r, point)}[/]" + (p.Simulated ? " [grey]s[/]" : "") : "[yellow]—[/]",
                MeterTestProcedure.Fmt(point.Max, point),
                Markup.Escape(MeterUnits.Display(point.Unit)),
                p.Pass switch { true => "[green]PASS[/]", false => "[red]FAIL[/]", _ => "[yellow]skipped[/]" });
        }
        return table;
    }

    public static void Summary(MeterRun run, string? path)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Meter[/]", Markup.Escape($"{run.Meter.Title}" + (run.Meter.Serial == "" ? "" : $" s/n {run.Meter.Serial}") + $" ({run.Meter.Driver})"));
        grid.AddRow("[grey]Calibrator[/]", Markup.Escape($"{run.Calibrator.Model} s/n {run.Calibrator.Serial}"));
        foreach (var t in run.Tests)
            grid.AddRow($"[grey]{Markup.Escape(t.Title)}{(t.Channel is "" or "A" ? "" : $" ({Markup.Escape(t.Channel)})")}[/]",
                (!t.Complete && t.Verdict != Verdict.Fail ? "[yellow bold]INCOMPLETE[/]" : ConsoleUi.VerdictMarkup(t.Verdict))
                + $" [grey]{t.Points.Count(p => p.Pass == true)}/{t.Points.Count} rows within limits[/]");
        if (path is not null)
            grid.AddRow("[grey]Results[/]", Markup.Escape(Path.GetFullPath(path)));
        var overall = !run.Complete && run.Verdict != Verdict.Fail ? "[yellow bold]INCOMPLETE[/]" : ConsoleUi.VerdictMarkup(run.Verdict);
        AnsiConsole.Write(new Panel(grid).Header($" {Markup.Escape(run.Meter.Model)} performance test: {overall} ").Border(BoxBorder.Double));
    }

    public static string WriteBeside(MeterRun run, string resultsPath)
    {
        var path = Path.ChangeExtension(resultsPath, ".pdf");
        Document(run, Path.GetFileName(resultsPath)).GeneratePdf(path);
        return path;
    }

    public static byte[] Generate(MeterRun run) => Document(run, null).GeneratePdf();

    private static Document Document(MeterRun run, string? sourceFile) => QuestPDF.Fluent.Document.Create(doc => doc.Page(page =>
    {
        page.Size(PageSizes.Letter);
        page.Margin(0.6f, Unit.Inch);
        page.DefaultTextStyle(t => t.FontSize(8.5f));
        page.Header().BorderBottom(1.5f).BorderColor(PdfColors.Blue.Darken3).PaddingBottom(4).Row(row =>
        {
            row.RelativeItem().Column(c =>
            {
                c.Item().Text(run.Company).FontSize(10).Bold().FontColor(PdfColors.Blue.Darken3);
                c.Item().Text($"Performance Test Record: {run.Meter.Title}").FontSize(13).Bold();
            });
            row.ConstantItem(120).AlignRight().AlignMiddle().Text(VerdictText(run.Verdict, run.Complete)).FontSize(12).Bold()
                .FontColor(VerdictColour(run.Verdict, run.Complete));
        });
        page.Content().PaddingVertical(8).Column(col =>
        {
            col.Spacing(9);
            if (run.SimulationScenario is { } s)
                col.Item().Background(PdfColors.Orange.Lighten4).Border(1).BorderColor(PdfColors.Orange.Darken2).Padding(6)
                    .Text($"SIMULATED RUN ({s}): no instrument was measured. Not a calibration record.").Bold().FontColor(PdfColors.Orange.Darken4);

            col.Item().Border(0.75f).BorderColor(PdfColors.Grey.Lighten1).Table(t =>
            {
                t.ColumnsDefinition(c => { c.ConstantColumn(85); c.RelativeColumn(); c.ConstantColumn(70); c.RelativeColumn(); });
                void Pair(string l, string v)
                {
                    t.Cell().Background(PdfColors.Grey.Lighten4).Padding(4).Text(l).SemiBold();
                    t.Cell().Padding(4).Text(v);
                }
                Pair("Model", run.Meter.Title);
                Pair("Serial No.", run.Meter.Serial == "" ? "—" : run.Meter.Serial);
                Pair("Tested by", run.Operator == "" ? "—" : run.Operator);
                Pair("Date", run.Started.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture));
                Pair("Range calibrator", $"{run.Calibrator.Model} s/n {run.Calibrator.Serial}" + (run.Calibrator.Options == "" ? "" : $", option {run.Calibrator.Options}"));
                Pair("Readings", run.Meter.Driver);
            });

            col.Item().Text(t =>
            {
                t.Span("Test definition: ").SemiBold();
                t.Span($"{run.Meter.Definition.File} (SHA-256 {run.Meter.Definition.Sha256[..16]}…), from {run.Meter.Definition.Source}.");
                if (run.Meter.Identity != "")
                    t.Span($" Meter identity: {run.Meter.Identity}.");
                if (run.Dvm is { } d)
                    t.Span($" DVM: {d.Details.Manufacturer} {d.Details.Model}" + (d.Details.Serial == "" ? "" : $" s/n {d.Details.Serial}")
                        + (d.Details.CalibrationDate is { } cal ? $", calibrated {cal:yyyy-MM-dd}" : "") + ".");
            });

            foreach (var test in run.Tests)
                col.Item().Element(c => TestTable(c, test));

            if (run.NotRun.Count > 0)
                col.Item().Column(c =>
                {
                    c.Item().Text("Performance tests not run here").FontSize(10).Bold().FontColor(PdfColors.Blue.Darken3);
                    foreach (var t in run.NotRun)
                        c.Item().PaddingLeft(6).Text($"•  {t.Title} ({t.Source}): needs {string.Join(", ", t.Equipment)}.").FontSize(7.5f);
                });
            foreach (var note in run.Notes)
                col.Item().Text(note).FontSize(7).FontColor(PdfColors.Grey.Darken2);

            col.Item().PaddingTop(14).Row(row =>
            {
                foreach (var label in new[] { "Tested by", "Date", "Reviewed by" })
                    row.RelativeItem().PaddingRight(16).Column(c =>
                    {
                        c.Item().Height(22).BorderBottom(0.75f).BorderColor(PdfColors.Black);
                        c.Item().Text(label).FontSize(7).FontColor(PdfColors.Grey.Darken1);
                    });
            });
        });
        page.Footer().Row(row =>
        {
            row.RelativeItem().Text($"{run.Company}  ·  11683A-Verify {run.AppVersion}" + (sourceFile is null ? "" : $"  ·  {sourceFile}"))
                .FontSize(7).FontColor(PdfColors.Grey.Darken1);
            row.ConstantItem(50).AlignRight().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(7).FontColor(PdfColors.Grey.Darken1));
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
            });
        });
    }));

    private static void TestTable(IContainer container, MeterTestResult test) => container.Column(c =>
    {
        c.Item().BorderBottom(0.75f).BorderColor(PdfColors.Grey.Lighten1).PaddingBottom(2)
            .Text(test.Title + (test.Channel is "" or "A" ? "" : $", channel {test.Channel}")).FontSize(10).Bold().FontColor(PdfColors.Blue.Darken3);
        c.Item().Text(test.Source).FontSize(7).FontColor(PdfColors.Grey.Darken1);
        c.Item().Table(t =>
        {
            t.ColumnsDefinition(cols =>
            {
                cols.RelativeColumn(1.2f); cols.RelativeColumn(2.2f); cols.RelativeColumn(1.6f);
                cols.RelativeColumn(); cols.RelativeColumn(); cols.RelativeColumn(); cols.ConstantColumn(34); cols.ConstantColumn(44);
            });
            t.Header(h =>
            {
                foreach (var title in new[] { "Row", "11683A", "Meter", "Min.", "Actual", "Max.", "Unit", "" })
                    h.Cell().Background(PdfColors.Grey.Lighten3).Border(0.5f).BorderColor(PdfColors.Grey.Lighten1).Padding(2).Text(title).SemiBold().FontSize(7.5f);
            });
            foreach (var p in test.Points)
            {
                var point = p.Point;
                IContainer Cell(IContainer x) => x.BorderBottom(0.5f).BorderColor(PdfColors.Grey.Lighten2).PaddingVertical(2).PaddingHorizontal(3);
                t.Cell().Element(Cell).Text(point.Label);
                t.Cell().Element(Cell).Text(point.Calibrator is { } cal ? Calibrator(cal) : "not used").FontSize(7.5f);
                t.Cell().Element(Cell).Text(point.MeterSetting).FontSize(7.5f);
                t.Cell().Element(Cell).AlignRight().Text(MeterTestProcedure.Fmt(point.Min, point));
                t.Cell().Element(Cell).AlignRight().Text(p.Reading is { } r ? MeterTestProcedure.Fmt(r, point) + (p.Simulated ? " s" : "") : "—").SemiBold();
                t.Cell().Element(Cell).AlignRight().Text(MeterTestProcedure.Fmt(point.Max, point));
                t.Cell().Element(Cell).Text(MeterUnits.Display(point.Unit));
                t.Cell().Element(Cell).Text(p.Pass switch { true => "PASS", false => "FAIL", _ => "—" }).Bold()
                    .FontColor(p.Pass switch { true => PdfColors.Green.Darken2, false => PdfColors.Red.Darken2, _ => PdfColors.Grey.Darken1 });
            }
        });
        if (test.Points.Any(p => p.Simulated))
            c.Item().Text("s  typed by the simulated operator (the simulator cannot read a display).").FontSize(7).FontColor(PdfColors.Grey.Darken1);
        c.Item().Text(t =>
        {
            t.Span("Result: ").SemiBold();
            t.Span(VerdictText(test.Verdict, test.Complete)).Bold().FontColor(VerdictColour(test.Verdict, test.Complete));
        });
    });

    private static string Calibrator(CalibratorSetting c) =>
        c.Reference == Verify11683A.Calibrator.ReferenceSwitch.Ext
            ? $"REF EXT {(c.ExternalVolts ?? 0).ToString("G5", CultureInfo.InvariantCulture)} V"
            : $"{Verify11683A.Calibrator.RangeSwitch.Get(c.Range).Label}, {c.Function.ToString().ToUpperInvariant()}"
              + (c.Polarity == Verify11683A.Calibrator.PolaritySwitch.Reverse ? ", REVERSE" : "");

    private static string VerdictText(Verdict v, bool complete) =>
        !complete && v != Verdict.Fail ? "INCOMPLETE" : v switch { Verdict.Pass => "PASS", Verdict.Fail => "FAIL", _ => "INFO" };

    private static string VerdictColour(Verdict v, bool complete) =>
        !complete && v != Verdict.Fail ? PdfColors.Orange.Darken2 : v switch
        {
            Verdict.Pass => PdfColors.Green.Darken2,
            Verdict.Fail => PdfColors.Red.Darken2,
            _ => PdfColors.Blue.Darken1,
        };
}
