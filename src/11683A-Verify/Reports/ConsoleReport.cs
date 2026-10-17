using System.Globalization;
using Spectre.Console;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.Results;
using Verify11683A.Uncertainty;

namespace Verify11683A.App.Reports;

/// <summary>Results tables for the console.</summary>
public static class ConsoleReport
{
    public static void Part1(Part1Result result, CalibratorLimits limits, UncertaintyConvention convention)
    {
        var k = convention.CoverageFactor.ToString("0.#", CultureInfo.InvariantCulture);
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Part 1: DC voltage and ratio[/]")
            .AddColumn("Range")
            .AddColumn(new TableColumn("Mean ± σ").RightAligned())
            .AddColumn(new TableColumn($"U (k = {k})").RightAligned())
            .AddColumn(new TableColumn("Ratio").RightAligned())
            .AddColumn(new TableColumn("Limits").Centered())
            .AddColumn(new TableColumn($"U (k = {k})").RightAligned())
            .AddColumn(new TableColumn("TUR").RightAligned())
            .AddColumn("Verdict");

        foreach (var limit in limits.Part1.Points)
        {
            var label = RangeSwitch.Get(limit.Range).Label;
            var p = result.Point(limit.Range);
            if (p is null)
            {
                table.AddRow(label, "[grey]not measured[/]", "", "", "", "", "", "");
                continue;
            }
            var s = p.Measurement.Statistics;
            var mean = $"{Si.Format(s.Mean, "V", 7)} ± {Si.Format(s.StandardDeviation, "V", 2)}"
                + (p.Measurement.Settled ? "" : " [yellow]*[/]");
            var u = Si.Format(p.Uncertainty.Expanded, "V", 3);

            if (p.RatioAssessment is { } ra)
                table.AddRow(label, mean, u, Plain(ra.Value, 5), $"{Plain(limit.RatioMin, 5)}–{Plain(limit.RatioMax, 5)}",
                    Plain(ra.ExpandedUncertainty, 3), ConsoleUi.TurMarkup(ra), ConsoleUi.VerdictMarkup(ra.Verdict));
            else
                table.AddRow(label, mean, u, limit.Range == result.ReferenceRange ? "[grey]reference[/]" : "[grey]—[/]", "", "", "",
                    p.VoltsAssessment is null ? ConsoleUi.VerdictMarkup(Verdict.Info) : "");

            if (p.VoltsAssessment is { } va)
                table.AddRow("", "", "", "[grey]voltage[/]", $"{Si.Format(va.Tolerance!.Min, "V", 5)}–{Si.Format(va.Tolerance.Max, "V", 5)}",
                    Si.Format(va.ExpandedUncertainty, "V", 3), ConsoleUi.TurMarkup(va), ConsoleUi.VerdictMarkup(va.Verdict));
        }
        AnsiConsole.Write(table);
        if (result.Points.Any(p => !p.Measurement.Settled))
            AnsiConsole.MarkupLine("[yellow]*[/] [grey]measured without settling; the operator chose to go on.[/]");
        Notes(result.Notes);
        Overall("Part 1", result.Verdict, result.Complete);
    }

    public static void Notes(IEnumerable<string> notes)
    {
        foreach (var note in notes)
            AnsiConsole.MarkupLine($"[grey]•[/] {Markup.Escape(note)}");
    }

    public static void Overall(string part, Verdict verdict, bool complete)
    {
        var text = !complete && verdict != Verdict.Fail
            ? "[yellow bold]INCOMPLETE[/]"
            : ConsoleUi.VerdictMarkup(verdict);
        AnsiConsole.MarkupLine($"{Markup.Escape(part)}: {text}");
    }

    private static string Plain(double value, int significant)
    {
        var digits = Math.Max(0, significant - 1 - (int)Math.Floor(Math.Log10(Math.Abs(value))));
        return value.ToString("F" + Math.Min(digits, 9), CultureInfo.InvariantCulture);
    }
}
