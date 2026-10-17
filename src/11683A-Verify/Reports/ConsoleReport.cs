using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.Results;
using Verify11683A.Uncertainty;

namespace Verify11683A.App.Reports;

/// <summary>
/// Results tables for the console. The same tables are the live view while a part runs (with the range being
/// measured highlighted) and the report at the end.
/// </summary>
public static class ConsoleReport
{
    public static Table Part1Table(Part1Result result, CalibratorLimits limits, UncertaintyConvention convention, string? active = null)
    {
        var k = K(convention);
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Part 1: DC voltage and ratio[/]")
            .AddColumn("Range")
            .AddColumn(new TableColumn("Nominal").RightAligned())
            .AddColumn(new TableColumn("Mean ± σ").RightAligned())
            .AddColumn(new TableColumn($"U (k={k})").RightAligned())
            .AddColumn(new TableColumn("Ratio").RightAligned())
            .AddColumn(new TableColumn("Min–Max").Centered())
            .AddColumn(new TableColumn($"U (k={k})").RightAligned())
            .AddColumn(new TableColumn("TUR").RightAligned())
            .AddColumn("Verdict");

        foreach (var limit in limits.Part1.Points)
        {
            var label = Label(limit.Range, active);
            var nominal = Si.Format(limits.NominalPart1Volts(limit.Range), "V", 5);
            var p = result.Point(limit.Range);
            if (p is null)
            {
                table.AddRow(label, $"[grey]{nominal}[/]", limit.Range == active ? "[yellow]measuring…[/]" : "[grey]—[/]", "", "",
                    limit.Range == result.ReferenceRange ? "[grey]reference[/]" : $"[grey]{Plain(limit.RatioMin, 5)}–{Plain(limit.RatioMax, 5)}[/]",
                    "", "", "");
                continue;
            }
            var mean = MeanSigma(p.Measurement, "V");
            var u = Si.Format(p.Uncertainty.Expanded, "V", 3);

            if (p.RatioAssessment is { } ra)
                table.AddRow(label, nominal, mean, u, Plain(ra.Value, 5), $"{Plain(limit.RatioMin, 5)}–{Plain(limit.RatioMax, 5)}",
                    Plain(ra.ExpandedUncertainty, 3), ConsoleUi.TurMarkup(ra), ConsoleUi.VerdictMarkup(ra.Verdict));
            else
                table.AddRow(label, nominal, mean, u,
                    limit.Range == result.ReferenceRange ? "[grey]reference[/]" : "[grey]needs 100 mW[/]", "", "", "",
                    p.VoltsAssessment is null ? ConsoleUi.VerdictMarkup(Verdict.Info) : "");

            if (p.VoltsAssessment is { } va)
                table.AddRow("", "", "", "", "[grey]voltage[/]", $"{Si.Format(va.Tolerance!.Min, "V", 5)}–{Si.Format(va.Tolerance.Max, "V", 5)}",
                    Si.Format(va.ExpandedUncertainty, "V", 3), ConsoleUi.TurMarkup(va), ConsoleUi.VerdictMarkup(va.Verdict));
        }
        return table;
    }

    public static Table Part2Table(Part2Result result, CalibratorLimits limits, UncertaintyConvention convention, string? active = null)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Part 2: four-wire resistance (STANDBY)[/]")
            .AddColumn("Range")
            .AddColumn(new TableColumn("Nominal").RightAligned())
            .AddColumn(new TableColumn("Mean ± σ").RightAligned())
            .AddColumn("DVM range")
            .AddColumn(new TableColumn("Min–Max").Centered())
            .AddColumn(new TableColumn($"U (k={K(convention)})").RightAligned())
            .AddColumn(new TableColumn("TUR").RightAligned())
            .AddColumn("Verdict");

