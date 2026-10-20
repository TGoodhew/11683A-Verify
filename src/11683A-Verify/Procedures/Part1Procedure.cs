using Spectre.Console;
using Spectre.Console.Rendering;
using Verify11683A.App.Reports;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.Measurement;
using Verify11683A.Results;

namespace Verify11683A.App.Procedures;

/// <summary>
/// Part 1: the voltage and ratio test (11683A manual, Range Switch Performance Test, p. 7, PDF p. 18). The operator
/// sets each range from 100 mW down to 300 µW, in any order; the app detects which one it is.
/// </summary>
public static class Part1Procedure
{
    public static Part1Result Run(VerificationSession session)
    {
        var limits = session.Limits;
        var part = limits.Part1;
        var sim = session.Bench.Simulator;

        var screens = session.Screens;
        const string Heading = "Part 1: DC voltage and ratio";
        screens.Begin("Part 1", Heading);
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(part.Source)}[/]");
        AnsiConsole.Write(ConsoleUi.Settings("Set the 11683A",
            ("REF", "INT"), ("POLARITY", "NORMAL"), ("FUNCTION", "CALIBRATE"),
            ("DVM", "rear DC REFERENCE OUTPUT (the lower BNC), 4-wire direct")));
        AnsiConsole.MarkupLine("[grey]The manual starts from RANGE 100 mW with FUNCTION in STANDBY; switch to CALIBRATE now.[/]");
        session.Bench.Step("Press Enter when set", s =>
        {
            s.SetReference(ReferenceSwitch.Int);
            s.SetPolarity(PolaritySwitch.Normal);
            s.SetFunction(FunctionSwitch.Calibrate);
        });

        var candidates = part.Points.Select(p => new ExpectedValue(p.Range, limits.NominalPart1Volts(p.Range))).ToList();
        var acquirer = session.Acquirer(new RangeDetector(DvmFunction.DcVoltage, candidates));
        var measured = new Dictionary<string, MeasuredPoint>();
        double? lastRange = null;
        string? lastLine = null;
        Part1Result Evaluate() => Part1Evaluator.Evaluate(limits, measured.Values, session.Specs, session.Period, session.AmbientC, session.Convention);

        while (measured.Count < part.Points.Count)
        {
            var remaining = part.Points.Select(p => p.Range).Where(r => !measured.ContainsKey(r)).ToList();
            var current = Evaluate();
            IRenderable View(string status, ExpectedValue? at) =>
                ConsoleReport.Live(ConsoleReport.Part1Table(current, limits, session.Convention, at?.RangeId), status);
            if (screens.Clearing)
            {
                screens.Begin("Part 1", Heading);
                AnsiConsole.Write(ConsoleReport.Part1Table(current, limits, session.Convention));
                if (lastLine is not null)
                    AnsiConsole.MarkupLine(lastLine);
            }
            AnsiConsole.WriteLine();
            if (session.Bench.Walkthrough)
            {
                SimulatorKnobs.ChooseRange(sim!, remaining);
            }
            else if (sim is not null)
            {
                sim.SetRange(remaining[0]);
                AnsiConsole.MarkupLine($"[grey]Simulator: RANGE → {RangeSwitch.Get(remaining[0])}[/]");
            }
            else
            {
                ConsoleUi.Prompt($"Set RANGE to any of {string.Join(", ", remaining.Select(r => RangeSwitch.Get(r).Label))}, then press Enter");
            }

            // While measuring, the live view is the table with the range highlighted and a status line under it.
            screens.Redraw();
            var point = session.MeasureNext(acquirer, lastRange, View);
            if (point is null)
            {
                AnsiConsole.MarkupLine("[yellow]Part 1 stopped by the operator.[/]");
                break;
            }
            lastRange = point.DvmRange;

            var label = RangeSwitch.Get(point.RangeId).Label;
            if (measured.ContainsKey(point.RangeId)
                && !ConsoleUi.Confirm($"{label} has already been measured. Replace it with this result?"))
                continue;
            measured[point.RangeId] = point;
            session.Log.Write($"Part 1 {point.RangeId}: mean {point.Statistics.Mean} V, s {point.Statistics.StandardDeviation} V, n {point.Statistics.Count}, "
                + $"DVM {point.DvmRange} V, settled {point.Settled} after {point.SettlingSeconds:0} s, attempts {point.Attempts}");
            lastLine = $"[green]✓[/] [bold]{Markup.Escape(label)}[/]: {Si.Format(point.Statistics.Mean, "V", 7)}, "
                + $"σ {Si.Format(point.Statistics.StandardDeviation, "V", 2)} (n = {point.Statistics.Count}) on the {Si.Range(point.DvmRange, "V")} range"
                + (point.Settled ? "" : " [yellow](not settled)[/]");
            if (!screens.Clearing)
                AnsiConsole.MarkupLine(lastLine);
        }

        var result = Evaluate();
        foreach (var line in ResultLog.Part1(result, session.Specs.Model))
            session.Log.Write(line);
        return result;
    }
}
