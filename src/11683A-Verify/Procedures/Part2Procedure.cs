using Spectre.Console;
using Spectre.Console.Rendering;
using Verify11683A.App.Reports;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Measurement;
using Verify11683A.Results;

namespace Verify11683A.App.Procedures;

/// <summary>
/// Part 2: the four-wire resistance test (11683A manual, Range Switch Performance Test, p. 8, PDF p. 19), FUNCTION in
/// STANDBY, ranges 300 µW down to 3 µW in any order. Before every detection attempt the DVM reads DC volts and
/// applies no ohms current unless the output is grounded.
/// </summary>
public static class Part2Procedure
{
    public static Part2Result Run(VerificationSession session)
    {
        var limits = session.Limits;
        var part = limits.Part2;
        var sim = session.Bench.Simulator;
        var dvm = session.Dvm;

        var screens = session.Screens;
        const string Heading = "Part 2: four-wire resistance";
        screens.Begin("Part 2", Heading);
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(part.Source)}[/]");
        AnsiConsole.Write(ConsoleUi.Settings("Set the 11683A",
            ("FUNCTION", "STANDBY"), ("DVM", "leave the 4-wire cable on DC REFERENCE OUTPUT")));
        AnsiConsole.MarkupLine("[grey]Before ohms current is applied, the DVM reads the output on its 100 mV DC range: it must be grounded, "
            + $"within {Si.Format(OhmsSafety.DefaultThresholdVolts, "V", 2)} of zero.[/]");
        session.Bench.Step("Press Enter when FUNCTION is in STANDBY", s => s.SetFunction(FunctionSwitch.Standby));

        var checks = new List<OhmsSafetyRecord>();
        var interlock = OhmsSafety.Interlock(dvm, c =>
        {
            checks.Add(c.ToRecord());
            session.Log.Write($"Ohms safety check: {c.Reading.Value} V{(c.Reading.Overload ? " (overload)" : "")}, safe={c.Safe}");
        });

        var candidates = part.Points.Select(p => new ExpectedValue(p.Range, p.OhmsMid)).ToList();
        var acquirer = session.Acquirer(new RangeDetector(DvmFunction.FourWireResistance, candidates), interlock);
        var measured = new Dictionary<string, MeasuredPoint>();
        double? lastRange = null;
        string? lastLine = null;
        Part2Result Evaluate() => Part2Evaluator.Evaluate(limits, measured.Values, checks, session.Specs, session.Period, session.AmbientC, session.Convention);

        try
        {
            while (measured.Count < part.Points.Count)
            {
                var remaining = part.Points.Select(p => p.Range).Where(r => !measured.ContainsKey(r)).ToList();
                var current = Evaluate();
                IRenderable View(string status, ExpectedValue? at) =>
                    ConsoleReport.Live(ConsoleReport.Part2Table(current, limits, session.Convention, at?.RangeId), status);
                if (screens.Clearing)
                {
                    screens.Begin("Part 2", Heading);
                    AnsiConsole.Write(ConsoleReport.Part2Table(current, limits, session.Convention));
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

                screens.Redraw();
                var point = session.MeasureNext(acquirer, lastRange, View);
                if (point is null)
                {
                    AnsiConsole.MarkupLine("[yellow]Part 2 stopped.[/]");
                    break;
                }
                lastRange = point.DvmRange;

                var label = RangeSwitch.Get(point.RangeId).Label;
                if (measured.ContainsKey(point.RangeId)
                    && !ConsoleUi.Confirm($"{label} has already been measured. Replace it with this result?"))
                    continue;
                measured[point.RangeId] = point;
                session.Log.Write($"Part 2 {point.RangeId}: mean {point.Statistics.Mean} Ω, s {point.Statistics.StandardDeviation} Ω, n {point.Statistics.Count}, "
                    + $"DVM {point.DvmRange} Ω, settled {point.Settled} after {point.SettlingSeconds:0} s, attempts {point.Attempts}");
                lastLine = $"[green]✓[/] [bold]{Markup.Escape(label)}[/]: {Si.Format(point.Statistics.Mean, "Ω", 7)}, "
                    + $"σ {Si.Format(point.Statistics.StandardDeviation, "Ω", 2)} (n = {point.Statistics.Count}) on the {Si.Range(point.DvmRange, "Ω")} range"
                    + (point.Settled ? "" : " [yellow](not settled)[/]");
                if (!screens.Clearing)
                    AnsiConsole.MarkupLine(lastLine);
            }
        }
        finally
        {
            // Leave the DVM on DC volts (10 V, or 20 V on a 2001: >10 GΩ), so no ohms current flows when FUNCTION goes back to CALIBRATE.
            try
            {
                dvm.Configure(DvmFunction.DcVoltage, RangePicker.Pick(dvm.Ranges(DvmFunction.DcVoltage), 10), 1);
                session.Log.Write("Part 2 done: DVM left on DC volts.");
            }
            catch (Exception ex)
            {
                session.Log.Write($"Could not return the DVM to DC volts: {ex.Message}");
            }
        }

        var result = Evaluate();
        foreach (var line in ResultLog.Part2(result, session.Specs.Model))
            session.Log.Write(line);
        return result;
    }
}
