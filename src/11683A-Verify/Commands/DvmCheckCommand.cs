using Spectre.Console;
using Verify11683A.App.Reports;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Instruments.Measurement;
using Verify11683A.Measurement;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

namespace Verify11683A.App.Commands;

/// <summary>
/// Step 1 bench check. Identifies the DVM, then configures every range the verification uses and reads each
/// setting back, so a wrong command, a rejected range or an input impedance that did not take shows up here
/// and not partway through a test. Finally, optionally, it reads the 11683A at 1 mW.
/// </summary>
public static class DvmCheckCommand
{
    /// <summary>The fixed ranges Parts 1 and 2 use: Part 1 needs 100 mV to 100 V, Part 2 needs 100 Ω to 10 kΩ.</summary>
    private static readonly (DvmFunction Function, double Range)[] RangesUsed =
    [
        (DvmFunction.DcVoltage, 0.1),
        (DvmFunction.DcVoltage, 1),
        (DvmFunction.DcVoltage, 10),
        (DvmFunction.DcVoltage, 100),
        (DvmFunction.FourWireResistance, 100),
        (DvmFunction.FourWireResistance, 1e3),
        (DvmFunction.FourWireResistance, 1e4),
    ];

    public static int Run(AppConfig config, CommandLine options)
    {
        using var bench = Bench.Open(config, options, "dvm-check");
        var dvm = bench.Dvm;
        var specs = DvmSpecTable.LoadForModel(dvm.Model);
        var limits = CalibratorLimits.LoadDefault();
        var failures = 0;

        AnsiConsole.Write(new Rule($"[bold]DVM check[/] — {Markup.Escape(dvm.Model)} at {Markup.Escape(dvm.ResourceName)}"
            + (bench.IsSimulation ? $" [yellow](simulated: {Markup.Escape(bench.Simulator!.Scenario.Name)})[/]" : "")).LeftJustified());

        // 1. Identity
        var identity = dvm.Identify();
        bench.Log.Write($"Identity: {identity.Raw}");
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Identity[/]", Markup.Escape(identity.Raw));
        grid.AddRow("[grey]Firmware[/]", Markup.Escape(identity.Firmware));
        grid.AddRow("[grey]Serial (config)[/]", config.Dvm.Serial == ""
            ? "[yellow]not set: add dvm.serial to appsettings.local.json[/]"
            : Markup.Escape(config.Dvm.Serial));
        var details = config.Dvm.Details(identity);
        grid.AddRow("[grey]Cal date (config)[/]", ConsoleUi.CalibrationText(details, DateOnly.FromDateTime(DateTime.Today)));
        grid.AddRow("[grey]Spec table[/]", Markup.Escape(specs.Source));
        AnsiConsole.Write(grid);
        if (identity.Serial is "" or "0")
            AnsiConsole.MarkupLine($"[grey]The {Markup.Escape(dvm.Model)} does not report its serial over the bus, so the report uses dvm.serial from config.[/]");
        AnsiConsole.WriteLine();

        // 2. Reset and an empty error queue
        dvm.Reset();
        AnsiConsole.MarkupLine("[green]✓[/] Reset; error queue empty.");

        // The cable is on the front terminals, and the Front/Rear switch cannot be set over the bus.
        bench.CheckTerminals();

        // 3. Before any ohms range is configured, make sure the 11683A output is not live.
        AnsiConsole.MarkupLine("This check configures the 4-wire ohms ranges, so the 11683A output must not be live.");
        AnsiConsole.MarkupLine("Set the 11683A [bold]FUNCTION[/] switch to [bold]STANDBY[/] (or disconnect the DVM).");
        bench.Step("Press Enter when FUNCTION is in STANDBY", s => s.SetFunction(FunctionSwitch.Standby));

        var safety = OhmsSafety.Check(dvm);
        bench.Log.Write($"Ohms safety: {safety.Reading.Value} V, safe={safety.Safe}");
        var ohmsAllowed = safety.Safe;
        AnsiConsole.MarkupLine(safety.Safe
            ? $"[green]✓[/] Output reads {Si.Format(safety.Reading.Value, "V", 3)}: {safety.Explanation}."
            : $"[red]✗[/] Output reads {(safety.Reading.Overload ? "overload" : Si.Format(safety.Reading.Value, "V", 4))}: {safety.Explanation}. [red]Ohms ranges skipped.[/]");
        AnsiConsole.WriteLine();

        // 4. Configure every range used and read it back
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Ranges used by the verification[/]")
            .AddColumn("Function").AddColumn("Range").AddColumn("Result").AddColumn("Input").AddColumn("Read back");
        foreach (var (function, range) in RangesUsed)
        {
            var rangeText = Si.Range(range, function.Unit());
            if (function == DvmFunction.FourWireResistance && !ohmsAllowed)
            {
                table.AddRow(function.DisplayName(), rangeText, "[yellow]skipped[/]", "", "output live");
                continue;
            }
            try
            {
                var applied = dvm.Configure(function, range, config.Measurement.Nplc);
                var readBack = dvm.ReadBackSettings();
                bench.Log.Write($"{function} {range}: OK; " + string.Join("; ", readBack.Select(kv => $"{kv.Key} {kv.Value}")));
                table.AddRow(function.DisplayName(), rangeText, "[green]OK[/]", Markup.Escape(applied.InputResistance),
                    Markup.Escape(string.Join("\n", readBack.Select(kv => $"{kv.Key} {kv.Value}"))));
            }
            catch (DvmException ex)
            {
                failures++;
                bench.Log.Write($"{function} {range}: FAIL: {ex.Message}");
                table.AddRow(function.DisplayName(), rangeText, "[red]FAIL[/]", "", Markup.Escape(ex.Message));
            }
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Every DCV range up to 10 V must show the >10 GΩ input: the 11683A needs at least 100 MΩ. "
            + "The 100 V range is 10 MΩ, as was the manual's 3455A.[/]");
        AnsiConsole.WriteLine();

        // 5. Optional: readings of the 11683A at 1 mW
        if (ConsoleUi.Confirm("Take readings of the 11683A at RANGE 1 mW?"))
            failures += ReadOneMilliwatt(bench, config, specs, limits, options.AmbientC);

        AnsiConsole.WriteLine();
        bench.Log.Write(failures == 0 ? "DVM check passed." : $"DVM check: {failures} problem(s).");
        AnsiConsole.MarkupLine(failures == 0
            ? "[green bold]DVM check passed.[/]"
            : $"[red bold]DVM check: {failures} problem(s).[/] See above.");
        AnsiConsole.MarkupLine($"[grey]Session log: {Markup.Escape(Path.GetFullPath(bench.Log.Path))}[/]");
        return failures == 0 ? 0 : 1;
    }

