using System.Globalization;
using Spectre.Console;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.H01;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Instruments.PowerMeter;
using Verify11683A.Instruments.Simulation;
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
/// Guide's own steps 9–14, every Table 2-4 row against its limits.
/// </remarks>
public static class Part4Procedure
{
    /// <summary>The DC calibrator should be set within this of the target before a reading is taken.</summary>
    public const double SourceBandPpm = 20;

    private const int PowerReadings = 5;

    public static Part4Result? Run(VerificationSession session, double? oneMilliwattVolts)
    {
        var table = H01Table.LoadDefault();
        var sim = session.Bench.Simulator;

        AnsiConsole.Write(new Rule("[bold]Part 4: Option H01 REF EXT transfer check (informational)[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(table.Source)}[/]");
        AnsiConsole.MarkupLine("Needs a power meter on the 11683A's front-panel POWER METER output with an 11730A cable, a DC calibrator "
            + "whose Guard and LO can float, and a BNC tee. The result is an indication under the power meter's own uncertainty.");
        if (!ConsoleUi.Confirm("Run Part 4?", defaultValue: true))
            return null;

        using var meter = session.OpenPowerMeter();
        meter.Identify();
        session.Log.Write($"Part 4 power meter: {meter.Description}");
        AnsiConsole.MarkupLine($"Power meter: [bold]{Markup.Escape(meter.Description)}[/]");

        // The 1 mW DC REFERENCE OUTPUT voltage, from Part 1 or measured now.
        var v1 = oneMilliwattVolts ?? MeasureOneMilliwatt(session);
        if (v1 is null)
            return null;
        AnsiConsole.MarkupLine($"1 mW DC REFERENCE OUTPUT (REF INT): [bold]{Si.Format(v1.Value, "V", 7)}[/]");
        var notes = new List<string>();
        if (oneMilliwattVolts is null)
            notes.Add($"The 1 mW voltage ({Si.Format(v1.Value, "V", 7)}) was measured at the start of Part 4, not taken from Part 1.");

        // Step 1: REF INT.
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("Step 1: REF INT, RANGE 1 mW").LeftJustified());
        AnsiConsole.MarkupLine("Connect the power meter to the POWER METER output with the 11730A cable. Power cord in, LINE on, "
            + "REF [bold]INT[/], RANGE [bold]1 mW[/], POLARITY [bold]NORMAL[/].");
        AnsiConsole.MarkupLine("Set FUNCTION to [bold]STANDBY[/]. Wait for the meter's downward drift to stop (typically under 60 s), then [bold]ZERO[/] it.");
        if (sim is not null)
        {
            sim.SetReference(ReferenceSwitch.Int);
            sim.SetPolarity(PolaritySwitch.Normal);
            sim.SetRange("1mW");
            sim.SetFunction(FunctionSwitch.Standby);
            ((SimulatedPowerMeter)meter).Zero();
        }
        else
        {
            ConsoleUi.Prompt("Press Enter when the meter is zeroed");
        }
        AnsiConsole.MarkupLine("Set FUNCTION to [bold]CALIBRATE[/], then [bold]CAL[/] the power meter.");
        if (sim is not null)
        {
            sim.SetFunction(FunctionSwitch.Calibrate);
            ((SimulatedPowerMeter)meter).Cal();
        }
        else
        {
            ConsoleUi.Prompt("Press Enter when the meter is calibrated");
        }
        var intWatts = ReadPower(session, meter, "REF INT, 1 mW");

        // Step 2: REF EXT.
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"Step 2: REF EXT, RANGE {RangeSwitch.Get(table.RangeSetting)}").LeftJustified());
        AnsiConsole.Write(new Panel(new Markup(
                "[bold red]WARNING[/] There is a small risk of mild electrical shock from the chassis of the 11683A option H01 "
                + "unless the power meter is connected. [grey](E4418B Service Guide p. 2-12)[/]\n"
                + "Keep the power meter connected, then [bold]unplug the 11683A's power cord[/]: the H01 ground loop stops it working "
                + "properly otherwise. The DC calibrator's [bold]Guard and LO must float[/], not be earthed."))
            .Border(BoxBorder.Heavy).BorderColor(Color.Red));
        AnsiConsole.MarkupLine($"Set RANGE [bold]{Markup.Escape(RangeSwitch.Get(table.RangeSetting).Label)}[/] and REF [bold]EXT[/]; FUNCTION stays CALIBRATE. "
            + $"[grey]({Markup.Escape(table.RangeSource)})[/]");
        AnsiConsole.MarkupLine("Move the DVM from DC REFERENCE OUTPUT to a [bold]BNC tee on REF EXT[/], and connect the DC calibrator's HI/LO to the tee.");
        if (sim is not null)
        {
            sim.LinePowered = false;
            sim.SetRange(table.RangeSetting);
            sim.SetReference(ReferenceSwitch.Ext);
            sim.DvmAt = DvmPoint.RefExtTee;
        }
        else
        {
            ConsoleUi.Prompt("Press Enter when connected");
        }
        var applied = SetSource(session, v1.Value, "the 1 mW voltage");
        var extWatts = ReadPower(session, meter, "REF EXT, 1 mW voltage");
        var transfer = new TransferCheck(intWatts, v1.Value, applied, extWatts, table.TransferTolerance);
        session.Log.Write($"Part 4 transfer: INT {intWatts} W, EXT {extWatts} W at {applied} V (target {v1}); agreement {transfer.Agreement:E3}");
        AnsiConsole.MarkupLine($"EXT/INT agreement [bold]{Percent(transfer.Agreement)}[/] (raw {Percent(transfer.RawAgreement)}), tolerance ±{Percent(transfer.Tolerance, false)}: "
            + (transfer.WithinTolerance ? "[green]within[/]" : "[yellow]outside[/]") + " [grey](indication)[/]");

        // Step 3, optional: Table 2-4.
        var points = new List<H01PointResult>();
        if (ConsoleUi.Confirm("Also step through the E4418B Table 2-4 voltages (about 10 points)?", defaultValue: sim is not null))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("Step 3: Table 2-4 (E4418B Service Guide steps 9–14)").LeftJustified());
            AnsiConsole.MarkupLine("FUNCTION [bold]STANDBY[/]; DC calibrator to 0 V, output off; meter filter 512; then [bold]ZERO[/] the meter.");
            if (sim is not null)
            {
                sim.SetFunction(FunctionSwitch.Standby);
                sim.ExternalSourceVolts = 0;
                ((SimulatedPowerMeter)meter).Zero();
            }
            else
            {
                ConsoleUi.Prompt("Press Enter when the meter is zeroed");
            }
            AnsiConsole.MarkupLine($"FUNCTION [bold]CALIBRATE[/]; DC calibrator to {Si.Format(table.CalibrationVolts, "V", 5)}, output '+'; then [bold]CAL[/] the meter.");
            if (sim is not null)
                sim.SetFunction(FunctionSwitch.Calibrate);
            SetSource(session, table.CalibrationVolts, "the calibration voltage");
            if (sim is not null)
                ((SimulatedPowerMeter)meter).Cal();
            else
                ConsoleUi.Prompt("Press Enter when the meter is calibrated");

            foreach (var row in table.Rows)
            {
                AnsiConsole.MarkupLine($"Meter filter [bold]{row.Filter}[/]. Expected {Si.Format(row.EffectiveWatts, "W", 5)} "
                    + $"({Si.Format(row.LowerWatts, "W", 5)} to {Si.Format(row.UpperWatts, "W", 5)}).");
                var v = SetSource(session, row.SettingVolts, "a Table 2-4 row");
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
        AnsiConsole.MarkupLine("Done. Turn the DC calibrator off, set REF to [bold]INT[/], plug the power cord back in, and move the DVM back to DC REFERENCE OUTPUT.");
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
        return new Part4Result(table.Source, meter.Description, meter.OverBus, transfer, points, notes);
    }

    /// <summary>Measures the 1 mW DC REFERENCE OUTPUT voltage with REF INT, as Part 1 would.</summary>
    private static double? MeasureOneMilliwatt(VerificationSession session)
    {
        var limits = session.Limits;
        var sim = session.Bench.Simulator;
        AnsiConsole.MarkupLine("No Part 1 result in this session, so first the 1 mW DC REFERENCE OUTPUT voltage: DVM on DC REFERENCE OUTPUT, "
            + "REF [bold]INT[/], POLARITY [bold]NORMAL[/], RANGE [bold]1 mW[/], FUNCTION [bold]CALIBRATE[/].");
        if (sim is not null)
        {
            sim.DvmAt = DvmPoint.DcReferenceOutput;
            sim.SetReference(ReferenceSwitch.Int);
            sim.SetPolarity(PolaritySwitch.Normal);
            sim.SetRange("1mW");
            sim.SetFunction(FunctionSwitch.Calibrate);
        }
        else
        {
            ConsoleUi.Prompt("Press Enter when set");
        }
        var acquirer = session.Acquirer(new RangeDetector(DvmFunction.DcVoltage, [new ExpectedValue("1mW", limits.NominalPart1Volts("1mW"))]));
        return session.MeasureNext(acquirer)?.Statistics.Mean;
    }

    /// <summary>
    /// Has the operator set the DC calibrator to <paramref name="target"/> while the DVM watches the REF EXT tee, then
    /// returns the mean of five readings.
    /// </summary>
    private static double SetSource(VerificationSession session, double target, string what)
    {
        var dvm = session.Dvm;
        var sim = session.Bench.Simulator;
        var range = RangePicker.Pick(dvm.Ranges(DvmFunction.DcVoltage), target);
        dvm.Configure(DvmFunction.DcVoltage, range, 10);
        AnsiConsole.MarkupLine($"Set the DC calibrator to [bold]{Si.Format(target, "V", 5)}[/] ({Markup.Escape(what)}).");
        if (sim is not null)
            sim.ExternalSourceVolts = target * (1 + 3e-6);   // a calibrator set a few ppm off

        if (ConsoleUi.Interactive && sim is null)
        {
            AnsiConsole.Live(new Text("")).Start(ctx =>
            {
                while (true)
                {
                    var r = dvm.Read();
                    var ppm = (r.Value / target - 1) * 1e6;
                    ctx.UpdateTarget(new Markup($"REF EXT: [bold]{Si.Format(r.Value, "V", 7)}[/]  error "
                        + $"{(Math.Abs(ppm) <= SourceBandPpm ? "[green]" : "[yellow]")}{ppm.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)} ppm[/]  "
                        + $"[blue]Adjust to within ±{SourceBandPpm:0} ppm, then press Enter.[/]"));
                    ctx.Refresh();
                    if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
                        break;
                }
            });
        }

        var readings = Enumerable.Range(0, 5).Select(_ => dvm.Read()).Where(r => !r.Overload).Select(r => r.Value).ToList();
        if (readings.Count == 0)
            throw new InvalidOperationException($"The DVM overloaded at the REF EXT tee on its {range} V range.");
        var mean = readings.Average();
        session.Log.Write($"REF EXT source for {what}: target {target} V, applied {mean} V");
        // 100 ppm, but at least 1 µV: at the lowest rows the DVM's own noise is tens of ppm.
        if (Math.Abs(mean - target) > Math.Max(100e-6 * target, 1e-6))
            AnsiConsole.MarkupLine($"[yellow]The DC calibrator is {Percent(mean / target - 1)} off the target; the reading is recorded with it.[/]");
        return mean;
    }

    private static double ReadPower(VerificationSession session, IPowerMeter meter, string what)
    {
        double watts;
        if (meter.OverBus)
        {
            if (session.Bench.Simulator is null)
                ConsoleUi.Prompt("Wait for the power meter reading to settle, then press Enter");
            var values = new List<double>();
            for (var i = 0; i < PowerReadings; i++)
            {
                values.Add(meter.ReadWatts());
                if (session.Bench.Simulator is null)
                    Thread.Sleep(500);
            }
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
        (fraction * 100).ToString(signed ? "+0.000;-0.000" : "0.0##", CultureInfo.InvariantCulture) + "%";
}
