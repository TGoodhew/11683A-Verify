using System.Globalization;
using Spectre.Console;
using Verify11683A.App.Reports;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.H01;
using Verify11683A.Instruments.DcSource;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Instruments.PowerMeter;
using Verify11683A.Instruments.Simulation;
using Verify11683A.Instruments.Visa;
using Verify11683A.Measurement;
using Verify11683A.Results;

namespace Verify11683A.App.Procedures;

/// <summary>
/// Part 4: the Option H01 REF EXT transfer check, following the E4418B Service Guide's Instrument Accuracy Test
/// Procedure 2 (pp. 2-11 to 2-14). Informational: the power meter's reading is compared, not the 11683A judged.
/// </summary>
/// <remarks>
/// Step 1 (REF INT, RANGE 1 mW, CALIBRATE): zero the meter in STANDBY, CAL it in CALIBRATE, read it. Step 2 (REF EXT,
/// RANGE 100 mW, line unplugged): feed REF EXT from a floating DC calibrator set to the 1 mW DC REFERENCE OUTPUT
/// voltage from Part 1, monitored by the DVM on a BNC tee, and read the meter again. Step 3, optional: the Service
/// Guide's own steps 9–14, every Table 2-4 row against its limits. The DC calibrator is set by the operator watching
/// the DVM, or by the app over the bus and trimmed against the DVM (dcSource in appsettings).
/// </remarks>
public static class Part4Procedure
{
    /// <summary>The DC calibrator should be set within this of the target before a reading is taken.</summary>
    public const double SourceBandPpm = TeeTrim.BandPpm;

    private const int PowerReadings = 5;

    public static Part4Result? Run(VerificationSession session, double? oneMilliwattVolts)
    {
        var table = H01Table.LoadDefault();
        var sim = session.Bench.Simulator;

        var screens = session.Screens;
        screens.Begin("Part 4", "Part 4: Option H01 REF EXT transfer check (informational)");
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(table.Source)}[/]");
        AnsiConsole.MarkupLine("Needs a power meter on the 11683A's front-panel POWER METER output with an 11730A cable, a DC calibrator "
            + "whose Guard and LO can float, and a BNC tee. The result is an indication under the power meter's own uncertainty.");
        if (!ConsoleUi.Confirm("Run Part 4?", defaultValue: true))
            return null;
        if (sim is null && !ConsoleUi.Interactive)
            throw new OperatorException("Part 4 on the real bench needs you at the console (input and output not redirected): "
                + "it would otherwise go on without your confirmations.");

        using var meter = ConsoleUi.Attempt("Identifying the power meter", () =>
        {
            var m = session.OpenPowerMeter();
            try
            {
                m.Identify();
                return m;
            }
            catch
            {
                m.Dispose();
                throw;
            }
        }, session.Log, canSkip: false)!;
        session.Log.Write($"Part 4 power meter: {meter.Description}");
        AnsiConsole.MarkupLine($"Power meter: [bold]{Markup.Escape(meter.Description)}[/]");
        using var bus = session.OpenDcSource();
        var source = new ExtSource(session, bus);
        AnsiConsole.MarkupLine(bus is null
            ? "DC calibrator: [bold]set by hand[/], watching the DVM"
            : $"DC calibrator: [bold]{Markup.Escape(bus.Description)}[/], set by the app and trimmed against the DVM; in Standby until needed");

        // The 1 mW DC REFERENCE OUTPUT voltage, from Part 1 or measured now.
        var v1 = oneMilliwattVolts ?? MeasureOneMilliwatt(session);
        if (v1 is null)
            return null;
        AnsiConsole.MarkupLine($"1 mW DC REFERENCE OUTPUT (REF INT): [bold]{Si.Format(v1.Value, "V", 7)}[/]");
        var notes = new List<string>();
        if (oneMilliwattVolts is null)
            notes.Add($"The 1 mW voltage ({Si.Format(v1.Value, "V", 7)}) was measured at the start of Part 4, not taken from Part 1.");