    private static int ReadOneMilliwatt(Bench bench, AppConfig config, DvmSpecTable specs, CalibratorLimits limits, double? ambientC)
    {
        var dvm = bench.Dvm;
        var point = limits.Part1.Point(limits.SupplyAdjustment.Range);
        const int count = 5;

        AnsiConsole.MarkupLine("Set the 11683A: REF switch [bold]INT[/], POLARITY [bold]NORMAL[/], RANGE [bold]1 mW[/], FUNCTION [bold]CALIBRATE[/].");
        AnsiConsole.MarkupLine("[grey]DVM on the rear DC REFERENCE OUTPUT BNC.[/]");
        bench.Step("Press Enter when set", s =>
        {
            s.SetReference(ReferenceSwitch.Int);
            s.SetPolarity(PolaritySwitch.Normal);
            s.SetRange(point.Range);
            s.SetFunction(FunctionSwitch.Calibrate);
        });

        // Only the 1 mW point is expected, so any other setting shows up as a warning.
        var detector = new RangeDetector(DvmFunction.DcVoltage, [new ExpectedValue(point.Range, limits.NominalPart1Volts(point.Range))]);
        PointAcquisition result = null!;
        AnsiConsole.Status().Start("Detecting the range", ctx =>
        {
            result = new PointAcquirer(dvm, detector, config.Measurement.Settling(DvmFunction.DcVoltage), config.Measurement.Nplc, count)
            {
                Log = m => bench.Log.Write("1 mW " + m),
                Progress = p => ctx.Status(p.Phase switch
                {
                    AcquisitionPhase.Scouting => $"Detecting the range: {Si.Format(p.Reading.Value, "V", 5)}",
                    AcquisitionPhase.Settling => $"Waiting for the output to settle: reading {p.Done}, {Si.Format(p.Reading.Value, "V", 7)}",
                    _ => $"Reading {p.Done} of {p.Total}, 1 V range, NPLC {config.Measurement.Nplc}: {Si.Format(p.Reading.Value, "V", 7)}",
                }),
            }.Acquire(allowUnsettled: true);
        });

        if (result.Outcome != AcquisitionOutcome.Measured)
        {
            bench.Log.Write($"1 mW result: not measured ({result.Outcome}): {result.Message}");
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(result.Message)}.[/] Check RANGE is 1 mW and FUNCTION is CALIBRATE.");
            return 1;
        }
        if (!result.Settled)
            AnsiConsole.MarkupLine($"[yellow]The output did not settle to ±{config.Measurement.SettlingBandPpm} ppm; the readings below may still be moving.[/]");

