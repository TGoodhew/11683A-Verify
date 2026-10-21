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
        var planned = tests.SelectMany(t => (t.PerChannel ? channels : [channels[0]]).Select(c => (Test: t, Channel: c))).ToList();
        try
        {
            foreach (var (test, channel) in planned)
            {
                using var meter = ConsoleUi.Attempt($"Identifying the {definition.Model}" + (useBus ? $" at {Resource}" : ""),
                    () => OpenIdentified(useBus, channel), bench.Log, canSkip: false)!;
                identity = meter.Description;
                bench.Log.Write($"Meter test {test.Id} channel {channel}: {meter.Description}");
                screens.Begin(StepName(test, channel), test.Title + (test.PerChannel ? $" — channel {channel}" : ""));
                var result = RunTest(test, channel, meter);
                results.Add(result);
                AnsiConsole.WriteLine();
                screens.Report(() => AnsiConsole.Write(MeterReport.Table(result)));
                if (_stopped is not null)
                    break;
                screens.Pause();
            }
        }
        catch (Exception ex) when (ex is OperatorStopException or InstrumentException)
        {
            Stop(ex);
        }
        finally
        {
            try
            {
                _dcSource?.Dispose();
            }
            catch (InstrumentException ex)
            {
                bench.Log.Write($"DC calibrator not put in Standby on closing: {ex.Message}");
                AnsiConsole.MarkupLine("[red]Check the DC calibrator is in Standby: it did not answer on closing.[/]");
            }
        }

        var notes = new List<string>();
        if (_stopped is not null)
        {
            notes.Add($"Stopped before the end: {_stopped.Message}");
            // Tests not reached are on the record too, every row not measured.
            foreach (var (test, channel) in planned.Skip(results.Count))
                results.Add(new MeterTestResult(test.Id, test.Title, test.Source, channel,
                    [.. test.Points.Select(p => new MeterPointResult(p, test.SourceOf(p), null, false))]));
        }
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

    /// <summary>Why the run stopped early (an instrument error with no one to retry it, or the operator's Stop); null if it did not.</summary>
    private Exception? _stopped;

    private void Stop(Exception ex)
    {
        _stopped ??= ex;
        bench.Log.Write($"Run stopped: {ex.Message}");
        AnsiConsole.MarkupLine($"[yellow]The run stopped: {Markup.Escape(ex.Message)} The rows measured so far are kept in the record.[/]");
    }

    /// <summary>Opens and identifies the meter; the session is closed again if it does not answer.</summary>
    private IPowerMeter OpenIdentified(bool useBus, string channel)
    {
        var meter = OpenMeter(useBus, channel);
        try
        {
            meter.Identify();
            return meter;
        }
        catch
        {
            meter.Dispose();
            throw;
        }
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
            // A simulated 436A plays its front-panel-only CAL ADJ and CAL FACTOR switch too.
            var frontPanelCal = definition.BusDriver == "436A";
            _simMeter = new SimulatedPowerMeter(sim)
            {
                RangeResponse = RangeResponse(),
                Description = $"SIMULATED {definition.Model}",
                CanCalibrate = !frontPanelCal,
                CanSetCalFactor = !frontPanelCal,
            };
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

    private MeterTestResult RunTest(MeterTest test, string channel, IPowerMeter meter)
    {
        if (test.Automated && meter.OverBus && meter is IPowerMeterControl control)
            return RunAutomated(test, channel, control);

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
    /// A test whose meter steps the app makes over the bus: Preset and the test's settings, the zero and the CAL, then
    /// each row's settings and a settled reading. The operator is asked only to set the 11683A, and only when a row
    /// changes it; a 436A's CAL ADJ and CAL FACTOR switch are the exceptions, being front-panel only.
    /// </summary>
    private MeterTestResult RunAutomated(MeterTest test, string channel, IPowerMeterControl control)
    {
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(test.Source)}[/]");
        AnsiConsole.MarkupLine($"Equipment: {Markup.Escape(string.Join("; ", test.Equipment))}");
        AnsiConsole.MarkupLine("[grey]The manual's steps. The app makes the meter's settings, zero and CAL over the bus; you set the 11683A when asked.[/]");
        for (var i = 0; i < test.Setup.Count; i++)
            AnsiConsole.MarkupLine($"[grey]  {i + 1}. {Markup.Escape(test.Setup[i])}[/]");
        foreach (var note in test.Notes)
            AnsiConsole.MarkupLine($"[grey]  • {Markup.Escape(note)}[/]");

        var dc = test.Points.Any(p => p.Calibrator?.ExternalVolts is not null) ? DcSource() : null;
        if (dc is not null)
        {
            dc.Standby();
            AnsiConsole.MarkupLine($"The app sets the DC calibrator ([bold]{Markup.Escape(dc.Description)}[/]): connect its [bold]OUTPUT HI/LO[/] to REF EXT. It is in Standby.");
        }
        if ((Sim is null || bench.Walkthrough) && !AskGo("Make the connections above, then press Enter (S to skip this test)"))
            return new MeterTestResult(test.Id, test.Title, test.Source, channel, []);

        _calibratorNow = null;
        _calFactorSwitch = 100;
        var baseline = test.Meter!;
        var log = bench.Log;
        var results = new List<MeterPointResult>();
        try
        {
            ConsoleUi.Attempt("Presetting the meter", () =>
            {
                control.Preset();
                control.Apply(baseline, null);
            }, log);
            (MeterSettings, double?)? applied = (baseline, null);
            log.Write($"{definition.Model} {test.Id} {channel}: preset; {baseline}");

            if (test.Zero is { } zero)
            {
                SetCalibrator(zero.Calibrator, dc, "for the zero", allowSkip: false);
                Settle(zero.SettleSeconds);
                ConsoleUi.Attempt("Zeroing the meter", () => Busy("Zeroing the meter", control.Zero), log);
                AnsiConsole.MarkupLine("[green]✓[/] Meter zeroed.");
            }
            if (test.Cal is { } cal)
            {
                SetCalibrator(cal.Calibrator, dc, "for the CAL", allowSkip: false);
                Settle(cal.SettleSeconds);
                if (control.CanCalibrate)
                {
                    ConsoleUi.Attempt("CAL on the 11683A", () => Busy("CAL on the 11683A", () => control.Calibrate(cal.ReferenceCalFactorPercent)), log);
                    AnsiConsole.MarkupLine($"[green]✓[/] Meter calibrated (reference cal factor {cal.ReferenceCalFactorPercent.ToString("0.#", CultureInfo.InvariantCulture)}%).");
                }
                else
                {
                    ConsoleUi.Attempt("CAL ADJ", () => AdjustByHand(control, cal.AdjustTo ?? "1.000 mW"), log);
                }
            }

            foreach (var point in test.Points)
            {
                var source = test.SourceOf(point);
                AnsiConsole.MarkupLine($"[bold]{Markup.Escape(point.Label)}[/]  [grey]meter:[/] {Markup.Escape(point.MeterSetting)}");
                double? reading = null;
                if (SetCalibrator(point.Calibrator, dc, null, allowSkip: true))
                {
                    if (!control.CanSetCalFactor && (point.CalFactorPercent ?? 100) != _calFactorSwitch)
                        SetCalFactorSwitch(point.CalFactorPercent ?? 100);
                    var settings = baseline.With(point.Meter);
                    reading = ConsoleUi.Attempt($"Reading '{point.Label}'", () =>
                    {
                        if (settings.StoreReference || applied != (settings, point.CalFactorPercent))
                        {
                            control.Apply(settings, point.CalFactorPercent);
                            applied = (settings, point.CalFactorPercent);
                            log.Write($"{definition.Model} settings for '{point.Label}': {settings}, cal factor {point.CalFactorPercent?.ToString(CultureInfo.InvariantCulture) ?? "100"}");
                        }
                        return source switch
                        {
                            ReadingSource.Operator => AskReading(point),
                            ReadingSource.Recorder => Read(point, source, (IPowerMeter)control),
                            _ => ReadSettled(control, point, settings.Display ?? MeterDisplay.Watts),
                        };
                    }, log, canSkip: true);
                }

                var result = new MeterPointResult(point, source, reading, false);
                results.Add(result);
                log.Write($"{definition.Model} {test.Id} {channel} '{point.Label}': {reading?.ToString("R", CultureInfo.InvariantCulture) ?? "skipped"} {point.Unit} "
                    + $"[{point.Min}, {point.Max}] {source}, automated");
                AnsiConsole.MarkupLine(reading is { } r
                    ? $"  {(result.Pass == true ? "[green]✓[/]" : "[red]✗[/]")} {Fmt(r, point)} {Markup.Escape(MeterUnits.Display(point.Unit))} "
                      + $"[grey](limits {Fmt(point.Min, point)} to {Fmt(point.Max, point)})[/]"
                    : "  [yellow]skipped[/]");
            }

            if (_calFactorSwitch != 100)
                SetCalFactorSwitch(100);
        }
        catch (Exception ex) when (ex is OperatorStopException or InstrumentException)
        {
            Stop(ex);
            // The rows not reached are on the record as not measured, so the run is not taken for complete.
            foreach (var p in test.Points.Skip(results.Count))
                results.Add(new MeterPointResult(p, test.SourceOf(p), null, false));
        }
        finally
        {
            StandbyQuietly(dc);
        }
        return new MeterTestResult(test.Id, test.Title, test.Source, channel, results);
    }

    /// <summary>The DC calibrator to Standby, even after an error; a failure is reported, not thrown over the first one.</summary>
    private void StandbyQuietly(IDcSource? dc)
    {
        try
        {
            dc?.Standby();
        }
        catch (InstrumentException ex)
        {
            bench.Log.Write($"DC calibrator Standby failed: {ex.Message}");
            AnsiConsole.MarkupLine("[red]The DC calibrator did not answer: put it in Standby by hand.[/]");
        }
    }

    /// <summary>The 11683A's switches as the operator last set them in this test; null before the first.</summary>
    private CalibratorSetting? _calibratorNow;

    /// <summary>A 436A's CAL FACTOR switch, as the operator last set it.</summary>
    private double _calFactorSwitch = 100;

    /// <summary>
    /// Has the operator set the 11683A to <paramref name="setting"/>, unless it is already so; a bus DC calibrator is
    /// set by the app. Returns false if the operator skipped the row.
    /// </summary>
    private bool SetCalibrator(CalibratorSetting? setting, IDcSource? dc, string? purpose, bool allowSkip)
    {
        if (setting is null)
            return true;
        var knobs = setting with { ExternalVolts = null };
        if (_calibratorNow is not null && _calibratorNow with { ExternalVolts = null } == knobs && (dc is not null || _calibratorNow == setting))
        {
            ApplyDcSource(setting, dc);
            _calibratorNow = setting;
            return true;
        }

        AnsiConsole.MarkupLine($"Set the 11683A{(purpose is null ? "" : " " + purpose)}: [bold]{Markup.Escape(Describe(setting, dc))}[/]");
        if ((Sim is null || bench.Walkthrough))
        {
            var prompt = "Press Enter when set" + (allowSkip ? " (S to skip this row)" : "") + (bench.Walkthrough ? " [simulator: Enter sets it]" : "");
            if (allowSkip ? !AskGo(prompt) : !Ask(prompt))
                return false;
        }
        if (Sim is { } sim)
        {
            sim.LinePowered = setting.Reference == ReferenceSwitch.Int;
            sim.SetReference(setting.Reference);
            sim.SetPolarity(setting.Polarity);
            sim.SetRange(setting.Range);
            sim.SetFunction(setting.Function);
            if (dc is null && setting.ExternalVolts is { } v)
                sim.ExternalSourceVolts = v * (1 + 3e-6);   // a hand-set calibrator a few ppm off
        }
        ApplyDcSource(setting, dc);
        _calibratorNow = setting;
        return true;
    }

    /// <summary>The 11683A's switches for the operator; with a bus calibrator its voltage is the app's to set.</summary>
    private static string Describe(CalibratorSetting setting, IDcSource? dc) =>
        dc is null ? setting.ToString() : (setting with { ExternalVolts = null }).ToString().Replace(", DC calibrator 0 V", "", StringComparison.Ordinal);

    private void ApplyDcSource(CalibratorSetting setting, IDcSource? dc)
    {
        if (dc is null || setting.Reference != ReferenceSwitch.Ext)
            return;
        if (setting.ExternalVolts is { } v and not 0)
        {
            ConsoleUi.Attempt($"Setting the DC calibrator to {Si.Format(v, "V", 5)}", () => dc.Output(v), bench.Log);
            bench.Log.Write($"DC calibrator set to {v.ToString("R", CultureInfo.InvariantCulture)} V");
            AnsiConsole.MarkupLine($"  [grey]DC calibrator set to {Si.Format(v, "V", 5)}[/]");
        }
        else
        {
            ConsoleUi.Attempt("Putting the DC calibrator in Standby", dc.Standby, bench.Log);
            AnsiConsole.MarkupLine("  [grey]DC calibrator in Standby (0 V)[/]");
        }
    }

    /// <summary>Waits for the 11683A to settle (its STANDBY drift takes up to 60 s), with a countdown.</summary>
    private void Settle(int seconds)
    {
        if (seconds <= 0 || Sim is not null)
            return;
        AnsiConsole.Status().Start($"Letting the 11683A settle: {seconds} s", ctx =>
        {
            for (var left = seconds; left > 0; left--)
            {
                ctx.Status($"Letting the 11683A settle: {left} s [grey](Enter to go on now)[/]");
                for (var tick = 0; tick < 10; tick++)
                {
                    if (ConsoleUi.Interactive && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
                    {
                        bench.Log.Write($"Settling cut short by the operator with {left} s to go");
                        return;
                    }
                    Thread.Sleep(100);
                }
            }
        });
    }

    private static void Busy(string what, Action action)
    {
        if (ConsoleUi.Interactive)
            AnsiConsole.Status().Start(what + "…", _ => action());
        else
            action();
    }

    /// <summary>
    /// Readings until two in a row agree to within a fifth of the half-tolerance (the meter's own settling covers its
    /// filter, not the 11683A's), and their mean; at most eight.
    /// </summary>
    private double? ReadSettled(IPowerMeterControl control, MeterTestPoint point, MeterDisplay display)
    {
        double Next()
        {
            var value = control.Read();
            return display == MeterDisplay.Watts ? MeterUnits.FromWatts(point.Unit, value)!.Value : value;
        }

        var band = Math.Max((point.Max - point.Min) / 2 * 0.2, 1e-12);
        var previous = Next();
        for (var i = 0; i < 7; i++)
        {
            var next = Next();
            if (Math.Abs(next - previous) <= band)
                return (previous + next) / 2;
            previous = next;
        }
        bench.Log.Write($"'{point.Label}': readings did not agree to within {band} {point.Unit}; the last is used");
        AnsiConsole.MarkupLine("  [yellow]The readings kept changing; the last one is recorded.[/]");
        return previous;
    }

    /// <summary>CAL as a front-panel adjustment (436A CAL ADJ): the operator turns it while the reading updates.</summary>
    private void AdjustByHand(IPowerMeterControl control, string target)
    {
        AnsiConsole.MarkupLine($"Adjust the meter's [bold]CAL ADJ[/] for [bold]{Markup.Escape(target)}[/] (it has no bus control).");
        if (Sim is not null)
        {
            bench.Step("Press Enter when adjusted", _ => _simMeter!.Cal());
            return;
        }
        if (!ConsoleUi.Interactive)
            throw new OperatorException("CAL ADJ needs an operator at the console.");
        AnsiConsole.Live(new Text("")).Start(ctx =>
        {
            while (true)
            {
                var watts = control.Read();
                ctx.UpdateTarget(new Markup($"Meter: [bold]{Si.Format(watts, "W", 5)}[/]  [blue]Adjust CAL ADJ, then press Enter.[/]"));
                ctx.Refresh();
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
                    break;
            }
        });
        bench.Log.Write($"CAL ADJ set by the operator for {target}");
    }

    /// <summary>A CAL FACTOR switch the bus cannot set (436A): the operator turns it.</summary>
    private void SetCalFactorSwitch(double percent)
    {
        AnsiConsole.MarkupLine($"Set the meter's [bold]CAL FACTOR[/] switch to [bold]{percent.ToString("0.#", CultureInfo.InvariantCulture)}%[/].");
        bench.Step("Press Enter when set", _ => _simMeter!.CalFactorPercent = percent);
        _calFactorSwitch = percent;
    }

    private static bool Ask(string message)
    {
        if (ConsoleUi.Interactive)
            AnsiConsole.Prompt(new TextPrompt<string>($"[blue]{Markup.Escape(message)}[/]").AllowEmpty());
        return true;
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
            throw new OperatorException("A typed reading needs an operator at the console.");
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
