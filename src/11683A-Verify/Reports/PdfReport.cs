using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.Results;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

namespace Verify11683A.App.Reports;

/// <summary>
/// The PDF test record, built from a <see cref="VerificationRun"/> alone so it can be regenerated from a saved results
/// file. The Part 1 and Part 2 tables follow the layout of the 11683A manual's own (pp. 7–8): range, then
/// Min / Actual / Max.
/// </summary>
/// <remarks>QuestPDF is used under its Community licence (free below US $1M annual gross revenue).</remarks>
public static class PdfReport
{
    private const float Body = 8.5f;
    private static readonly string Accent = Colors.Blue.Darken3;
    private static readonly string Rule = Colors.Grey.Lighten1;

    static PdfReport() => QuestPDF.Settings.License = LicenseType.Community;

    /// <summary>Writes the report beside the results file: 11683A_&lt;serial&gt;_&lt;stamp&gt;.pdf.</summary>
    public static string WriteBeside(VerificationRun run, string resultsPath)
    {
        var path = Path.ChangeExtension(resultsPath, ".pdf");
        Write(run, path, Path.GetFileName(resultsPath));
        return path;
    }

    public static void Write(VerificationRun run, string path, string? sourceFile = null) =>
        Build(run, sourceFile).GeneratePdf(path);

    public static byte[] Generate(VerificationRun run, string? sourceFile = null) => Build(run, sourceFile).GeneratePdf();

