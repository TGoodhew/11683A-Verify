using System.Globalization;
using System.Reflection;
using Spectre.Console;
using Verify11683A.App.Reports;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.Instruments.DcSource;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Instruments.PowerMeter;
using Verify11683A.Instruments.Simulation;
using Verify11683A.Instruments.Visa;
using Verify11683A.Measurement;
using Verify11683A.Meters;
using Verify11683A.Results;
using Verify11683A.Specs;

namespace Verify11683A.App.Procedures;

/// <summary>
/// Runs a power meter's performance tests, as transcribed from its manual into data/meters/&lt;model&gt;.json, with the
/// 11683A as the source. Each row tells the operator what to set on the 11683A and on the meter, then reads the meter
/// (over the bus, from the DVM on its recorder output, or typed in) and checks it against the manual's limits.
/// </summary>
public sealed class MeterTestProcedure(AppConfig config, CommandLine options, Bench bench, MeterDefinition definition, Screens screens)
{
    private const int BusReadings = 3;
    private const int DvmReadings = 5;

    private readonly Random _simOperator = new(435);
    private DvmIdentity? _dvmIdentity;
    private SimulatedPowerMeter? _simMeter;
    private IDcSource? _dcSource;
    private bool _dcSourceOpened;

    private Simulated11683A? Sim => bench.Simulator;

    public MeterRun Run()
    {
        var started = bench.Clock.Now;
        Header();

        var tests = ChooseTests();
        var serial = AskSerial();
        var channels = ChooseChannels(tests);
        var useBus = definition.BusDriver is not null && (Sim is not null
            || ConsoleUi.Confirm($"Read the {definition.Model} over the bus at {Resource}?", defaultValue: true));
        screens.SetSteps([.. tests.SelectMany(t => (t.PerChannel ? channels : [channels[0]]).Select(c => StepName(t, c))), "Results"]);

        var results = new List<MeterTestResult>();
        var identity = "";
        try
        {
            foreach (var test in tests)
            foreach (var channel in test.PerChannel ? channels : [channels[0]])
            {
                using var meter = OpenMeter(useBus, channel);
                meter.Identify();
                identity = meter.Description;
                bench.Log.Write($"Meter test {test.Id} channel {channel}: {meter.Description}");
                screens.Begin(StepName(test, channel), test.Title + (test.PerChannel ? $" — channel {channel}" : ""));
                var result = RunTest(test, channel, meter);
                if (result is null)
                    break;
                results.Add(result);
                AnsiConsole.WriteLine();
                screens.Report(() => AnsiConsole.Write(MeterReport.Table(result)));
                screens.Pause();
            }
        }
        finally
        {
            _dcSource?.Dispose();
        }

        var notes = new List<string>();
        if (definition.Ambiguities.Count > 0)
            notes.Add($"Transcription notes for {definition.FileName}: " + string.Join(" ", definition.Ambiguities));
        if (_dcSource is not null)
            notes.Add($"The DC calibrator on REF EXT ({_dcSource.Description}) was set by the app over the bus, on its OUTPUT terminals.");

        var recorderUsed = results.Any(r => r.Points.Any(p => p.Source == ReadingSource.Recorder));
        var defPath = Path.Combine(MeterDefinition.DefaultDirectory, definition.FileName);
        return new MeterRun
        {
            AppVersion = typeof(MeterTestProcedure).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
            Started = started,
            Finished = bench.Clock.Now,
            Company = config.Company,
            Operator = config.Operator,
            Meter = new MeterUnderTest(definition.Model, definition.Title, serial, identity,
                Sim is not null ? "simulated" : useBus ? $"{definition.BusDriver} driver at {Resource}" : "readings typed in by the operator",
                DataFileVersion.Of(defPath, definition.Manual)),
            Calibrator = new UnitUnderTest(config.Calibrator.Model, config.Calibrator.Serial, config.Calibrator.Options),
            Dvm = recorderUsed && _dvmIdentity is { } id
                ? new DvmRecord(config.Dvm.Details(id), bench.Dvm.ResourceName, id.Raw, config.Dvm.Details(id).StateOn(DateOnly.FromDateTime(started.Date)))
                : null,
            SimulationScenario = Sim?.Scenario.Name,
            Tests = results,
            NotRun = definition.NotCovered,
            Notes = notes,
        };
    }