        var stats = result.Statistics!;
        var mean = stats.Mean;
        var sigma = stats.StandardDeviation;
        var spec = specs.Limit(DvmFunction.DcVoltage, result.Configuration!.Range, mean, config.Dvm.Period, ambientC);
        var convention = config.Uncertainty.Convention;
        var uncertainty = MeasurementUncertainty.Of(spec, stats, convention);
        var assessment = Assessment.Of(mean, uncertainty.Expanded, new Tolerance(point.VoltsMin!.Value, point.VoltsMax!.Value), convention);
        bench.Log.Write(ResultLog.OneMilliwatt(stats, uncertainty, assessment, specs.Model, limits.Part1.Source, result.Settled));

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Mean[/]", $"[bold]{Si.Format(mean, "V", 6)}[/]");
        grid.AddRow("[grey]σ (n = " + stats.Count + ")[/]", $"{Si.Format(sigma, "V", 3)} ({Si.Ppm(sigma / Math.Abs(mean))})");
        grid.AddRow($"[grey]{Markup.Escape(specs.Model)} {spec.Period.Key()} spec[/]",
            $"±{Si.Format(spec.Total, "V", 3)} ({Si.Ppm(spec.Relative)})"
            + (spec.TemperatureError > 0 ? $" incl. ±{Si.Format(spec.TemperatureError, "V", 2)} temperature" : ""));
        grid.AddRow($"[grey]U (k = {convention.CoverageFactor:0.#})[/]", $"±{Si.Format(uncertainty.Expanded, "V", 3)} ({Si.Ppm(uncertainty.Expanded / Math.Abs(mean))})");
        grid.AddRow("[grey]Manual limit[/]", $"{Si.Format(point.VoltsMin!.Value, "V", 5)} to {Si.Format(point.VoltsMax!.Value, "V", 5)}  [grey]({Markup.Escape(limits.Part1.Source)})[/]");
        grid.AddRow("[grey]TUR[/]", ConsoleUi.TurMarkup(assessment));
        grid.AddRow("[grey]Indication[/]", ConsoleUi.VerdictMarkup(assessment.Verdict) + (assessment.Verdict == Verdict.Pass
            ? " [grey](a preview: the full Part 1 test checks every range)[/]"
            : " [yellow]the Power Supply Adjustment (A2R1) would be recommended[/]"));
        AnsiConsole.Write(new Panel(grid).Header("11683A DC REFERENCE OUTPUT, RANGE 1 mW"));
        return 0;
    }
}