        // Step 1: REF INT.
        var context = $"[grey]Power meter {Markup.Escape(meter.Description)}; 1 mW DC REFERENCE OUTPUT {Si.Format(v1.Value, "V", 7)}[/]";
        screens.Begin("Part 4", "Part 4 · Step 1: REF INT, RANGE 1 mW");
        if (screens.Clearing)
            AnsiConsole.MarkupLine(context);
        AnsiConsole.Write(ConsoleUi.Settings("Set the 11683A",
            ("POWER METER", "power meter connected with the 11730A cable"), ("LINE", "power cord in, LINE on"),
            ("REF", "INT"), ("RANGE", "1 mW"), ("POLARITY", "NORMAL")));
        AnsiConsole.MarkupLine("Set FUNCTION to [bold]STANDBY[/]. Wait for the meter's downward drift to stop (typically under 60 s), then [bold]ZERO[/] it.");
        session.Bench.Step("Press Enter when the meter is zeroed", s =>
        {
            s.SetReference(ReferenceSwitch.Int);
            s.SetPolarity(PolaritySwitch.Normal);
            s.SetRange("1mW");
            s.SetFunction(FunctionSwitch.Standby);
            ((SimulatedPowerMeter)meter).Zero();
        });
        AnsiConsole.MarkupLine("Set FUNCTION to [bold]CALIBRATE[/], then [bold]CAL[/] the power meter.");
        session.Bench.Step("Press Enter when the meter is calibrated", s =>
        {
            s.SetFunction(FunctionSwitch.Calibrate);
            ((SimulatedPowerMeter)meter).Cal();
        });
        var intWatts = ReadPower(session, meter, "REF INT, 1 mW");

        // Step 2: REF EXT.
        screens.Begin("Part 4", $"Part 4 · Step 2: REF EXT, RANGE {RangeSwitch.Get(table.RangeSetting)}");
        if (screens.Clearing)
            AnsiConsole.MarkupLine(context + $"[grey]; REF INT reading {Si.Format(intWatts, "W", 6)}[/]");
        AnsiConsole.Write(new Panel(new Markup(
                "[bold red]WARNING[/] There is a small risk of mild electrical shock from the chassis of the 11683A option H01 "
                + "unless the power meter is connected. [grey](E4418B Service Guide p. 2-12)[/]\n"
                + "Keep the power meter connected, then [bold]unplug the 11683A's power cord[/]: the H01 ground loop stops it working "
                + "properly otherwise. The DC calibrator's [bold]Guard and LO must float[/], not be earthed."))
            .Border(BoxBorder.Heavy).BorderColor(Color.Red));
        AnsiConsole.MarkupLine($"Set RANGE [bold]{Markup.Escape(RangeSwitch.Get(table.RangeSetting).Label)}[/] and REF [bold]EXT[/]; FUNCTION stays CALIBRATE. "
            + $"[grey]({Markup.Escape(table.RangeSource)})[/]");
        AnsiConsole.MarkupLine("Move the DVM from DC REFERENCE OUTPUT to a [bold]BNC tee on REF EXT[/], and connect the "
            + $"{source.ConnectText(v1.Value)} to the tee.");
        session.Bench.Step("Press Enter when connected", s =>
        {
            s.LinePowered = false;
            s.SetRange(table.RangeSetting);
            s.SetReference(ReferenceSwitch.Ext);
            s.DvmAt = DvmPoint.RefExtTee;
        });
        var applied = source.Set(v1.Value, "the 1 mW voltage");
        var extWatts = ReadPower(session, meter, "REF EXT, 1 mW voltage");
        var transfer = new TransferCheck(intWatts, v1.Value, applied, extWatts, table.TransferTolerance);
        session.Log.Write($"Part 4 transfer: INT {intWatts} W, EXT {extWatts} W at {applied} V (target {v1}); agreement {transfer.Agreement:E3}");
        AnsiConsole.MarkupLine($"EXT/INT agreement [bold]{Percent(transfer.Agreement)}[/] (raw {Percent(transfer.RawAgreement)}), tolerance ±{Percent(transfer.Tolerance, false)}: "
            + (transfer.WithinTolerance ? "[green]within[/]" : "[yellow]outside[/]") + " [grey](indication)[/]");