    /// <summary>The test's name in the step tracker, with the channel when it is run on each.</summary>
    private string StepName(MeterTest test, string channel) =>
        test.Title + (test.PerChannel && definition.Channels.Count > 1 ? $" {channel}" : "");

    private string Resource => options.MeterResource ?? (config.PowerMeter.Model.Equals("manual", StringComparison.OrdinalIgnoreCase)
        ? definition.DefaultResource
        : config.PowerMeter.Resource);

    private void Header()
    {
        screens.Begin("Setup", "Setup: the meter and the tests to run");
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(definition.Manual)}[/]");
        AnsiConsole.MarkupLine($"Range calibrator: {Markup.Escape($"{config.Calibrator.Model} s/n {config.Calibrator.Serial}")}"
            + (config.Calibrator.Options == "" ? "" : Markup.Escape($", option {config.Calibrator.Options}")));
        if (definition.WarmupMinutes > 0)
            AnsiConsole.MarkupLine($"Warm the {Markup.Escape(definition.Model)} up for at least {definition.WarmupMinutes} min first.");
        foreach (var note in definition.Notes)
            AnsiConsole.MarkupLine($"[grey]•[/] {Markup.Escape(note)}");
        if (definition.NotCovered.Count > 0)
        {
            AnsiConsole.MarkupLine("[grey]Not run here (other equipment needed):[/]");
            foreach (var t in definition.NotCovered)
                AnsiConsole.MarkupLine($"[grey]  – {Markup.Escape(t.Title)}: {Markup.Escape(string.Join(", ", t.Equipment))}[/]");
        }
        AnsiConsole.WriteLine();
    }

    private List<MeterTest> ChooseTests()
    {
        var hasH01 = config.Calibrator.Options.Contains("H01", StringComparison.OrdinalIgnoreCase);
        var available = definition.Tests.Where(t => !t.NeedsH01 || hasH01).ToList();
        if (!ConsoleUi.Interactive)
            return available;
        var prompt = new MultiSelectionPrompt<MeterTest>()
            .Title("Which tests? [grey](space to toggle, Enter to start)[/]")
            .UseConverter(t => Markup.Escape($"{t.Title} ({t.Points.Count} rows)" + (t.NeedsH01 ? " – needs H01 and a DC calibrator" : "")))
            .AddChoices(available);
        foreach (var t in available.Where(t => !t.NeedsH01))
            prompt.Select(t);
        return AnsiConsole.Prompt(prompt);
    }

    private string AskSerial()
    {
        if (!ConsoleUi.Interactive)
            return Sim is not null ? "SIM0001" : "";
        return AnsiConsole.Prompt(new TextPrompt<string>($"{Markup.Escape(definition.Model)} serial number:").AllowEmpty()).Trim();
    }

    private List<string> ChooseChannels(List<MeterTest> tests)
    {
        if (definition.Channels.Count < 2 || !tests.Any(t => t.PerChannel) || !ConsoleUi.Interactive)
            return definition.Channels;
        var prompt = new MultiSelectionPrompt<string>().Title("Which channels?").AddChoices(definition.Channels);
        foreach (var c in definition.Channels)
            prompt.Select(c);
        var chosen = AnsiConsole.Prompt(prompt);
        return chosen.Count == 0 ? [definition.Channels[0]] : chosen;
    }

    private IPowerMeter OpenMeter(bool useBus, string channel)
    {
        if (Sim is { } sim)
        {
            _simMeter = new SimulatedPowerMeter(sim) { RangeResponse = RangeResponse(), Description = $"SIMULATED {definition.Model}" };
            return _simMeter;
        }
        if (!useBus)
            return new TypedPowerMeter(definition.Model);
        var visa = VisaSession.Open(Resource, config.PowerMeter.TimeoutMs);
        var channelNumber = definition.Channels.IndexOf(channel) + 1;
        return PowerMeters.Open(definition.BusDriver!, new LoggingVisaSession(visa, bench.Log.Write), Math.Max(1, channelNumber));
    }

    /// <summary>For the simulator: how this model reads each 11683A range, from its own accuracy table.</summary>
    private Func<string, double> RangeResponse()
    {
        var map = new Dictionary<string, double>();
        foreach (var p in definition.Tests.SelectMany(t => t.Points))
        {
            if (p.Calibrator is not { Function: FunctionSwitch.Calibrate, Reference: ReferenceSwitch.Int } c || p.CalFactorPercent is not null)
                continue;
            var watts = WattsOf(p.Expected, p.Unit);
            if (watts is null || map.ContainsKey(c.Range))
                continue;
            map[c.Range] = watts.Value / Simulated11683A.TrueFullScaleWatts(RangeSwitch.Get(c.Range));
        }
        return range => map.TryGetValue(range, out var r) ? r : 1;
    }

    private MeterTestResult? RunTest(MeterTest test, string channel, IPowerMeter meter)
    {
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(test.Source)}[/]");
        AnsiConsole.MarkupLine($"Equipment: {Markup.Escape(string.Join("; ", test.Equipment))}");
        for (var i = 0; i < test.Setup.Count; i++)
            AnsiConsole.MarkupLine($"  {i + 1}. {Markup.Escape(test.Setup[i])}");
        foreach (var note in test.Notes)
            AnsiConsole.MarkupLine($"[grey]  • {Markup.Escape(note)}[/]");
        if (test.Points.Any(p => test.SourceOf(p) == ReadingSource.Recorder))
            AnsiConsole.MarkupLine("[bold]The bench DVM reads the power meter's rear-panel RECORDER OUTPUT[/] (move it from the 11683A).");

        var dc = test.Points.Any(p => p.Calibrator?.ExternalVolts is not null) ? DcSource() : null;
        if (dc is not null)
        {
            dc.Standby();
            AnsiConsole.MarkupLine($"The app sets the DC calibrator ([bold]{Markup.Escape(dc.Description)}[/]): connect its [bold]OUTPUT HI/LO[/] to REF EXT. "
                + "It is in Standby; the zero and CAL come after the setup.");
        }

        if ((Sim is null || bench.Walkthrough) && !AskGo(
                (dc is null ? "Press Enter when the setup above is done" : "Press Enter when the connections above are made") + " (S to skip this test)"
                + (bench.Walkthrough && dc is null ? " [simulator: Enter does the zero and CAL]" : "")))
            return new MeterTestResult(test.Id, test.Title, test.Source, channel, []);
        if (Sim is not null)
            PrepareSimulation(test);
        if (dc is not null)
            ZeroAndCal(dc);

        var results = new List<MeterPointResult>();
        foreach (var point in test.Points)
        {
            var source = test.SourceOf(point);
            if (source == ReadingSource.Meter && !meter.OverBus)
                source = ReadingSource.Operator;
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(point.Label)}[/]  [grey]11683A:[/] {Markup.Escape(point.Calibrator?.ToString() ?? "not used")}  "
                + $"[grey]meter:[/] {Markup.Escape(point.MeterSetting)}");
            if (dc is not null && point.Calibrator?.ExternalVolts is { } volts)
            {
                dc.Output(volts);
                bench.Log.Write($"DC calibrator set to {volts.ToString("R", CultureInfo.InvariantCulture)} V");
                AnsiConsole.MarkupLine($"  [grey]DC calibrator set to {Si.Format(volts, "V", 5)}[/]");
            }

            double? reading;
            var simulated = false;
            if (Sim is not null)
            {
                if (bench.Walkthrough && source != ReadingSource.Operator
                    && !AskGo("Set as shown, wait for the reading to settle, then press Enter (S to skip) [simulator: Enter sets it]"))
                {
                    reading = null;
                }
                else
                {
                    ApplySimulation(point);
                    if (source == ReadingSource.Operator)
                    {
                        // The simulator cannot read a display: it offers the expected value with a last-digit wobble, which the
                        // walkthrough operator can accept or overtype (and a scripted run just takes).
                        var display = point.Expected + (point.Max - point.Min) * 0.02 * (_simOperator.NextDouble() - 0.5);
                        reading = bench.Walkthrough ? AskReading(point, display) : display;
                        simulated = reading == display;
                    }
                    else
                    {
                        reading = Read(point, source, meter);
                    }
                }
            }
            else if (source == ReadingSource.Operator)
            {
                reading = AskReading(point);
            }
            else
            {
                reading = AskGo("Set as shown, wait for the reading to settle, then press Enter (S to skip)") ? Read(point, source, meter) : null;
            }

            var result = new MeterPointResult(point, source, reading, simulated);
            results.Add(result);
            bench.Log.Write($"{definition.Model} {test.Id} {channel} '{point.Label}': {reading?.ToString("R", CultureInfo.InvariantCulture) ?? "skipped"} {point.Unit} "
                + $"[{point.Min}, {point.Max}] {source}{(simulated ? " (simulated operator)" : "")}");
            AnsiConsole.MarkupLine(reading is { } r
                ? $"  {(result.Pass == true ? "[green]✓[/]" : "[red]✗[/]")} {Fmt(r, point)} {Markup.Escape(MeterUnits.Display(point.Unit))} "
                  + $"[grey](limits {Fmt(point.Min, point)} to {Fmt(point.Max, point)})[/]"
                : "  [yellow]skipped[/]");
        }

        if (_simMeter is not null)
            _simMeter.CalFactorPercent = 100;
        dc?.Standby();
        return new MeterTestResult(test.Id, test.Title, test.Source, channel, results);
    }

    /// <summary>
    /// The DC calibrator for the REF EXT tests, opened on first use: on the bus (or simulated) when dcSource names a
    /// model, otherwise null and the operator sets it. The OUTPUT terminals only: no DVM watches it here, so the
    /// DIVIDER's ~500 Ω source resistance would go uncorrected.
    /// </summary>
    private IDcSource? DcSource()
    {
        if (!_dcSourceOpened)
        {
            _dcSourceOpened = true;
            _dcSource = bench.OpenDcSource(config.DcSource, useDivider: false);
        }
        return _dcSource;
    }

    /// <summary>E4418B Procedure 2 steps 9–13 with the calibrator on the bus: zero with it in Standby, CAL at 144.93 mV.</summary>
    private void ZeroAndCal(IDcSource dc)
    {
        dc.Standby();
        AnsiConsole.MarkupLine("11683A FUNCTION [bold]STANDBY[/]; the calibrator is in Standby at 0 V. Meter filter 512, then [bold]ZERO[/] the meter.");
        bench.Step("Press Enter when the meter is zeroed", s =>
        {
            s.SetFunction(FunctionSwitch.Standby);
            _simMeter!.Zero();
        });
        var calVolts = H01.H01Table.LoadDefault().CalibrationVolts;
        dc.Output(calVolts);
        bench.Log.Write($"DC calibrator set to {calVolts.ToString("R", CultureInfo.InvariantCulture)} V for CAL");
        AnsiConsole.MarkupLine($"The calibrator is at {Si.Format(calVolts, "V", 5)}. 11683A FUNCTION [bold]CALIBRATE[/], then [bold]CAL[/] the meter.");
        bench.Step("Press Enter when the meter is calibrated", s =>
        {
            s.SetFunction(FunctionSwitch.Calibrate);
            _simMeter!.Cal();
        });
    }

    private double? Read(MeterTestPoint point, ReadingSource source, IPowerMeter meter)
    {
        if (source == ReadingSource.Recorder)
        {
            var dvm = bench.Dvm;
            if (_dvmIdentity is null)
            {
                _dvmIdentity = dvm.Identify();
                dvm.Reset();
                bench.CheckTerminals();
            }
            var span = Math.Max(Math.Abs(point.Min), Math.Abs(point.Max));
            var volts = point.Unit == "mV" ? span * 1e-3 : span;
            // The 34401A and 3458A read 20% past each range.
            var range = RangePicker.Pick(dvm.Ranges(DvmFunction.DcVoltage), volts / 1.2);
            dvm.Configure(DvmFunction.DcVoltage, range, 10);
            var values = Enumerable.Range(0, DvmReadings).Select(_ => dvm.Read()).Where(r => !r.Overload).Select(r => r.Value).ToList();
            return values.Count == 0 ? null : MeterUnits.FromVolts(point.Unit, values.Average());
        }

        if (meter is Hp436APowerMeter hp436)
            hp436.UseCalFactorSwitch = point.CalFactorPercent is not null;
        var watts = Enumerable.Range(0, BusReadings).Select(_ => meter.ReadWatts()).Average();
        return MeterUnits.FromWatts(point.Unit, watts);
    }

    private void PrepareSimulation(MeterTest test)
    {
        var sim = Sim!;
        var meter = _simMeter!;
        var ext = test.Points.FirstOrDefault(p => p.Calibrator?.Reference == ReferenceSwitch.Ext)?.Calibrator;
        sim.DvmAt = DvmPoint.External;
        sim.ExternalDvmSource = () => 0;
        if (ext is not null)
        {
            // E4418B Procedure 2: zero with the DC calibrator off, CAL at 144.93 mV. A bus calibrator does its own (ZeroAndCal).
            sim.LinePowered = false;
            sim.SetRange(ext.Range);
            sim.SetReference(ReferenceSwitch.Ext);
            if (_dcSource is not null)
                return;
            sim.ExternalSourceVolts = 0;
            sim.SetFunction(FunctionSwitch.Standby);
            meter.Zero();
            sim.ExternalSourceVolts = H01.H01Table.LoadDefault().CalibrationVolts;
            sim.SetFunction(FunctionSwitch.Calibrate);
            meter.Cal();
            return;
        }
        sim.LinePowered = true;
        sim.SetReference(ReferenceSwitch.Int);
        sim.SetPolarity(PolaritySwitch.Normal);
        sim.SetRange("1mW");
        sim.SetFunction(FunctionSwitch.Standby);
        meter.Zero();
        sim.SetFunction(FunctionSwitch.Calibrate);
        meter.Cal();
    }

    private void ApplySimulation(MeterTestPoint point)
    {
        var sim = Sim!;
        var meter = _simMeter!;
        if (point.Calibrator is { } c)
        {
            sim.SetReference(c.Reference);
            sim.SetPolarity(c.Polarity);
            sim.SetRange(c.Range);
            sim.SetFunction(c.Function);
            if (c.ExternalVolts is { } v && _dcSource is null)
                sim.ExternalSourceVolts = v * (1 + 3e-6);
        }
        else
        {
            sim.SetFunction(FunctionSwitch.Standby);   // a power sensor with no RF: nothing reaches the meter
        }
        meter.CalFactorPercent = point.CalFactorPercent ?? 100;
        var fullScale = Simulated11683A.TrueFullScaleWatts(RangeSwitch.Get(point.MeterRange ?? point.Calibrator?.Range ?? "1mW"));
        sim.ExternalDvmSource = () => meter.DisplayedWatts() / fullScale;   // RECORDER OUTPUT: 1 V at full scale
    }

    private static bool AskGo(string message)
    {
        if (!ConsoleUi.Interactive)
            return true;
        var text = AnsiConsole.Prompt(new TextPrompt<string>($"[blue]{Markup.Escape(message)}[/]").AllowEmpty());
        return !text.Trim().Equals("s", StringComparison.OrdinalIgnoreCase);
    }

    private static double? AskReading(MeterTestPoint point, double? simulatedDisplay = null)
    {
        if (!ConsoleUi.Interactive)
            throw new InvalidOperationException("A typed reading needs an operator at the console.");
        var shown = simulatedDisplay is { } d ? Fmt(d, point) : null;
        var prompt = new TextPrompt<string>($"Reading in {Markup.Escape(MeterUnits.Display(point.Unit))} "
                + (shown is null ? "[grey](Enter to skip)[/]:" : $"[grey](the simulated display shows {shown}; Enter accepts it)[/]:"))
            .AllowEmpty();
        if (shown is not null)
            prompt = prompt.DefaultValue(shown).HideDefaultValue();
        var text = AnsiConsole.Prompt(prompt
            .Validate(t => t.Trim() == "" || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                ? ValidationResult.Success()
                : ValidationResult.Error("Enter a number, or nothing to skip.")));
        if (text.Trim() == "")
            return null;
        var value = double.Parse(text, CultureInfo.InvariantCulture);
        return simulatedDisplay is { } sd && text.Trim() == shown ? sd : value;
    }

    private static double? WattsOf(double value, string unit) => unit switch
    {
        "W" => value, "mW" => value * 1e-3, "uW" => value * 1e-6, "nW" => value * 1e-9, _ => null,
    };

    internal static string Fmt(double value, MeterTestPoint point)
    {
        // As many decimals as the manual's limits carry, plus one.
        static int Decimals(double v)
        {
            var s = Math.Abs(v).ToString("0.########", CultureInfo.InvariantCulture);
            var dot = s.IndexOf('.');
            return dot < 0 ? 0 : s.Length - dot - 1;
        }
        var decimals = Math.Min(8, Math.Max(Decimals(point.Min), Decimals(point.Max)) + 1);
        if (Math.Abs(value) < 0.5 * Math.Pow(10, -decimals))
            value = 0;   // no "-0.000"
        return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
}
