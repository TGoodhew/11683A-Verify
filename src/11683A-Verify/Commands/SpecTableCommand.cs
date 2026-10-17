using System.Globalization;
using Spectre.Console;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.Measurement;
using Verify11683A.Specs;

namespace Verify11683A.App.Commands;

/// <summary>
/// Prints the DVM spec table exactly as loaded, so it can be checked line by line against the manual page,
/// then the spec limit at every 11683A test point on the range the test will use.
/// </summary>
public static class SpecTableCommand
{
    public static int Run(AppConfig config, CommandLine options)
    {
        var specs = DvmSpecTable.LoadForModel(config.Dvm.Model);
        var limits = CalibratorLimits.LoadDefault();
        var period = config.Dvm.Period;
        var unit = specs.Units == "%" ? "%" : "ppm";

        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(specs.Model)} spec table[/] ({unit} of reading + {unit} of range)").LeftJustified());
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(specs.Source)}[/]");

        foreach (var section in specs.Functions)
        {
            var periods = section.Ranges.SelectMany(r => r.Accuracy.Keys).Distinct()
                .OrderBy(k => SpecPeriodExtensions.Parse(k)).ToList();
            var table = new Table().Border(TableBorder.Rounded).Title(section.Function.DisplayName()).AddColumn("Range");
            foreach (var p in periods)
                table.AddColumn(new TableColumn(p).RightAligned());
            table.AddColumn(new TableColumn("Tempco /°C").RightAligned());
            table.AddColumn(section.Function == DvmFunction.DcVoltage ? "Input R" : "Test current");

            foreach (var r in section.Ranges)
            {
                var cells = new List<string> { Si.Range(r.Range, section.Function.Unit()) };
                cells.AddRange(periods.Select(p => r.Accuracy.TryGetValue(p, out var t) ? Term(t) : "—"));
                cells.Add(r.TempcoPerC is { } tc ? Term(tc) : "—");
                cells.Add(Markup.Escape((section.Function == DvmFunction.DcVoltage ? r.InputResistance : r.TestCurrent) ?? ""));
                table.AddRow(cells.ToArray());
            }
            AnsiConsole.Write(table);

            foreach (var adder in section.Adders)
                AnsiConsole.MarkupLine($"  {(adder.Applied ? "[green]applied[/]" : "[grey]not applied[/]")}  "
                    + $"{(adder.Range is { } ar ? Si.Range(ar, section.Function.Unit()) : "all ranges")}: "
                    + $"+{Term(adder.Term)}  [grey]{Markup.Escape(adder.Description)}[/]");
        }

        foreach (var note in specs.Notes)
            AnsiConsole.MarkupLine($"[grey]• {Markup.Escape(note)}[/]");
        AnsiConsole.WriteLine();

        // The spec limit at each test point, on the fixed range the test will use.
        var points = new Table().Border(TableBorder.Rounded)
            .Title($"[bold]{Markup.Escape(specs.Model)} {period.Key()} spec limit at the 11683A test points[/]"
                + (options.AmbientC is { } a ? $" (ambient {a.ToString(CultureInfo.InvariantCulture)} °C)" : ""))
            .AddColumn("Part").AddColumn("11683A range").AddColumn(new TableColumn("Nominal").RightAligned())
            .AddColumn("DVM range").AddColumn(new TableColumn("Spec limit ±").RightAligned())
            .AddColumn(new TableColumn("Relative").RightAligned()).AddColumn("Test tolerance");

        var dcvRanges = specs.RangesFor(DvmFunction.DcVoltage);
        foreach (var p in limits.Part1.Points)
        {
            var nominal = limits.NominalPart1Volts(p.Range);
            var range = RangePicker.Pick(dcvRanges, nominal);
            var limit = specs.Limit(DvmFunction.DcVoltage, range, nominal, period, options.AmbientC);
            var tolerance = p.Range == limits.Part1.ReferenceRange
                ? "reference for the ratios"
                : $"ratio {p.RatioMin.ToString(CultureInfo.InvariantCulture)}–{p.RatioMax.ToString(CultureInfo.InvariantCulture)} "
                  + $"(±{Si.Ppm((p.RatioMax - p.RatioMin) / 2 / p.RatioMid)})";
            if (p.VoltsMin is { } lo && p.VoltsMax is { } hi)
                tolerance += $"; {Si.Format(lo, "V", 5)}–{Si.Format(hi, "V", 5)}";
            points.AddRow("1", RangeSwitch.Get(p.Range).Label, Si.Format(nominal, "V", 5), Si.Range(range, "V"),
                Si.Format(limit.Total, "V", 3), Si.Ppm(limit.Relative), tolerance);
        }

        var ohmsRanges = specs.RangesFor(DvmFunction.FourWireResistance);
        foreach (var p in limits.Part2.Points)
        {
            var range = RangePicker.Pick(ohmsRanges, p.OhmsMid);
            var limit = specs.Limit(DvmFunction.FourWireResistance, range, p.OhmsMid, period, options.AmbientC);
            var tolerance = $"{Si.Format(p.OhmsMin, "Ω", 5)}–{Si.Format(p.OhmsMax, "Ω", 5)} (±{Si.Ppm((p.OhmsMax - p.OhmsMin) / 2 / p.OhmsMid)})";
            points.AddRow("2", RangeSwitch.Get(p.Range).Label, Si.Format(p.OhmsMid, "Ω", 5), Si.Range(range, "Ω"),
                Si.Format(limit.Total, "Ω", 3), Si.Ppm(limit.Relative), tolerance);
        }

        AnsiConsole.Write(points);
        AnsiConsole.MarkupLine("[grey]Nominal Part 1 voltages are derived from the 145.00 mV supply target and the mid-band ratios; "
            + "the manual gives only ratios. Uncertainty, TUR and verdicts follow in step 3.[/]");
        return 0;
    }

    private static string Term(SpecTerm t) =>
        $"{t.Reading.ToString("0.####", CultureInfo.InvariantCulture)} + {t.Range.ToString("0.####", CultureInfo.InvariantCulture)}";
}