        // Step 3, optional: Table 2-4.
        var points = new List<H01PointResult>();
        if (ConsoleUi.Confirm("Also step through the E4418B Table 2-4 voltages (about 10 points)?", defaultValue: sim is not null))
        {
            screens.Begin("Part 4", "Part 4 · Step 3: Table 2-4 (E4418B Service Guide steps 9–14)");
            source.Off();
            AnsiConsole.MarkupLine("FUNCTION [bold]STANDBY[/]; " + (bus is null ? "DC calibrator to 0 V, output off" : "the calibrator is in Standby at 0 V")
                + "; meter filter 512; then [bold]ZERO[/] the meter.");
            session.Bench.Step("Press Enter when the meter is zeroed", s =>
            {
                s.SetFunction(FunctionSwitch.Standby);
                s.ExternalSourceVolts = 0;
                ((SimulatedPowerMeter)meter).Zero();
            });
            AnsiConsole.MarkupLine($"FUNCTION [bold]CALIBRATE[/]; " + (bus is null ? $"DC calibrator to {Si.Format(table.CalibrationVolts, "V", 5)}, output '+'" : "the app sets the calibrator")
                + "; then [bold]CAL[/] the meter.");
            if (sim is not null)
                sim.SetFunction(FunctionSwitch.Calibrate);
            source.Set(table.CalibrationVolts, "the calibration voltage");
            session.Bench.Step("Press Enter when the meter is calibrated", s => ((SimulatedPowerMeter)meter).Cal());

            foreach (var row in table.Rows)
            {
                AnsiConsole.MarkupLine($"Meter filter [bold]{row.Filter}[/]. Expected {Si.Format(row.EffectiveWatts, "W", 5)} "
                    + $"({Si.Format(row.LowerWatts, "W", 5)} to {Si.Format(row.UpperWatts, "W", 5)}).");
                var v = source.Set(row.SettingVolts, "a Table 2-4 row");
                var reading = ReadPower(session, meter, Si.Format(row.SettingVolts, "V", 5));
                var point = new H01PointResult(row, v, reading);
                points.Add(point);
                session.Log.Write($"Part 4 table {row.SettingVolts} V: applied {v} V, reading {reading} W, within {point.WithinLimits}");
                AnsiConsole.MarkupLine($"  {Si.Format(reading, "W", 6)} ({Percent(point.Deviation)}): "
                    + (point.WithinLimits ? "[green]within the table's limits[/]" : "[yellow]outside the table's limits[/]"));
            }
        }

        // Put the unit back as it was.
        AnsiConsole.WriteLine();
        source.Off();
        AnsiConsole.MarkupLine("Done. " + (bus is null ? "Turn the DC calibrator off" : "The calibrator is in Standby (press LOCAL to use its front panel again)")
            + ", set REF to [bold]INT[/], plug the power cord back in, and move the DVM back to DC REFERENCE OUTPUT.");
        screens.Pause("Press Enter when the bench is back as it was");
        if (sim is not null)
        {
            sim.ExternalSourceVolts = 0;
            sim.SetReference(ReferenceSwitch.Int);
            sim.LinePowered = true;
            sim.DvmAt = DvmPoint.DcReferenceOutput;
        }

