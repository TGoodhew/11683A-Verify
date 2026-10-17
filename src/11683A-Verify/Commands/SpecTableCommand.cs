using System.Globalization;
using Spectre.Console;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.Measurement;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

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

        // The spec limit at each test point, on the fixed range the test will use, and the TUR it allows.
        var convention = config.Uncertainty.Convention;
        var k = convention.CoverageFactor.ToString("0.#", CultureInfo.InvariantCulture);
        var points = new Table().Border(TableBorder.Rounded)
            .Title($"[bold]{Markup.Escape(specs.Model)} {period.Key()} spec limit and TUR at the 11683A test points[/]"
                + (options.AmbientC is { } a ? $" (ambient {a.ToString(CultureInfo.InvariantCulture)} °C)" : ""))
            .AddColumn("Part").AddColumn("11683A range").AddColumn(new TableColumn("Nominal").RightAligned())
            .AddColumn("DVM range").AddColumn(new TableColumn("Spec limit ±").RightAligned())
            .AddColumn(new TableColumn("Relative").RightAligned()).AddColumn("Test tolerance")
            .AddColumn(new TableColumn($"U (k = {k})").RightAligned()).AddColumn(new TableColumn("TUR").RightAligned());

        // Type B only: the readings' own scatter adds to this on the bench.
        MeasurementUncertainty SpecOnly(DvmFunction function, double nominal)
        {
            var range = RangePicker.Pick(specs.RangesFor(function), nominal);
            var limit = specs.Limit(function, range, nominal, period, options.AmbientC);
            return MeasurementUncertainty.Of(limit, ReadingStatistics.Of([nominal]), convention);
        }

        var reference = SpecOnly(DvmFunction.DcVoltage, limits.NominalPart1Volts(limits.Part1.ReferenceRange));
        foreach (var p in limits.Part1.Points)
        {
            var nominal = limits.NominalPart1Volts(p.Range);
            var u = SpecOnly(DvmFunction.DcVoltage, nominal);
            var cells = new List<string>
            {
                "1", RangeSwitch.Get(p.Range).Label, Si.Format(nominal, "V", 5), Si.Range(u.Spec.Range, "V"),
                Si.Format(u.Spec.Total, "V", 3), Si.Ppm(u.Spec.Relative),
            };
            if (p.Range == limits.Part1.ReferenceRange)
            {
                points.AddRow([.. cells, "reference for the ratios", Si.Format(u.Expanded, "V", 3), "—"]);
                continue;
            }

            var ratio = RatioUncertainty.Of(reference, u, convention);
            var ratioCheck = Assessment.Of(p.RatioMid, ratio.Expanded, new Tolerance(p.RatioMin, p.RatioMax), convention);
            points.AddRow([.. cells,
                $"ratio {Plain(p.RatioMin)}–{Plain(p.RatioMax)}",
                ratio.Expanded.ToString("0.00000", CultureInfo.InvariantCulture),
                ConsoleUi.TurMarkup(ratioCheck)]);

            if (p.VoltsMin is { } lo && p.VoltsMax is { } hi)
            {
                var voltsCheck = Assessment.Of(nominal, u.Expanded, new Tolerance(lo, hi), convention);
                points.AddRow("", "", "", "", "", "", $"{Plain(lo * 1e3)}–{Plain(hi * 1e3)} mV",
                    Si.Format(u.Expanded, "V", 3), ConsoleUi.TurMarkup(voltsCheck));
            }
        }

        foreach (var p in limits.Part2.Points)
        {
            var u = SpecOnly(DvmFunction.FourWireResistance, p.OhmsMid);
            var check = Assessment.Of(p.OhmsMid, u.Expanded, new Tolerance(p.OhmsMin, p.OhmsMax), convention);
            points.AddRow("2", RangeSwitch.Get(p.Range).Label, Si.Format(p.OhmsMid, "Ω", 5), Si.Range(u.Spec.Range, "Ω"),
                Si.Format(u.Spec.Total, "Ω", 3), Si.Ppm(u.Spec.Relative),
                $"{Plain(p.OhmsMin)}–{Plain(p.OhmsMax)} Ω",
                Si.Format(u.Expanded, "Ω", 3), ConsoleUi.TurMarkup(check));
        }

        AnsiConsole.Write(points);
        AnsiConsole.MarkupLine("[grey]Nominal Part 1 voltages are derived from the 145.00 mV supply target and the mid-band ratios; "
            + "the manual gives only ratios.[/]");
        AnsiConsole.MarkupLine($"[grey]U is the spec alone: u = spec/√3, U = {k}u; ratios combine {convention.RatioCombination}. "
            + $"The readings' scatter (s/√N) adds to it on the bench. TUR = half the tolerance / U; below {convention.MinimumTur:0.#}:1 is flagged.[/]");
        return 0;
    }

    /// <summary>A limit as the manual prints it: 3143.3, 3.3457, 143.00.</summary>
    private static string Plain(double value) => value.ToString(value >= 100 ? "0.00###" : "0.000##", CultureInfo.InvariantCulture);

    private static string Term(SpecTerm t) =>
        $"{t.Reading.ToString("0.####", CultureInfo.InvariantCulture)} + {t.Range.ToString("0.####", CultureInfo.InvariantCulture)}";
}
