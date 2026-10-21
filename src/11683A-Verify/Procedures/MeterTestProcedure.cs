using System.Diagnostics;
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
using Verify11683A.Uncertainty;

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

        var serial = AskSerial();
        var retest = OfferRetest(serial);
        List<(MeterTest Test, string Channel)> planned;
        if (retest is not null)
        {
            // Only what the last run did not pass, still channel by channel.
            var redo = retest.ToRerun;
            var channelOrder = definition.Channels;
            planned = [.. redo
                .Select(r => (Test: definition.Tests.FirstOrDefault(t => t.Id == r.TestId), r.Channel))
                .Where(p => p.Test is not null)
                .Select(p => (p.Test!, p.Channel))
                .OrderBy(p => channelOrder.IndexOf(p.Channel)).ThenBy(p => definition.Tests.IndexOf(p.Item1))];
        }
        else
        {
            var tests = ChooseTests();
            var channels = ChooseChannels(tests);
            // Every test on one channel before the cable moves to the next: A, then B.
            planned = [.. channels.SelectMany((c, i) => tests.Where(t => t.PerChannel || i == 0).Select(t => (t, c)))];
        }
        var useBus = definition.BusDriver is not null && (Sim is not null
            || ConsoleUi.Confirm($"Read the {definition.Model} over the bus at {Resource}?", defaultValue: true));
        screens.SetSteps([.. planned.Select(p => StepName(p.Test, p.Channel)), "Results"]);

        var results = new List<MeterTestResult>();
        var identity = "";
        try
        {
            foreach (var (test, channel) in planned)
            {
                using var meter = ConsoleUi.Attempt($"Identifying the {definition.Model}" + (useBus ? $" at {Resource}" : ""),
                    () => OpenIdentified(useBus, channel), bench.Log, canSkip: false)!;
                identity = meter.Description;
                bench.Log.Write($"Meter test {test.Id} channel {channel}: {meter.Description}");
                screens.Begin(StepName(test, channel), ShortTitle(test) + (test.PerChannel && definition.Channels.Count > 1 ? $" — channel {channel}" : ""));
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
        foreach (var ((testId, channel, label), readings) in _rereads)
        {
            var test = definition.Tests.First(t => t.Id == testId);
            notes.Add($"{StepName(test, channel)}, {label}: outside its limits and read again at once, {readings} readings in all; the last is recorded.");
        }
        if (definition.Ambiguities.Count > 0)
            notes.Add($"Transcription notes for {definition.FileName}: " + string.Join(" ", definition.Ambiguities));
        if (_dcSource is not null)
            notes.Add($"The DC calibrator on REF EXT ({_dcSource.Description}) was set by the app over the bus, on its OUTPUT terminals.");
        IReadOnlyList<MeterTestResult> recorded = results;
        if (retest is not null)
        {
            notes.Insert(0, retest.Note(results));
            recorded = retest.Merge(results);
            bench.Log.Write(retest.Note(results));
        }

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
            Tests = recorded,
            NotRun = definition.NotCovered,
            Notes = notes,
        };
    }

    /// <summary>
    /// If this meter's last run (same model and serial) did not pass everything, offers to run again only the tests it
    /// failed or left incomplete; their new results then replace the old in a combined record. --retest accepts without
    /// asking.
    /// </summary>
    private MeterRetest? OfferRetest(string serial)
    {
        var last = MeterRetest.Latest(config.ResultsDirectory, definition.Model, serial, Sim is not null);
        if (last is null || last.ToRerun.Count == 0)
            return null;
        var grid = new Grid().AddColumn(new GridColumn().PadRight(3)).AddColumn();
        foreach (var t in last.ToRerun)
            grid.AddRow(Markup.Escape(StepName(definition.Tests.FirstOrDefault(d => d.Id == t.TestId) ?? new MeterTest { Title = t.Title, PerChannel = true }, t.Channel)),
                !t.Complete && t.Verdict != Verdict.Fail ? "[yellow]INCOMPLETE[/]" : ConsoleUi.VerdictMarkup(t.Verdict));
        var header = $" Last run of this {definition.Model}: {last.Previous.Started:yyyy-MM-dd HH:mm} ";
        var widest = last.ToRerun.Max(t => StepName(definition.Tests.FirstOrDefault(d => d.Id == t.TestId) ?? new MeterTest { Title = t.Title }, t.Channel).Length) + 3 + 12 + 4;
        AnsiConsole.Write(new Panel(grid) { Width = Math.Max(widest, header.Length + 6) }
            .Header(Markup.Escape(header))
            .Border(BoxBorder.Rounded).BorderColor(Color.Yellow));
        var accept = options.Retest || (ConsoleUi.Interactive && ConsoleUi.Confirm("Run only these again (the passing tests are kept from that run)?", defaultValue: true));
        bench.Log.Write(accept
            ? $"Retest of {Path.GetFileName(last.PreviousPath)}: {string.Join(", ", last.ToRerun.Select(t => $"{t.TestId} {t.Channel}"))}"
            : "Retest offered and declined");
        return accept ? last : null;
    }

    /// <summary>The rows read again straight after a reading outside their limits, and how many readings each had in all.</summary>
    private readonly Dictionary<(string TestId, string Channel, string Label), int> _rereads = [];

    /// <summary>
    /// Takes a row's reading with <paramref name="read"/> and, while it is outside the limits, offers at the console to
    /// read the row again at once (the operator can check the 11683A and the meter first). The last reading counts.
    /// </summary>
    private double? ReadRow(MeterTest test, string channel, MeterTestPoint point, ReadingSource source, Func<double?> read)
    {
        var reading = read();
        for (var count = 2; reading is { } r && new MeterPointResult(point, source, r, false).Pass == false && OfferReread(test, channel, point, r); count++)
        {
            bench.Log.Write($"{definition.Model} {test.Id} {channel} '{point.Label}': {r.ToString("R", CultureInfo.InvariantCulture)} {point.Unit} is outside its limits; read again (reading {count})");
            _rereads[(test.Id, channel, point.Label)] = count;
            reading = read();
        }
        return reading;
    }

    /// <summary>After a reading outside its limits, at the console: read the row again? Not offered to a scripted run.</summary>
    private bool OfferReread(MeterTest test, string channel, MeterTestPoint point, double reading)
    {
        if (!ConsoleUi.Interactive || _stopped is not null)
            return false;
        var unit = Markup.Escape(MeterUnits.Display(point.Unit));
        AnsiConsole.MarkupLine($"  [red]✗ {Markup.Escape(point.Label)}: {Fmt(reading, point)} {unit}[/], outside {Fmt(point.Min, point)} to {Fmt(point.Max, point)} {unit}.");
        var again = ConsoleUi.Confirm("Read this step again?", defaultValue: true);
        if (!again)
            bench.Log.Write($"{definition.Model} {test.Id} {channel} '{point.Label}': outside its limits; not read again");
        return again;
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
        (test.PerChannel && definition.Channels.Count > 1 ? $"{channel}: " : "") + ShortTitle(test);

    /// <summary>"Instrument Accuracy Test, Procedure 1 (standard 11683A; for indication)" without the parenthesis.</summary>
    private static string ShortTitle(MeterTest test) =>
        test.Title.IndexOf(" (", StringComparison.Ordinal) is > 0 and var cut ? test.Title[..cut] : test.Title;

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
        return ConsoleUi.Ask(prompt);
    }

    private string AskSerial()
    {
        if (!ConsoleUi.Interactive)
            return Sim is not null ? "SIM0001" : "";
        return ConsoleUi.Ask(new TextPrompt<string>($"{Markup.Escape(definition.Model)} serial number:").AllowEmpty()).Trim();
    }

    private List<string> ChooseChannels(List<MeterTest> tests)
    {
        if (definition.Channels.Count < 2 || !tests.Any(t => t.PerChannel) || !ConsoleUi.Interactive)
            return definition.Channels;
        var prompt = new MultiSelectionPrompt<string>().Title("Which channels?").AddChoices(definition.Channels);
        foreach (var c in definition.Channels)
            prompt.Select(c);
        var chosen = ConsoleUi.Ask(prompt);
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

        var results = new List<MeterPointResult>();
        try
        {
            if ((Sim is null || bench.Walkthrough) && !AskGo(
                    (dc is null ? "Press Enter when the setup above is done" : "Press Enter when the connections above are made") + " (S to skip this test, X to exit)"
                    + (bench.Walkthrough && dc is null ? " [simulator: Enter does the zero and CAL]" : "")))
                return new MeterTestResult(test.Id, test.Title, test.Source, channel, []);
            if (Sim is not null)
                PrepareSimulation(test);
            if (dc is not null)
                ZeroAndCal(dc);
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

                var simulated = false;
                double? Take()
                {
                    if (Sim is not null)
                    {
                        if (bench.Walkthrough && source != ReadingSource.Operator
                            && !AskGo("Set as shown, wait for the reading to settle, then press Enter (S to skip, X to exit) [simulator: Enter sets it]"))
                        {
                            return null;
                        }
                        else
                        {
                            ApplySimulation(point);
                            if (source == ReadingSource.Operator)
                            {
                                // The simulator cannot read a display: it offers the expected value with a last-digit wobble, which the
                                // walkthrough operator can accept or overtype (and a scripted run just takes).
                                var display = point.Expected + (point.Max - point.Min) * 0.02 * (_simOperator.NextDouble() - 0.5);
                                var typed = bench.Walkthrough ? AskReading(point, display) : display;
                                simulated = typed == display;
                                return typed;
                            }
                            else
                            {
                                return Read(point, source, meter);
                            }
                        }
                    }
                    else if (source == ReadingSource.Operator)
                    {
                        return AskReading(point);
                    }
                    else
                    {
                        return AskGo("Set as shown, wait for the reading to settle, then press Enter (S to skip, X to exit)") ? Read(point, source, meter) : null;
                    }
                }

                var reading = ReadRow(test, channel, point, source, Take);
                var result = new MeterPointResult(point, source, reading, simulated);
                results.Add(result);
                bench.Log.Write($"{definition.Model} {test.Id} {channel} '{point.Label}': {reading?.ToString("R", CultureInfo.InvariantCulture) ?? "skipped"} {point.Unit} "
                    + $"[{point.Min}, {point.Max}] {source}{(simulated ? " (simulated operator)" : "")}");
                AnsiConsole.MarkupLine(reading is { } r
                    ? $"  {(result.Pass == true ? "[green]✓[/]" : "[red]✗[/]")} {Fmt(r, point)} {Markup.Escape(MeterUnits.Display(point.Unit))} "
                      + $"[grey](limits {Fmt(point.Min, point)} to {Fmt(point.Max, point)})[/]"
                    : "  [yellow]skipped[/]");
            }
        }
        catch (Exception ex) when (ex is OperatorStopException or InstrumentException)
        {
            Stop(ex);
            // As in an automated test: the readings taken are kept, and the rows not reached are on the record as not measured.
            foreach (var p in test.Points.Skip(results.Count))
                results.Add(new MeterPointResult(p, test.SourceOf(p), null, false));
        }
        finally
        {
            if (_simMeter is not null)
                _simMeter.CalFactorPercent = 100;
            StandbyQuietly(dc);
        }
        return new MeterTestResult(test.Id, test.Title, test.Source, channel, results);
    }

    /// <summary>
    /// A test whose meter steps the app makes over the bus: Preset and the test's settings, the zero and the CAL, then
    /// each row's settings and a settled reading. The operator is asked only to set the 11683A, and only when a row
    /// changes it; a 436A's CAL ADJ and CAL FACTOR switch are the exceptions, being front-panel only.
    /// </summary>
    private MeterTestResult RunAutomated(MeterTest test, string channel, IPowerMeterControl control)
    {
        var dc = test.Points.Any(p => p.Calibrator?.ExternalVolts is not null) ? DcSource() : null;
        dc?.Standby();
        if (!Connect(Connections(test, channel, dc)))
            return new MeterTestResult(test.Id, test.Title, test.Source, channel, []);

        _calFactorSwitch = 100;
        var baseline = test.Meter!;
        var log = bench.Log;
        var results = new List<MeterPointResult>();
        try
        {
            ConsoleUi.Attempt("Presetting the meter", control.Preset, log);
            // The STANDBY check comes before the test's settings, while a reading is quick (an E441x at filter 512 takes 26 s).
            if (test.Zero is { } zero)
            {
                SetCalibrator(zero.Calibrator, dc, "for the zero", allowSkip: false);
                Settle(zero.SettleSeconds, control, MeterDisplay.Watts);   // just preset: watts
                CheckStandby(control, zero);
            }
            ConsoleUi.Attempt("Setting up the meter", () => control.Apply(baseline, null), log);
            (MeterSettings, double?)? applied = (baseline, null);
            log.Write($"{definition.Model} {test.Id} {channel}: preset; {baseline}");

            if (test.Zero is not null)
                ZeroMeter(control);
            if (test.Cal is { } cal)
            {
                if (control.CanCalibrate)
                    CalibrateMeter(control, test, cal, dc);
                else
                {
                    SetCalibrator(cal.Calibrator, dc, "for the CAL", allowSkip: false);
                    Settle(cal.SettleSeconds, control, baseline.Display ?? MeterDisplay.Watts);
                    ConsoleUi.Attempt("CAL ADJ", () => AdjustByHand(control, cal.AdjustTo ?? "1.000 mW"), log);
                }
            }

            foreach (var point in test.Points)
            {
                ShowProgress(test, channel, results);
                var source = test.SourceOf(point);
                double? reading = null;
                if (SetCalibrator(point.Calibrator, dc, null, allowSkip: true))
                {
                    if (!control.CanSetCalFactor && (point.CalFactorPercent ?? 100) != _calFactorSwitch)
                        SetCalFactorSwitch(point.CalFactorPercent ?? 100);
                    var settings = baseline.With(point.Meter);
                    reading = ReadRow(test, channel, point, source, () => ConsoleUi.Attempt($"Reading '{point.Label}'", () =>
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
                    }, log, canSkip: true));
                }

                var result = new MeterPointResult(point, source, reading, false);
                results.Add(result);
                log.Write($"{definition.Model} {test.Id} {channel} '{point.Label}': {reading?.ToString("R", CultureInfo.InvariantCulture) ?? "skipped"} {point.Unit} "
                    + $"[{point.Min}, {point.Max}] {source}, automated");
                if (!screens.Clearing)
                    RowLine(point, result, reading);
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

    private void ZeroMeter(IPowerMeterControl control)
    {
        ConsoleUi.Attempt("Zeroing the meter", () => Busy("Zeroing the meter", control.Zero), bench.Log);
        AnsiConsole.MarkupLine("[green]✓[/] Meter zeroed.");
    }

    /// <summary>
    /// Before the zero: the meter should see next to nothing with the 11683A in STANDBY. If it sees power, FUNCTION is
    /// most likely still in CALIBRATE, and a zero now would take that power as zero and make the CAL fail; the operator
    /// is told, and can check again, zero anyway or exit. The limit is a quarter of the zero range's full scale.
    /// </summary>
    private void CheckStandby(IPowerMeterControl control, MeterStep zero)
    {
        var limit = 0.25 * Simulated11683A.TrueFullScaleWatts(RangeSwitch.Get(zero.Calibrator.Range));
        while (true)
        {
            var watts = ConsoleUi.Attempt("Checking the 11683A is in STANDBY",
                () => (double?)ConsoleUi.Busy("Checking the 11683A is in STANDBY", _ => control.Read()), bench.Log, canSkip: true);
            if (watts is not { } w || Math.Abs(w) <= limit)
                return;
            bench.Log.Write($"Before the zero the meter read {w.ToString("R", CultureInfo.InvariantCulture)} W (limit {limit} W): the 11683A looks to be in CALIBRATE");
            AnsiConsole.MarkupLine($"[red]✗ The meter reads {Si.Format(w, "W", 4)} with the 11683A meant to be in STANDBY: FUNCTION looks to be in CALIBRATE.[/]");
            AnsiConsole.MarkupLine("[grey]Zeroing now would take that power as zero, and the CAL would then fail.[/]");
            var choice = ConsoleUi.Choose("Set FUNCTION to STANDBY, then:", ["Check again", "Zero anyway", "Exit"], "Zero anyway");
            bench.Log.Write($"Operator chose {choice}");
            if (choice == "Exit")
                throw new OperatorStopException("Exited by the operator before the zero.");
            if (choice == "Zero anyway")
                return;
        }
    }

    /// <summary>
    /// The CAL, and if the meter refuses it (CAL ERROR, the 1 mW it sees is too far off), what it sees and the likely
    /// cause; the operator can redo the zero and then the CAL (a zero taken in CALIBRATE is the usual cause), retry the
    /// CAL, or exit.
    /// </summary>
    private void CalibrateMeter(IPowerMeterControl control, MeterTest test, MeterStep cal, IDcSource? dc)
    {
        while (true)
        {
            SetCalibrator(cal.Calibrator, dc, "for the CAL", allowSkip: false);
            Settle(cal.SettleSeconds, control, test.Meter!.Display ?? MeterDisplay.Watts);
            try
            {
                Busy("CAL on the 11683A", () => control.Calibrate(cal.ReferenceCalFactorPercent));
                AnsiConsole.MarkupLine($"[green]✓[/] Meter calibrated (reference cal factor {cal.ReferenceCalFactorPercent.ToString("0.#", CultureInfo.InvariantCulture)}%).");
                return;
            }
            catch (InstrumentException ex)
            {
                bench.Log.Write($"CAL failed: {ex.Message}");
                if (!ConsoleUi.Interactive)
                    throw;
                AnsiConsole.MarkupLine($"[red]✗ The CAL failed: {Markup.Escape(ex.Message)}[/]");
                AnsiConsole.MarkupLine(Markup.Escape(CalDiagnosis(control)));
                var choice = ConsoleUi.Choose("What now?", ["Redo the zero, then the CAL", "Retry the CAL", "Exit"], "Exit");
                bench.Log.Write($"Operator chose {choice}");
                if (choice == "Exit")
                    throw new OperatorStopException("Exited by the operator after the CAL failed.", ex);
                _calibratorNow = null;   // ask for the 11683A's switches again, whatever was set
                if (choice.StartsWith("Redo", StringComparison.Ordinal) && test.Zero is { } zero)
                {
                    SetCalibrator(zero.Calibrator, dc, "for the zero", allowSkip: false);
                    Settle(zero.SettleSeconds, control, test.Meter!.Display ?? MeterDisplay.Watts);
                    CheckStandby(control, zero);
                    ZeroMeter(control);
                }
            }
        }
    }

    /// <summary>What the meter sees after a failed CAL, and what that most likely means.</summary>
    private string CalDiagnosis(IPowerMeterControl control)
    {
        double watts;
        try
        {
            watts = ConsoleUi.Busy("Checking what the meter sees", _ => control.Read());
        }
        catch (InstrumentException)
        {
            return "Check the 11683A is at RANGE 1 mW, FUNCTION CALIBRATE.";
        }
        bench.Log.Write($"After the failed CAL the meter read {watts.ToString("R", CultureInfo.InvariantCulture)} W");
        var seen = Si.Format(watts, "W", 4);
        if (watts < 0.1e-3)
            return $"The meter sees almost no power ({seen}): FUNCTION may still be in STANDBY, or the zero was taken with the 11683A in CALIBRATE.";
        if (Math.Abs(watts / 1e-3 - 1) > 0.05)
            return $"The meter sees {seen} instead of about 1 mW. Check RANGE 1 mW and FUNCTION CALIBRATE; if they are right, the zero was "
                + "most likely taken with the 11683A in CALIBRATE: redo the zero.";
        return $"The meter sees about 1 mW ({seen}), so the 11683A looks right: retry the CAL.";
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

        AnsiConsole.Write(SwitchPanel(setting, _calibratorNow, dc, "Set the 11683A" + (purpose is null ? "" : " " + purpose)));
        if ((Sim is null || bench.Walkthrough))
        {
            var prompt = "Press Enter when set" + (allowSkip ? " (S to skip this row, X to exit)" : " (X to exit)") + (bench.Walkthrough ? " [simulator: Enter sets it]" : "");
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

    /// <summary>
    /// The 11683A's switches as a panel, each on its own line; the ones that differ from <paramref name="before"/> are
    /// highlighted and the rest greyed, so what to turn stands out. A hand-set DC calibrator is a line of its own.
    /// </summary>
    private static Panel SwitchPanel(CalibratorSetting setting, CalibratorSetting? before, IDcSource? dc, string header)
    {
        var rows = new List<(string Name, string Value, bool Changed)>
        {
            ("RANGE", RangeSwitch.Get(setting.Range).Label, before?.Range != setting.Range),
            ("FUNCTION", setting.Function.ToString().ToUpperInvariant(), before?.Function != setting.Function),
            ("POLARITY", setting.Polarity.ToString().ToUpperInvariant(), before?.Polarity != setting.Polarity),
            ("REF", setting.Reference == ReferenceSwitch.Ext ? "EXT" : "INT", before?.Reference != setting.Reference),
        };
        if (dc is null && setting.Reference == ReferenceSwitch.Ext)
            rows.Add(("DC calibrator", setting.ExternalVolts is { } v and not 0 ? Si.Format(v, "V", 5) : "0 V, output off", before?.ExternalVolts != setting.ExternalVolts));
        return Highlighted(rows, header);
    }

    /// <summary>A panel of name/value lines, the changed values highlighted and the rest greyed.</summary>
    private static Panel Highlighted(IEnumerable<(string Name, string Value, bool Changed)> rows, string header)
    {
        var list = rows.ToList();
        var grid = new Grid().AddColumn(new GridColumn().PadRight(3)).AddColumn();
        foreach (var (name, value, changed) in list)
            grid.AddRow($"[grey]{Markup.Escape(name)}[/]", changed ? $"[bold yellow]{Markup.Escape(value)}[/]" : $"[grey]{Markup.Escape(value)}[/]");
        // Wide enough for the header, which a panel otherwise cuts to its contents' width.
        var content = list.Max(r => r.Name.Length) + 3 + list.Max(r => r.Value.Length) + 4;
        return new Panel(grid) { Width = Math.Max(content, header.Length + 6) }
            .Header($" {Markup.Escape(header)} ").Border(BoxBorder.Rounded).BorderColor(Color.Blue);
    }

    /// <summary>The connections as they are now; the operator is asked only when they change.</summary>
    private IReadOnlyList<(string Name, string Value)>? _connectionsNow;

    /// <summary>What is connected to what for <paramref name="test"/> on <paramref name="channel"/>.</summary>
    private IReadOnlyList<(string Name, string Value)> Connections(MeterTest test, string channel, IDcSource? dc)
    {
        var input = definition.Channels.Count < 2 ? "sensor input"
            : (definition.BusDriver == "438A" ? "SENSOR " : "CHANNEL ") + channel;
        var rows = new List<(string, string)> { ("11730A cable", $"11683A POWER METER → {definition.Model} {input}") };
        if (test.NeedsH01)
        {
            rows.Add(("DC calibrator", (dc is null ? "HI/LO" : "OUTPUT HI/LO") + " → 11683A REF EXT (Guard, LO floating)"));
            rows.Add(("11683A power", "unplugged (keep the power meter connected)"));
        }
        else
        {
            rows.Add(("11683A power", "plugged in, LINE ON"));
        }
        return rows;
    }

    /// <summary>Shows the connections and waits for them, if they differ from the last test's. False if the operator skips the test.</summary>
    private bool Connect(IReadOnlyList<(string Name, string Value)> connections)
    {
        var before = _connectionsNow;
        if (before is not null && before.SequenceEqual(connections))
            return true;
        AnsiConsole.Write(Highlighted(connections.Select(c => (c.Name, c.Value, before is null || !before.Contains(c))), "Connect"));
        if ((Sim is null || bench.Walkthrough) && !AskGo("Press Enter when connected (S to skip this test, X to exit)"))
            return false;
        _connectionsNow = connections;
        return true;
    }

    /// <summary>One line per row: its name, the reading, its limits.</summary>
    private static void RowLine(MeterTestPoint point, MeterPointResult result, double? reading)
    {
        var label = Markup.Escape(point.Label.PadRight(18));
        AnsiConsole.MarkupLine(reading is { } r
            ? $"  {(result.Pass == true ? "[green]✓[/]" : "[red]✗[/]")} {label} [bold]{Fmt(r, point)}[/] {Markup.Escape(MeterUnits.Display(point.Unit))}  "
              + $"[grey]{Fmt(point.Min, point)} to {Fmt(point.Max, point)}[/]"
            : $"  [yellow]–[/] {label} [yellow]skipped[/]");
    }

    private void ApplyDcSource(CalibratorSetting setting, IDcSource? dc)
    {
        if (dc is null || setting.Reference != ReferenceSwitch.Ext)
            return;
        if (setting.ExternalVolts is { } v and not 0)
        {
            ConsoleUi.Attempt($"Setting the DC calibrator to {Si.Format(v, "V", 5)}", () => Busy($"Setting the DC calibrator to {Si.Format(v, "V", 5)}", () => dc.Output(v)), bench.Log);
            bench.Log.Write($"DC calibrator set to {v.ToString("R", CultureInfo.InvariantCulture)} V");
            AnsiConsole.MarkupLine($"  [grey]DC calibrator set to {Si.Format(v, "V", 5)}[/]");
        }
        else
        {
            ConsoleUi.Attempt("Putting the DC calibrator in Standby", dc.Standby, bench.Log);
            AnsiConsole.MarkupLine("  [grey]DC calibrator in Standby (0 V)[/]");
        }
    }

    /// <summary>
    /// Waits for the 11683A to settle (its STANDBY drift takes up to 60 s), with a countdown and, beside it, what the
    /// meter reads meanwhile in <paramref name="display"/>'s units, so the drift can be seen dying away.
    /// </summary>
    private void Settle(int seconds, IPowerMeterControl control, MeterDisplay display)
    {
        if (seconds <= 0 || Sim is not null)
            return;
        var clock = Stopwatch.StartNew();
        var total = TimeSpan.FromSeconds(seconds);
        TimeSpan Left() => total - clock.Elapsed;
        AnsiConsole.Status().Start($"Letting the 11683A settle: {seconds} s", ctx =>
        {
            using var watch = new MeterWatch(control.Read, Left);
            while (Left() > TimeSpan.Zero)
            {
                ctx.Status($"Letting the 11683A settle: {Math.Ceiling(Left().TotalSeconds):0} s   {Watching(watch, display)}   [grey](Enter to go on now)[/]");
                if (ConsoleUi.Interactive && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
                {
                    bench.Log.Write($"Settling cut short by the operator with {Math.Ceiling(Left().TotalSeconds):0} s to go");
                    break;
                }
                Thread.Sleep(100);
            }
            if (watch.Busy)
                ctx.Status($"Finishing the meter's reading   {Watching(watch, display)}");
            watch.Dispose();
            var (latest, _, count) = watch.Readings;
            bench.Log.Write(watch.Error is { } error
                ? $"While settling the meter stopped answering: {error.Message}"
                : $"While settling the meter read {latest?.ToString("R", CultureInfo.InvariantCulture) ?? "nothing"} {display} last ({count} readings)");
        });
    }

    /// <summary>The meter's last reading and the one before it, for the settling countdown.</summary>
    private static string Watching(MeterWatch watch, MeterDisplay display)
    {
        if (watch.Error is not null)
            return "[yellow]meter not answering[/]";
        var (latest, previous, _) = watch.Readings;
        if (latest is not { } now)
            return "[grey]meter: reading…[/]";
        return $"meter [bold]{Markup.Escape(Shown(now, display))}[/]" + (previous is { } was ? $" [grey](was {Markup.Escape(Shown(was, display))})[/]" : "");
    }

    private static string Shown(double value, MeterDisplay display) => display switch
    {
        MeterDisplay.Watts => Si.Format(value, "W", 4),
        MeterDisplay.Dbm => value.ToString("0.000", CultureInfo.InvariantCulture) + " dBm",
        _ => value.ToString("0.000", CultureInfo.InvariantCulture) + " dB",
    };

    private static void Busy(string what, Action action) => ConsoleUi.Busy(what, _ =>
    {
        action();
        return 0;
    });

    /// <summary>
    /// Readings until two in a row agree to within a fifth of the half-tolerance (the meter's own settling covers its
    /// filter, not the 11683A's), and their mean; at most eight.
    /// </summary>
    private double? ReadSettled(IPowerMeterControl control, MeterTestPoint point, MeterDisplay display)
    {
        var band = Math.Max((point.Max - point.Min) / 2 * 0.2, 1e-12);
        // Under a spinner: an E441x reading with a 512 filter takes about 26 s, and nothing else would show it is busy.
        var (value, settled) = ConsoleUi.Busy($"Measuring {point.Label}", status =>
        {
            double Next(int n)
            {
                status($"Measuring {point.Label}: reading {n}");
                var reading = control.Read();
                return display == MeterDisplay.Watts ? MeterUnits.FromWatts(point.Unit, reading)!.Value : reading;
            }

            var previous = Next(1);
            for (var i = 2; i <= 8; i++)
            {
                var next = Next(i);
                if (Math.Abs(next - previous) <= band)
                    return ((previous + next) / 2, true);
                previous = next;
            }
            return (previous, false);
        });
        if (!settled)
        {
            bench.Log.Write($"'{point.Label}': readings did not agree to within {band} {point.Unit}; the last is used");
            AnsiConsole.MarkupLine("  [yellow]The readings kept changing; the last one is recorded.[/]");
        }
        return value;
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
        ConsoleUi.BeforePrompt();
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
        ConsoleUi.Answered();
        bench.Log.Write($"CAL ADJ set by the operator for {target}");
    }

    /// <summary>A CAL FACTOR switch the bus cannot set (436A): the operator turns it.</summary>
    private void SetCalFactorSwitch(double percent)
    {
        AnsiConsole.MarkupLine($"Set the meter's [bold]CAL FACTOR[/] switch to [bold]{percent.ToString("0.#", CultureInfo.InvariantCulture)}%[/].");
        bench.Step("Press Enter when set", _ => _simMeter!.CalFactorPercent = percent);
        _calFactorSwitch = percent;
    }

    /// <summary>Enter to go on; X to exit the run.</summary>
    /// <exception cref="OperatorStopException">The operator typed X.</exception>
    private static bool Ask(string message)
    {
        if (!ConsoleUi.Interactive)
            return true;
        var text = ConsoleUi.Ask(new TextPrompt<string>($"[blue]{Markup.Escape(message)}[/]").AllowEmpty()).Trim();
        if (text.Equals("x", StringComparison.OrdinalIgnoreCase))
            throw new OperatorStopException("Exited by the operator.");
        return true;
    }

    /// <summary>
    /// On a clearing console, the test's screen redrawn with its table so far: the rows measured, with PASS or FAIL,
    /// and the rows to come. A redirected console gets one line per row instead.
    /// </summary>
    private void ShowProgress(MeterTest test, string channel, IReadOnlyList<MeterPointResult> done)
    {
        if (!screens.Clearing)
            return;
        screens.Redraw();
        var all = done.Concat(test.Points.Skip(done.Count).Select(p => new MeterPointResult(p, test.SourceOf(p), null, false))).ToList();
        AnsiConsole.Write(MeterReport.Table(new MeterTestResult(test.Id, ShortTitle(test), test.Source, channel, all), pendingFrom: done.Count));
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

    /// <summary>Enter to go on (true), S to skip (false), X to exit the run: what is measured so far is saved.</summary>
    /// <exception cref="OperatorStopException">The operator typed X.</exception>
    private static bool AskGo(string message)
    {
        if (!ConsoleUi.Interactive)
            return true;
        var text = ConsoleUi.Ask(new TextPrompt<string>($"[blue]{Markup.Escape(message)}[/]").AllowEmpty()).Trim();
        if (text.Equals("x", StringComparison.OrdinalIgnoreCase))
            throw new OperatorStopException("Exited by the operator.");
        return !text.Equals("s", StringComparison.OrdinalIgnoreCase);
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
        var text = ConsoleUi.Ask(prompt
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