        notes.Add("Indication only: the readings are subject to the power meter's own uncertainty (E4418B Service Guide, Appendix B); "
            + "Table 2-4's limits do not include measurement uncertainty.");
        if (!meter.OverBus)
            notes.Add("Power meter readings were entered by the operator.");
        notes.Add(bus is null
            ? "The DC calibrator on REF EXT was set by the operator, watching the DVM on the tee."
            : $"The DC calibrator on REF EXT ({bus.Description}) was set by the app and trimmed against the DVM on the tee.");
        var result = new Part4Result(table.Source, meter.Description, meter.OverBus, transfer, points, notes);
        session.Log.Write(ResultLog.Part4(result));
        return result;
    }

    /// <summary>Measures the 1 mW DC REFERENCE OUTPUT voltage with REF INT, as Part 1 would.</summary>
    private static double? MeasureOneMilliwatt(VerificationSession session)
    {
        var limits = session.Limits;
        var sim = session.Bench.Simulator;
        AnsiConsole.MarkupLine("No Part 1 result in this session, so first the 1 mW DC REFERENCE OUTPUT voltage: DVM on DC REFERENCE OUTPUT, "
            + "REF [bold]INT[/], POLARITY [bold]NORMAL[/], RANGE [bold]1 mW[/], FUNCTION [bold]CALIBRATE[/].");
        session.Bench.Step("Press Enter when set", s =>
        {
            s.DvmAt = DvmPoint.DcReferenceOutput;
            s.SetReference(ReferenceSwitch.Int);
            s.SetPolarity(PolaritySwitch.Normal);
            s.SetRange("1mW");
            s.SetFunction(FunctionSwitch.Calibrate);
        });
        var acquirer = session.Acquirer(new RangeDetector(DvmFunction.DcVoltage, [new ExpectedValue("1mW", limits.NominalPart1Volts("1mW"))]));
        return session.MeasureNext(acquirer)?.Statistics.Mean;
    }

    /// <summary>
    /// The DC calibrator on REF EXT, watched by the DVM on the tee: set by the operator, or by the app over the bus
    /// (<paramref name="bus"/>), which also says when the leads must move between its OUTPUT and DIVIDER terminals.
    /// </summary>
    private sealed class ExtSource(VerificationSession session, IDcSource? bus)
    {
        private DcTerminals? _terminals;

        /// <summary>What to connect to the tee, for the first setting.</summary>
        public string ConnectText(double firstVolts)
        {
            if (bus is null)
                return "DC calibrator's HI/LO";
            _terminals = bus.TerminalsFor(firstVolts);
            return $"calibrator's [bold]{Terminals(_terminals.Value)} HI/LO[/] terminals";
        }

        /// <summary>Output off: the bus calibrator goes to Standby at 0 V; a hand-set one is the operator's to switch.</summary>
        public void Off() => bus?.Standby();

        /// <summary>Sets the calibrator to <paramref name="target"/> and returns the mean of five DVM readings at the tee.</summary>
        public double Set(double target, string what)
        {
            var dvm = session.Dvm;
            var sim = session.Bench.Simulator;
            var range = RangePicker.Pick(dvm.Ranges(DvmFunction.DcVoltage), target);
            dvm.Configure(DvmFunction.DcVoltage, range, 10);
            if (bus is not null)
            {
                ConsoleUi.Attempt($"Setting the calibrator to {Si.Format(target, "V", 5)}", () => SetOverBus(target, what, range), session.Log);
            }
            else
            {
                AnsiConsole.MarkupLine($"Set the DC calibrator to [bold]{Si.Format(target, "V", 5)}[/] ({Markup.Escape(what)}).");
                if (sim is not null)
                    sim.ExternalSourceVolts = target * (1 + 3e-6);   // a calibrator set a few ppm off
                if (session.Bench.Walkthrough)
                    ConsoleUi.Prompt("Press Enter when the DC calibrator is set [simulator: it lands 3 ppm high]");
                if (ConsoleUi.Interactive && sim is null)
                    WatchByHand(target);
            }

            var mean = ConsoleUi.Attempt("Reading the DVM at the REF EXT tee", () => MeanAtTee(5, range), session.Log, canSkip: false);
            session.Log.Write($"REF EXT source for {what}: target {target} V, applied {mean} V");
            // 100 ppm, but at least 1 µV: at the lowest rows the DVM's own noise is tens of ppm.
            if (Math.Abs(mean - target) > Math.Max(100e-6 * target, 1e-6))
                AnsiConsole.MarkupLine($"[yellow]The DC calibrator is {Percent(mean / target - 1)} off the target; the reading is recorded with it.[/]");
            return mean;
        }

        /// <summary>
        /// Sets the bus calibrator, then corrects it by what the DVM reads at the tee (the divider's ~500 Ω source drops
        /// a little into the REF EXT load) until it is within ±<see cref="SourceBandPpm"/> ppm, or 1 µV.
        /// </summary>
        private void SetOverBus(double target, string what, double range)
        {
            var terminals = bus!.TerminalsFor(target);
            if (_terminals is { } present && present != terminals)
            {
                bus.Standby();
                AnsiConsole.MarkupLine($"The calibrator is in Standby. Move its leads from the {Terminals(present)} terminals to its "
                    + $"[bold]{Terminals(terminals)} HI/LO[/] terminals.");
                session.Bench.Step("Press Enter when the leads are moved");
            }
            _terminals = terminals;

            AnsiConsole.MarkupLine($"Setting the calibrator to [bold]{Si.Format(target, "V", 5)}[/] ({Markup.Escape(what)}), {Terminals(terminals)} terminals.");
            var trim = TeeTrim.Run(target, bus.Output, () => MeanAtTee(3, range), step =>
                session.Log.Write($"DC calibrator set to {step.Setting.ToString("R", CultureInfo.InvariantCulture)} V; tee {step.Tee.ToString("R", CultureInfo.InvariantCulture)} V, "
                    + $"{((step.Tee / target - 1) * 1e6).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)} ppm"));
            if (trim.Gross)
            {
                bus.Standby();
                throw new InstrumentException($"The DVM reads {Si.Format(trim.Tee, "V", 6)} at the REF EXT tee with the calibrator set to "
                    + $"{Si.Format(trim.Setting, "V", 6)} on its {Terminals(terminals)} terminals; the calibrator is in Standby. Check the leads. "
                    + TeeTrim.StrapHint);
            }
        }

        /// <summary>The operator adjusts the calibrator against a live DVM reading of the tee, then presses Enter.</summary>
        private void WatchByHand(double target)
        {
            var dvm = session.Dvm;
            ConsoleUi.BeforePrompt();
            AnsiConsole.Live(new Text("")).Start(ctx =>
            {
                while (true)
                {
                    var r = dvm.Read();
                    var ppm = (r.Value / target - 1) * 1e6;
                    ctx.UpdateTarget(new Markup($"REF EXT: [bold]{Si.Format(r.Value, "V", 7)}[/]  error "
                        + $"{(Math.Abs(ppm) <= SourceBandPpm ? "[green]" : "[yellow]")}{ppm.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)} ppm[/]  "
                        + $"[blue]Adjust to within ±{SourceBandPpm:0} ppm, then press Enter.[/]"));
                    ctx.Refresh();
                    if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
                        break;
                }
            });
            ConsoleUi.Answered();
        }

        private double MeanAtTee(int count, double range)
        {
            var readings = Enumerable.Range(0, count).Select(_ => session.Dvm.Read()).Where(r => !r.Overload).Select(r => r.Value).ToList();
            if (readings.Count == 0)
                throw new InstrumentException($"The DVM overloaded at the REF EXT tee on its {range} V range.");
            return readings.Average();
        }

        private static string Terminals(DcTerminals terminals) => terminals == DcTerminals.Divider ? "DIVIDER" : "OUTPUT";
    }

    private static double ReadPower(VerificationSession session, IPowerMeter meter, string what)
    {
        double watts;
        if (meter.OverBus)
        {
            if (session.Bench.Simulator is null || session.Bench.Walkthrough)
                ConsoleUi.Prompt("Wait for the power meter reading to settle, then press Enter");
            var values = ConsoleUi.Attempt($"Reading the power meter ({what})", () =>
            {
                var read = new List<double>();
                for (var i = 0; i < PowerReadings; i++)
                {
                    read.Add(meter.ReadWatts());
                    if (session.Bench.Simulator is null)
                        Thread.Sleep(500);
                }
                return read;
            }, session.Log, canSkip: false)!;
            watts = values.Average();
            session.Log.Write($"Power meter ({what}): {string.Join(", ", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))} W");
        }
        else
        {
            watts = meter.ReadWatts();
            session.Log.Write($"Power meter ({what}), entered: {watts.ToString("R", CultureInfo.InvariantCulture)} W");
        }
        AnsiConsole.MarkupLine($"Power meter ({Markup.Escape(what)}): [bold]{Si.Format(watts, "W", 6)}[/]");
        return watts;
    }

    private static string Percent(double fraction, bool signed = true) =>
        (fraction * 100).ToString(signed ? "+0.000;-0.000;0.000" : "0.0##", CultureInfo.InvariantCulture) + "%";
}