        foreach (var limit in limits.Part2.Points)
        {
            var label = Label(limit.Range, active);
            var nominal = Si.Format(limit.OhmsMid, "Ω", 5);
            var minMax = $"{Plain(limit.OhmsMin, 5)}–{Plain(limit.OhmsMax, 5)} Ω";
            var p = result.Point(limit.Range);
            if (p is null)
            {
                table.AddRow(label, $"[grey]{nominal}[/]", limit.Range == active ? "[yellow]measuring…[/]" : "[grey]—[/]", "",
                    $"[grey]{minMax}[/]", "", "", "");
                continue;
            }
            table.AddRow(label, nominal, MeanSigma(p.Measurement, "Ω"), Si.Range(p.Measurement.DvmRange, "Ω"), minMax,
                Si.Format(p.Assessment.ExpandedUncertainty, "Ω", 3), ConsoleUi.TurMarkup(p.Assessment), ConsoleUi.VerdictMarkup(p.Verdict));
        }
        return table;
    }

    public static void Part1(Part1Result result, CalibratorLimits limits, UncertaintyConvention convention)
    {
        AnsiConsole.Write(Part1Table(result, limits, convention));
        Unsettled(result.Points.Select(p => p.Measurement));
        Notes(result.Notes);
        Overall("Part 1", result.Verdict, result.Complete);
    }

    public static void Part2(Part2Result result, CalibratorLimits limits, UncertaintyConvention convention)
    {
        AnsiConsole.Write(Part2Table(result, limits, convention));
        Unsettled(result.Points.Select(p => p.Measurement));
        var safe = result.SafetyChecks.Where(c => c.Safe).ToList();
        if (safe.Count > 0)
            AnsiConsole.MarkupLine($"[grey]Output confirmed grounded {safe.Count} time(s) before ohms current was applied "
                + $"(largest {Si.Format(safe.Max(c => Math.Abs(c.Volts)), "V", 2)}).[/]");
        var refused = result.SafetyChecks.Count - safe.Count;
        if (refused > 0)
            AnsiConsole.MarkupLine($"[yellow]{refused} time(s) the output was live and ohms were refused.[/]");
        Notes(result.Notes);
        Overall("Part 2", result.Verdict, result.Complete);
    }

    public static void Part4(Part4Result result)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Part 4: Option H01 REF EXT (indication)[/]")
            .AddColumn("Step").AddColumn(new TableColumn("DC calibrator").RightAligned()).AddColumn(new TableColumn("Expected").RightAligned())
            .AddColumn(new TableColumn("Power meter").RightAligned()).AddColumn(new TableColumn("Difference").RightAligned()).AddColumn("Limits").AddColumn("");
        if (result.Transfer is { } t)
        {
            table.AddRow("INT, 1 mW", "—", "", Si.Format(t.IntWatts, "W", 6), "", "", "");
            table.AddRow("EXT, 1 mW voltage", Si.Format(t.AppliedVolts, "V", 7), Si.Format(t.IntWatts, "W", 6), Si.Format(t.ExtWatts, "W", 6),
                Percent(t.Agreement), $"±{Percent(t.Tolerance, false)}", t.WithinTolerance ? "[green]within[/]" : "[yellow]outside[/]");
        }
        foreach (var p in result.TablePoints)
            table.AddRow("Table 2-4", Si.Format(p.AppliedVolts, "V", 6), Si.Format(p.Row.EffectiveWatts, "W", 5), Si.Format(p.ReadingWatts, "W", 6),
                Percent(p.Deviation), $"{Si.Format(p.Row.LowerWatts, "W", 5)}–{Si.Format(p.Row.UpperWatts, "W", 5)}",
                p.WithinLimits ? "[green]within[/]" : "[yellow]outside[/]");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Power meter: {Markup.Escape(result.PowerMeter)}[/]");
        Notes(result.Notes);
    }

    /// <summary>The closing summary: each part, the overall verdict, TUR flags, conditions and where the results went.</summary>
    public static void Summary(VerificationRun run, string? resultsPath)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Unit[/]", Markup.Escape($"{run.Unit.Model} s/n {run.Unit.Serial}" + (run.Unit.Options == "" ? "" : $", option {run.Unit.Options}")));
        grid.AddRow("[grey]DVM[/]", Markup.Escape($"{run.Dvm.Details.Model}" + (run.Dvm.Details.Serial == "" ? "" : $" s/n {run.Dvm.Details.Serial}")
            + (run.Dvm.Details.CalibrationDate is { } d ? $", calibrated {d:yyyy-MM-dd}" : ", calibration date not entered")));
        if (run.Dvm.CalibrationState == Specs.CalibrationState.PastDue)
            grid.AddRow("", "[red]DVM calibration past due[/]");
        grid.AddRow("[grey]Conditions[/]", Markup.Escape(Conditions(run.Conditions)));

        if (run.Part1 is { } p1)
            grid.AddRow("[grey]Part 1[/]", PartLine(p1.Verdict, p1.Complete, p1.Points.Select(p => p.Verdict),
                p1.Points.SelectMany(p => new[] { p.RatioAssessment, p.VoltsAssessment }).OfType<Assessment>()));
        else
            grid.AddRow("[grey]Part 1[/]", "[grey]not run[/]");
        if (run.Part2 is { } p2)
            grid.AddRow("[grey]Part 2[/]", PartLine(p2.Verdict, p2.Complete, p2.Points.Select(p => p.Verdict), p2.Points.Select(p => p.Assessment)));
        else
            grid.AddRow("[grey]Part 2[/]", "[grey]not run[/]");

        if (run.Part4 is { } p4)
            grid.AddRow("[grey]Part 4 (H01)[/]", (p4.AllWithin ? "[green]within[/]" : "[yellow]outside[/]")
                + (p4.Transfer is { } t ? $" [grey]EXT/INT {Markup.Escape(Percent(t.Agreement))}; indication only[/]" : " [grey]indication only[/]"));
        if (run.Part1?.SupplyAdjustmentRecommended == true)
            grid.AddRow("", "[yellow]Power Supply Adjustment (A2R1) recommended, then repeat Part 1.[/]");
        if (resultsPath is not null)
            grid.AddRow("[grey]Results[/]", Markup.Escape(Path.GetFullPath(resultsPath)));

        var overall = !run.Complete && run.Verdict != Verdict.Fail ? "[yellow bold]INCOMPLETE[/]" : ConsoleUi.VerdictMarkup(run.Verdict);
        AnsiConsole.Write(new Panel(grid).Header($" Range Switch Performance Test: {overall} ").Border(BoxBorder.Double));
    }

    public static string Conditions(TestConditions c)
    {
        var parts = new List<string>
        {
            c.AmbientC is { } a ? $"ambient {a.ToString("0.0", CultureInfo.InvariantCulture)} °C" : "ambient not entered",
            c.WarmupMinutes is { } w ? $"11683A on {w.ToString("0", CultureInfo.InvariantCulture)} min" : "11683A warm-up not recorded",
        };
        if (c.DvmWarmupMinutes is { } dw)
            parts.Add($"DVM on {dw.ToString("0", CultureInfo.InvariantCulture)} min");
        if (c.WarmupCutShort)
            parts.Add("warm-up cut short by the operator");
        if (c.SimulationScenario is { } s)
            parts.Add($"SIMULATED ({s})");
        return string.Join("; ", parts);
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

    /// <summary>The live view: the table, then a status line.</summary>
    public static IRenderable Live(Table table, string status) =>
        new Rows(table, new Markup($"[blue]{Markup.Escape(status)}[/]"));

    private static string PartLine(Verdict verdict, bool complete, IEnumerable<Verdict> points, IEnumerable<Assessment> assessments)
    {
        var list = points.ToList();
        var lowTur = assessments.Count(a => a.LowTur);
        var counts = string.Join(", ", new[]
        {
            (Verdict.Pass, "pass"), (Verdict.Fail, "fail"), (Verdict.Indeterminate, "indeterminate"),
        }.Select(x => (Count: list.Count(v => v == x.Item1), Name: x.Item2)).Where(x => x.Count > 0).Select(x => $"{x.Count} {x.Name}"));
        var head = !complete && verdict != Verdict.Fail ? "[yellow bold]INCOMPLETE[/]" : ConsoleUi.VerdictMarkup(verdict);
        return $"{head}  [grey]{Markup.Escape(counts)}{(lowTur > 0 ? $"; {lowTur} below the TUR threshold" : "")}[/]";
    }

    private static string Percent(double fraction, bool signed = true) =>
        (fraction * 100).ToString(signed ? "+0.000;-0.000" : "0.0##", CultureInfo.InvariantCulture) + "%";

    private static void Unsettled(IEnumerable<MeasuredPoint> points)
    {
        if (points.Any(p => !p.Settled))
            AnsiConsole.MarkupLine("[yellow]*[/] [grey]measured without settling; the operator chose to go on.[/]");
    }

    private static string MeanSigma(MeasuredPoint m, string unit)
    {
        var s = m.Statistics;
        return $"{Si.Format(s.Mean, unit, 7)} ± {Si.Format(s.StandardDeviation, unit, 2)}" + (m.Settled ? "" : " [yellow]*[/]");
    }

    private static string Label(string rangeId, string? active)
    {
        var label = Markup.Escape(RangeSwitch.Get(rangeId).Label);
        return rangeId == active ? $"[yellow bold]▶ {label}[/]" : label;
    }

    private static string K(UncertaintyConvention convention) => convention.CoverageFactor.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Plain(double value, int significant)
    {
        var digits = Math.Max(0, significant - 1 - (int)Math.Floor(Math.Log10(Math.Abs(value))));
        return value.ToString("F" + Math.Min(digits, 9), CultureInfo.InvariantCulture);
    }
}