    private static Document Build(VerificationRun run, string? sourceFile) => Document.Create(document =>
    {
        document.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(0.6f, Unit.Inch);
            page.DefaultTextStyle(t => t.FontSize(Body));

            page.Header().Element(c => Header(c, run));
            page.Content().PaddingVertical(8).Column(column =>
            {
                column.Spacing(10);
                if (run.Conditions.SimulationScenario is { } scenario)
                    column.Item().Background(Colors.Orange.Lighten4).Border(1).BorderColor(Colors.Orange.Darken2).Padding(6)
                        .Text($"SIMULATED RUN ({scenario}): no instrument was measured. Not a calibration record.").Bold().FontColor(Colors.Orange.Darken4);
                column.Item().Element(c => TitleBlock(c, run));
                column.Item().Element(c => Equipment(c, run));
                if (run.Part1 is { } p1)
                    column.Item().Element(c => Part1(c, p1, run.Settings));
                if (run.Part2 is { } p2)
                    column.Item().Element(c => Part2(c, p2, run.Settings));
                column.Item().Element(c => UncertaintyNotes(c, run));
                column.Item().Element(c => Notes(c, run));
                column.Item().Element(Signatures);
            });
            page.Footer().Row(row =>
            {
                row.RelativeItem().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken1));
                    t.Span($"{run.Company}  ·  11683A-Verify {run.AppVersion}");
                    if (sourceFile is not null)
                        t.Span($"  ·  {sourceFile}");
                });
                row.ConstantItem(60).AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken1));
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        });
    });

    private static void Header(IContainer container, VerificationRun run) =>
        container.BorderBottom(1.5f).BorderColor(Accent).PaddingBottom(4).Row(row =>
        {
            row.RelativeItem().Column(c =>
            {
                c.Item().Text(run.Company).FontSize(10).Bold().FontColor(Accent);
                c.Item().Text("Performance Verification Record: HP 11683A Range Calibrator").FontSize(13).Bold();
            });
            row.ConstantItem(150).AlignRight().AlignMiddle().Element(c => VerdictBadge(c, run.Verdict, run.Complete, 12));
        });

    /// <summary>The title block, laid out like the manual's record: model, serial, tested by, date.</summary>
    private static void TitleBlock(IContainer container, VerificationRun run) =>
        container.Border(0.75f).BorderColor(Rule).Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(80);
                c.RelativeColumn();
                c.ConstantColumn(80);
                c.RelativeColumn();
            });
            void Pair(string label, string value)
            {
                table.Cell().Background(Colors.Grey.Lighten4).Padding(4).Text(label).SemiBold();
                table.Cell().Padding(4).Text(value);
            }
            Pair("Model", "11683A Range Calibrator" + (run.Unit.Options == "" ? "" : $", option {run.Unit.Options}"));
            Pair("Serial No.", run.Unit.Serial == "" ? "—" : run.Unit.Serial);
            Pair("Tested by", run.Operator == "" ? "—" : run.Operator);
            Pair("Date", run.Started.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture));
            Pair("Procedure", "Range Switch Performance Test, 11683A Operating and Service Manual pp. 7–8");
            Pair("Conditions", ConsoleReport.Conditions(run.Conditions));
        });

    private static void Equipment(IContainer container, VerificationRun run)
    {
        var d = run.Dvm.Details;
        container.Column(c =>
        {
            Section(c, "Test equipment and traceability");
            c.Item().Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.ConstantColumn(110);
                    cols.RelativeColumn();
                });
                void Row(string label, string value, string? colour = null)
                {
                    table.Cell().PaddingVertical(1.5f).Text(label).SemiBold();
                    var text = table.Cell().PaddingVertical(1.5f).Text(value);
                    if (colour is not null)
                        text.FontColor(colour).Bold();
                }
                Row("Digital voltmeter", $"{d.Manufacturer} {d.Model}" + (d.Serial == "" ? ", serial not entered" : $", s/n {d.Serial}")
                    + (d.Firmware == "" ? "" : $", firmware {d.Firmware}") + $", at {run.Dvm.Resource}");
                Row("Calibration", d.CalibrationDate is { } date
                    ? $"{date:yyyy-MM-dd}, due {d.DueDate:yyyy-MM-dd} ({d.CalibrationIntervalMonths}-month interval)"
                    : "date not entered");
                if (run.Dvm.CalibrationState == CalibrationState.PastDue)
                    Row("", "DVM calibration was past due: these results are not traceable.", Colors.Red.Darken2);
                Row("Accuracy specification", $"{run.Settings.SpecPeriod.Key()} column. {run.SpecTable.Source}");
                Row("Spec table file", $"{run.SpecTable.File}, SHA-256 {run.SpecTable.Sha256[..16]}…");
                Row("Limits file", $"{run.Limits.File}, SHA-256 {run.Limits.Sha256[..16]}…; {run.Limits.Source}");
                Row("DVM identity", run.Dvm.IdentityReply);
                Row("Cable", "4-wire, direct to DC REFERENCE OUTPUT, Kelvin split at the BNC plug, ≤ 5 ft (manual p. 7, Table 2)");
            });
        });
    }

    private static void Part1(IContainer container, Part1Result result, RunSettings settings) => container.Column(c =>
    {
        Section(c, "Part 1: DC voltage and ratio", result.Source);
        c.Item().Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.ConstantColumn(44);           // range
                cols.RelativeColumn(1.0f);         // V min
                cols.RelativeColumn(1.2f);         // V actual
                cols.RelativeColumn(1.0f);         // V max
                cols.RelativeColumn(0.9f);         // σ (n)
                cols.RelativeColumn(0.7f);         // ratio min
                cols.RelativeColumn(0.7f);         // ratio actual
                cols.RelativeColumn(0.7f);         // ratio max
                cols.RelativeColumn(0.75f);        // U
                cols.ConstantColumn(38);           // TUR
                cols.ConstantColumn(60);           // verdict
            });
            table.Header(h =>
            {
                h.Cell().RowSpan(2).Element(HeaderCell).Text("Range");
                h.Cell().ColumnSpan(4).Element(HeaderCell).AlignCenter().Text("DVM reading");
                h.Cell().ColumnSpan(3).Element(HeaderCell).AlignCenter().Text("Ratio (V100 mW / Vrange)");
                h.Cell().RowSpan(2).Element(HeaderCell).AlignRight().Text($"U (k={K(settings)})");
                h.Cell().RowSpan(2).Element(HeaderCell).AlignRight().Text("TUR");
                h.Cell().RowSpan(2).Element(HeaderCell).Text("Verdict");
                foreach (var t in new[] { "Min.", "Actual", "Max.", "σ (n)", "Min.", "Actual", "Max." })
                    h.Cell().Element(HeaderCell).AlignRight().Text(t);
            });

            foreach (var rangeId in Ordered(result.Points.Select(p => p.RangeId).Concat(result.Missing)))
            {
                var label = RangeSwitch.Get(rangeId).Label;
                var p = result.Point(rangeId);
                if (p is null)
                {
                    table.Cell().Element(BodyCell).Text(label);
                    table.Cell().ColumnSpan(10).Element(BodyCell).Text("not measured").Italic().FontColor(Colors.Grey.Darken1);
                    continue;
                }
                var s = p.Measurement.Statistics;
                var volts = p.VoltsAssessment;
                var ratio = p.RatioAssessment;
                table.Cell().Element(BodyCell).Text(label);
                table.Cell().Element(BodyCell).AlignRight().Text(volts is null ? "" : Si.Format(volts.Tolerance!.Min, "V", 5));
                table.Cell().Element(BodyCell).AlignRight().Text(Si.Format(s.Mean, "V", 7) + (p.Measurement.Settled ? "" : " *"));
                table.Cell().Element(BodyCell).AlignRight().Text(volts is null ? "" : Si.Format(volts.Tolerance!.Max, "V", 5));
                table.Cell().Element(BodyCell).AlignRight().Text($"{Si.Format(s.StandardDeviation, "V", 2)} ({s.Count})");
                if (ratio is not null)
                {
                    table.Cell().Element(BodyCell).AlignRight().Text(Plain(ratio.Tolerance!.Min, 5));
                    table.Cell().Element(BodyCell).AlignRight().Text(Plain(ratio.Value, 5)).SemiBold();
                    table.Cell().Element(BodyCell).AlignRight().Text(Plain(ratio.Tolerance.Max, 5));
                    table.Cell().Element(BodyCell).AlignRight().Text(Plain(ratio.ExpandedUncertainty, 2));
                    table.Cell().Element(BodyCell).AlignRight().Text(Tur(ratio));
                }
                else
                {
                    table.Cell().ColumnSpan(3).Element(BodyCell).AlignCenter()
                        .Text(rangeId == result.ReferenceRange ? "1.0000 (reference)" : "no 100 mW reading").FontColor(Colors.Grey.Darken1);
                    table.Cell().Element(BodyCell).AlignRight().Text(Si.Format(p.Uncertainty.Expanded, "V", 2));
                    table.Cell().Element(BodyCell).AlignRight().Text(volts is null ? "—" : "");
                }
                table.Cell().Element(BodyCell).Element(v => VerdictBadge(v, ratio?.Verdict ?? (volts is null ? Verdict.Info : volts.Verdict), true, Body));

                if (volts is not null)
                {
                    // The 1 mW voltage is its own check.
                    table.Cell().Element(BodyCell).Text("");
                    table.Cell().ColumnSpan(4).Element(BodyCell).Text($"{label} voltage: {Si.Format(volts.Value, "V", 7)}, "
                        + $"U = {Si.Format(volts.ExpandedUncertainty, "V", 2)}").FontColor(Colors.Grey.Darken2);
                    table.Cell().ColumnSpan(3).Element(BodyCell).Text("");
                    table.Cell().Element(BodyCell).Text("");
                    table.Cell().Element(BodyCell).AlignRight().Text(Tur(volts));
                    table.Cell().Element(BodyCell).Element(v => VerdictBadge(v, volts.Verdict, true, Body));
                }
            }
        });
        PartNotes(c, result.Notes, result.Points.Any(p => !p.Measurement.Settled));
        c.Item().PaddingTop(2).Text(t =>
        {
            t.Span("Part 1: ").SemiBold();
            VerdictText(t, result.Verdict, result.Complete);
        });
    });

    private static void Part2(IContainer container, Part2Result result, RunSettings settings) => container.Column(c =>
    {
        Section(c, "Part 2: four-wire resistance, FUNCTION in STANDBY", result.Source);
        c.Item().Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.ConstantColumn(44);
                cols.RelativeColumn();
                cols.RelativeColumn(1.3f);
                cols.RelativeColumn();
                cols.RelativeColumn();
                cols.RelativeColumn(0.8f);
                cols.RelativeColumn(0.9f);
                cols.ConstantColumn(38);
                cols.ConstantColumn(60);
            });
            table.Header(h =>
            {
                h.Cell().RowSpan(2).Element(HeaderCell).Text("Range");
                h.Cell().ColumnSpan(3).Element(HeaderCell).AlignCenter().Text("DVM reading (Ω)");
                h.Cell().RowSpan(2).Element(HeaderCell).AlignRight().Text("σ (n)");
                h.Cell().RowSpan(2).Element(HeaderCell).AlignRight().Text("DVM range");
                h.Cell().RowSpan(2).Element(HeaderCell).AlignRight().Text($"U (k={K(settings)})");
                h.Cell().RowSpan(2).Element(HeaderCell).AlignRight().Text("TUR");
                h.Cell().RowSpan(2).Element(HeaderCell).Text("Verdict");
                foreach (var t in new[] { "Min.", "Actual", "Max." })
                    h.Cell().Element(HeaderCell).AlignRight().Text(t);
            });
            foreach (var rangeId in Ordered(result.Points.Select(p => p.RangeId).Concat(result.Missing)))
            {
                var label = RangeSwitch.Get(rangeId).Label;
                var p = result.Point(rangeId);
                table.Cell().Element(BodyCell).Text(label);
                if (p is null)
                {
                    table.Cell().ColumnSpan(8).Element(BodyCell).Text("not measured").Italic().FontColor(Colors.Grey.Darken1);
                    continue;
                }
                var s = p.Measurement.Statistics;
                table.Cell().Element(BodyCell).AlignRight().Text(Plain(p.Assessment.Tolerance!.Min, 5));
                table.Cell().Element(BodyCell).AlignRight().Text(Plain(s.Mean, 7) + (p.Measurement.Settled ? "" : " *")).SemiBold();
                table.Cell().Element(BodyCell).AlignRight().Text(Plain(p.Assessment.Tolerance.Max, 5));
                table.Cell().Element(BodyCell).AlignRight().Text($"{Si.Format(s.StandardDeviation, "Ω", 2)} ({s.Count})");
                table.Cell().Element(BodyCell).AlignRight().Text(Si.Range(p.Measurement.DvmRange, "Ω"));
                table.Cell().Element(BodyCell).AlignRight().Text(Si.Format(p.Assessment.ExpandedUncertainty, "Ω", 2));
                table.Cell().Element(BodyCell).AlignRight().Text(Tur(p.Assessment));
                table.Cell().Element(BodyCell).Element(v => VerdictBadge(v, p.Verdict, true, Body));
            }
        });
        var safe = result.SafetyChecks.Where(x => x.Safe).ToList();
        var notes = result.Notes.ToList();
        if (safe.Count > 0)
            notes.Insert(0, $"Before ohms current was applied, a DC reading on the 100 mV range confirmed the output was grounded "
                + $"{safe.Count} time(s); the largest was {Si.Format(safe.Max(x => Math.Abs(x.Volts)), "V", 2)} "
                + $"(limit {Si.Format(safe[0].ThresholdVolts, "V", 2)}).");
        PartNotes(c, notes, result.Points.Any(p => !p.Measurement.Settled));
        c.Item().PaddingTop(2).Text(t =>
        {
            t.Span("Part 2: ").SemiBold();
            VerdictText(t, result.Verdict, result.Complete);
        });
    });

    private static void UncertaintyNotes(IContainer container, VerificationRun run) => container.Column(c =>
    {
        var s = run.Settings;
        var u = s.Uncertainty;
        Section(c, "Measurement and uncertainty");
        foreach (var line in new[]
        {
            $"Each point: {s.ReadingsPerPoint} readings at NPLC {Plain(s.Nplc, 3)} ({Plain(s.LineFrequencyHz, 2)} Hz line) on a fixed DVM range, autozero on, after the "
                + $"readings stayed within ±{Plain(s.SettlingBandPpm, 2)} ppm (floors 2 µV, 2 mΩ) for {Plain(s.SettlingHoldSeconds, 2)} s.",
            $"The DVM's {s.SpecPeriod.Key()} accuracy specification at the reading, on the range used, is treated as rectangular: "
                + "u(B) = spec/√3. The temperature coefficient is added for each °C outside 18–28 °C.",
            $"u(A) = s/√N. u(c) = √(u(B)² + u(A)²). U = {K(s)}·u(c).",
            $"Ratios combine the two readings' relative uncertainties by {(u.RatioCombination == RatioCombination.Rss ? "root-sum-square" : "linear addition (worst case)")}.",
            $"TUR = (half the tolerance) / U; below {u.MinimumTur.ToString("0.#", CultureInfo.InvariantCulture)}:1 is flagged (marked !).",
            "PASS: the value ± U is entirely inside the limits. FAIL: entirely outside. INDETERMINATE: ± U straddles a limit.",
            "DVM input on the 0.1, 1 and 10 V ranges is >10 GΩ, meeting the 100 MΩ minimum load. The 100 mW reading (about 15.8 V) "
                + "is on the 100 V range, 10 MΩ ±1%, the same as the manual's HP 3455A.",
        })
            c.Item().PaddingLeft(6).Text("•  " + line);
    });

    private static void Notes(IContainer container, VerificationRun run)
    {
        container.Column(c =>
        {
            Section(c, "Operator notes");
            if (run.Notes.Count == 0)
                c.Item().Height(36).BorderBottom(0.5f).BorderColor(Rule);
            else
                foreach (var note in run.Notes)
                    c.Item().PaddingLeft(6).Text("•  " + note);
        });
    }

    private static void Signatures(IContainer container) =>
        container.PaddingTop(14).Row(row =>
        {
            foreach (var label in new[] { "Tested by", "Date", "Reviewed by" })
            {
                row.RelativeItem().PaddingRight(16).Column(c =>
                {
                    c.Item().Height(22).BorderBottom(0.75f).BorderColor(Colors.Black);
                    c.Item().Text(label).FontSize(7).FontColor(Colors.Grey.Darken1);
                });
            }
        });

    private static void Section(ColumnDescriptor column, string title, string? source = null)
    {
        column.Item().PaddingBottom(2).BorderBottom(0.75f).BorderColor(Rule).Row(row =>
        {
            row.RelativeItem().Text(title).FontSize(10).Bold().FontColor(Accent);
            if (source is not null)
                row.AutoItem().AlignBottom().Text(source).FontSize(7).FontColor(Colors.Grey.Darken1);
        });
    }

    private static void PartNotes(ColumnDescriptor column, IEnumerable<string> notes, bool unsettled)
    {
        foreach (var note in notes)
            column.Item().PaddingLeft(6).Text("•  " + note).FontSize(7.5f);
        if (unsettled)
            column.Item().PaddingLeft(6).Text("*  measured without settling: the operator chose to go on.").FontSize(7.5f);
    }

    private static IContainer HeaderCell(IContainer c) =>
        c.Background(Colors.Grey.Lighten3).Border(0.5f).BorderColor(Rule).PaddingVertical(2).PaddingHorizontal(3)
            .DefaultTextStyle(t => t.SemiBold().FontSize(7.5f));

    private static IContainer BodyCell(IContainer c) =>
        c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(2).PaddingHorizontal(3);

    private static void VerdictBadge(IContainer container, Verdict verdict, bool complete, float size)
    {
        var (text, colour) = !complete && verdict != Verdict.Fail
            ? ("INCOMPLETE", Colors.Orange.Darken2)
            : verdict switch
            {
                Verdict.Pass => ("PASS", Colors.Green.Darken2),
                Verdict.Fail => ("FAIL", Colors.Red.Darken2),
                Verdict.Indeterminate => ("INDETERMINATE", Colors.Orange.Darken2),
                _ => ("INFO", Colors.Blue.Darken1),
            };
        container.Text(text).FontSize(size).Bold().FontColor(colour);
    }

    private static void VerdictText(TextDescriptor t, Verdict verdict, bool complete)
    {
        var (text, colour) = !complete && verdict != Verdict.Fail
            ? ("INCOMPLETE", Colors.Orange.Darken2)
            : verdict switch
            {
                Verdict.Pass => ("PASS", Colors.Green.Darken2),
                Verdict.Fail => ("FAIL", Colors.Red.Darken2),
                Verdict.Indeterminate => ("INDETERMINATE", Colors.Orange.Darken2),
                _ => ("INFO", Colors.Blue.Darken1),
            };
        t.Span(text).Bold().FontColor(colour);
    }

    private static string Tur(Assessment a) =>
        double.IsNaN(a.Tur) ? "—" : double.IsPositiveInfinity(a.Tur) ? "∞" : a.Tur.ToString("0.0", CultureInfo.InvariantCulture) + (a.LowTur ? ":1 !" : ":1");

    /// <summary>Highest range first, as in the manual.</summary>
    private static IEnumerable<string> Ordered(IEnumerable<string> ranges) =>
        ranges.Distinct().OrderByDescending(r => RangeSwitch.Get(r).NominalWatts);

    private static string K(RunSettings s) => s.Uncertainty.CoverageFactor.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Plain(double value, int significant)
    {
        if (value == 0 || double.IsNaN(value) || double.IsInfinity(value))
            return value.ToString(CultureInfo.InvariantCulture);
        var digits = Math.Max(0, significant - 1 - (int)Math.Floor(Math.Log10(Math.Abs(value))));
        return value.ToString("F" + Math.Min(digits, 9), CultureInfo.InvariantCulture);
    }
}
