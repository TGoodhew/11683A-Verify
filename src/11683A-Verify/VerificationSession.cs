using System.Globalization;
using System.Reflection;
using Spectre.Console;
using Spectre.Console.Rendering;
using Verify11683A.App.Reports;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Instruments.Measurement;
using Verify11683A.Measurement;
using Verify11683A.Results;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

namespace Verify11683A.App;

/// <summary>
/// What every test command shares: the bench, the DVM identified and reset, the limits, spec table and
/// uncertainty convention, and the loop that measures a point with the operator.
/// </summary>
public sealed class VerificationSession : IDisposable
{
    private VerificationSession(Bench bench, AppConfig config, CommandLine options, DvmIdentity identity)
    {
        Bench = bench;
        Config = config;
        Options = options;
        Identity = identity;
        DvmDetails = config.Dvm.Details(identity);
        Specs = DvmSpecTable.LoadForModel(bench.Dvm.Model);
        Limits = CalibratorLimits.LoadDefault();
        Convention = config.Uncertainty.Convention;
        AmbientC = options.AmbientC;
        Started = bench.Clock.Now;
    }

    public Bench Bench { get; }
    public AppConfig Config { get; }
    public CommandLine Options { get; }
    public IDvm Dvm => Bench.Dvm;
    public DvmIdentity Identity { get; }
    public DvmDetails DvmDetails { get; }
    public DvmSpecTable Specs { get; }
    public CalibratorLimits Limits { get; }
    public UncertaintyConvention Convention { get; }
    public SpecPeriod Period => Config.Dvm.Period;
    public double? AmbientC { get; set; }

    /// <summary>How long the 11683A had been warming up when the test started, if known.</summary>
    public double? WarmupMinutes { get; set; }

    public double? DvmWarmupMinutes { get; set; }

    public bool WarmupCutShort { get; set; }
    public DateTimeOffset Started { get; }
    public SessionLog Log => Bench.Log;

    public static VerificationSession Start(AppConfig config, CommandLine options, string name)
    {
        var bench = Bench.Open(config, options, name);
        try
        {
            var identity = bench.Dvm.Identify();
            bench.Log.Write($"Identity: {identity.Raw}");
            bench.Dvm.Reset();
            var session = new VerificationSession(bench, config, options, identity);
            session.WriteHeader(name);
            return session;
        }
        catch
        {
            bench.Dispose();
            throw;
        }
    }

    private void WriteHeader(string name)
    {
        AnsiConsole.Write(new Rule($"[bold]11683A verification[/] — {Markup.Escape(name)}"
            + (Bench.IsSimulation ? $" [yellow](simulated: {Markup.Escape(Bench.Simulator!.Scenario.Name)})[/]" : "")).LeftJustified());
        var grid = new Grid().AddColumn().AddColumn();
        var c = Config.Calibrator;
        grid.AddRow("[grey]Unit[/]", Markup.Escape($"{c.Model} s/n {c.Serial}" + (c.Options == "" ? "" : $", option {c.Options}")));
        grid.AddRow("[grey]DVM[/]", Markup.Escape($"{DvmDetails.Manufacturer} {DvmDetails.Model}"
            + (DvmDetails.Serial == "" ? "" : $" s/n {DvmDetails.Serial}") + (DvmDetails.Firmware == "" ? "" : $", fw {DvmDetails.Firmware}")
            + $" at {Dvm.ResourceName}"));
        grid.AddRow("[grey]DVM calibration[/]", ConsoleUi.CalibrationText(DvmDetails, DateOnly.FromDateTime(Bench.Clock.Now.Date)));
        grid.AddRow("[grey]Spec[/]", Markup.Escape($"{Specs.Model} {Period.Key()} column; U = {Convention.CoverageFactor.ToString("0.#", CultureInfo.InvariantCulture)}·u_c, "
            + $"ratios {Convention.RatioCombination}"));
        if (AmbientC is { } a)
            grid.AddRow("[grey]Ambient[/]", Markup.Escape(a.ToString("0.0", CultureInfo.InvariantCulture) + " °C"));
        if (Config.Operator != "")
            grid.AddRow("[grey]Operator[/]", Markup.Escape(Config.Operator));
        AnsiConsole.Write(grid);
        if (DvmDetails.StateOn(DateOnly.FromDateTime(Bench.Clock.Now.Date)) == CalibrationState.PastDue)
            AnsiConsole.MarkupLine("[red]The DVM's calibration is past due: the results are not traceable.[/]");
        AnsiConsole.WriteLine();
        Log.Write($"Unit {c.Model} s/n {c.Serial} {c.Options}; DVM {DvmDetails}; spec {Specs.Model} {Period.Key()}; ambient {AmbientC?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
    }

    public PointAcquirer Acquirer(RangeDetector detector, Func<string?>? interlock = null) =>
        new(Dvm, detector, Config.Measurement.Settling(detector.Function), Config.Measurement.Nplc, Config.Measurement.ReadingsPerPoint)
        {
            Log = Log.Write,
            Interlock = interlock,
        };

    /// <summary>
    /// Measures whichever point the 11683A is set to, talking the operator through warnings and a point that will
    /// not settle. Returns null when the operator chooses to stop.
    /// </summary>
    /// <param name="view">
    /// The live view to show while measuring, given the status line and the range detected so far. On a
    /// redirected console a status line is printed instead.
    /// </param>
    public MeasuredPoint? MeasureNext(PointAcquirer acquirer, double? startRange = null, Func<string, ExpectedValue?, IRenderable>? view = null)
    {
        var allowUnsettled = false;
        while (true)
        {
            var acquisition = view is not null && !Console.IsOutputRedirected
                ? AcquireLive(acquirer, allowUnsettled, startRange, view)
                : AcquireWithStatus(acquirer, allowUnsettled, startRange);
            switch (acquisition.Outcome)
            {
                case AcquisitionOutcome.Measured:
                    return ToMeasured(acquirer.Function, acquisition);

                case AcquisitionOutcome.NotDetected:
                    AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(Capitalise(acquisition.Message))}.[/]");
                    if (Bench.IsSimulation || ConsoleUi.Choose("Fix the switches, then:", ["Try again", "Stop"], "Stop") == "Stop")
                        return null;
                    break;

                case AcquisitionOutcome.NotSettled:
                    AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(Capitalise(acquisition.Message))}.[/]");
                    var choice = ConsoleUi.Choose("The output is still moving:",
                        ["Keep waiting", "Measure anyway (flagged as not settled)", "Stop"], "Measure anyway (flagged as not settled)");
                    if (choice == "Stop")
                        return null;
                    allowUnsettled = choice.StartsWith("Measure", StringComparison.Ordinal);
                    break;

                case AcquisitionOutcome.Blocked:
                    AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(Capitalise(acquisition.Message))}.[/]");
                    if (Bench.IsSimulation || ConsoleUi.Choose("Ohms not applied. Set FUNCTION to STANDBY, then:", ["Try again", "Stop"], "Stop") == "Stop")
                        return null;
                    break;

                default:
                    AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(Capitalise(acquisition.Message))}.[/]");
                    if (Bench.IsSimulation || ConsoleUi.Choose("Leave the knobs alone while a point is measured, then:", ["Try again", "Stop"], "Stop") == "Stop")
                        return null;
                    break;
            }
        }
    }

    private PointAcquisition AcquireWithStatus(PointAcquirer acquirer, bool allowUnsettled, double? startRange)
    {
        var unit = acquirer.Function.Unit();
        PointAcquisition result = null!;
        AnsiConsole.Status().Start("Detecting the range", ctx =>
        {
            acquirer.Progress = p => ctx.Status(StatusText(p, unit));
            result = acquirer.Acquire(allowUnsettled, startRange);
        });
        acquirer.Progress = null;
        return result;
    }

    private PointAcquisition AcquireLive(PointAcquirer acquirer, bool allowUnsettled, double? startRange, Func<string, ExpectedValue?, IRenderable> view)
    {
        var unit = acquirer.Function.Unit();
        PointAcquisition result = null!;
        AnsiConsole.Live(view("Detecting the range", null)).AutoClear(true).Start(ctx =>
        {
            acquirer.Progress = p =>
            {
                ctx.UpdateTarget(view(StatusText(p, unit), p.Point));
                ctx.Refresh();
            };
            result = acquirer.Acquire(allowUnsettled, startRange);
        });
        acquirer.Progress = null;
        return result;
    }

    private static string StatusText(AcquisitionProgress p, string unit) => p.Phase switch
    {
        AcquisitionPhase.Scouting => $"Detecting the range: {Si.Format(p.Reading.Value, unit, 5)}",
        AcquisitionPhase.Settling => $"{p.Point?.Range}: waiting for the output to settle, reading {p.Done}: {Si.Format(p.Reading.Value, unit, 7)}",
        _ => $"{p.Point?.Range}: reading {p.Done} of {p.Total}: {Si.Format(p.Reading.Value, unit, 7)}",
    } + (p.Attempt > 1 ? $" (attempt {p.Attempt})" : "");

    /// <summary>Everything this session produced, ready to save.</summary>
    public VerificationRun CreateRun(Part1Result? part1, Part2Result? part2, IReadOnlyList<string>? notes = null)
    {
        var m = Config.Measurement;
        var c = Config.Calibrator;
        return new VerificationRun
        {
            AppVersion = typeof(VerificationSession).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
            Started = Started,
            Finished = Bench.Clock.Now,
            Company = Config.Company,
            Operator = Config.Operator,
            Unit = new UnitUnderTest(c.Model, c.Serial, c.Options),
            Dvm = new DvmRecord(DvmDetails, Dvm.ResourceName, Identity.Raw, DvmDetails.StateOn(DateOnly.FromDateTime(Started.Date))),
            Conditions = new TestConditions(AmbientC, WarmupMinutes, Bench.Simulator?.Scenario.Name, DvmWarmupMinutes, WarmupCutShort),
            Settings = new RunSettings(m.Nplc, m.ReadingsPerPoint, m.LineFrequencyHz, m.SettlingBandPpm, m.SettlingHoldSeconds,
                m.SettlingTimeoutSeconds, Period, Convention),
            Limits = DataFileVersion.Of(DataFiles.Resolve(DataFiles.LimitsFileName), Limits.Source),
            SpecTable = DataFileVersion.Of(DataFiles.Resolve(DataFiles.SpecFileName(Specs.Model)), Specs.Source),
            Part1 = part1,
            Part2 = part2,
            Notes = notes ?? [],
        };
    }

    /// <summary>
    /// Asks for operator notes, then saves the run as JSON in the results directory with the PDF report and a copy of
    /// the session log beside it.
    /// </summary>
    public string Save(VerificationRun run)
    {
        run = run with { Notes = [.. run.Notes, .. AskNotes()] };
        var path = ResultsFile.Save(run, Config.ResultsDirectory);
        Log.Write($"Results saved to {Path.GetFullPath(path)}");
        try
        {
            var pdf = PdfReport.WriteBeside(run, path);
            Log.Write($"Report written to {Path.GetFullPath(pdf)}");
        }
        catch (Exception ex)
        {
            Log.Write($"Report failed: {ex}");
            AnsiConsole.MarkupLine($"[red]The PDF report could not be written: {Markup.Escape(ex.Message)}[/] "
                + "[grey]The results are saved; rebuild it later with --regen-pdf.[/]");
        }
        Log.SaveCopy(Path.ChangeExtension(path, ".log"));
        AnsiConsole.MarkupLine($"[grey]Results: {Markup.Escape(Path.GetFullPath(path))}, with the PDF report and session log beside it[/]");
        return path;
    }

    /// <summary>
    /// After a passing run, offers to write 11683A-actuals.json for the power meter app, from the results file exactly
    /// as saved.
    /// </summary>
    public void OfferActualsExport(string resultsPath)
    {
        var json = File.ReadAllText(resultsPath);
        var run = ResultsFile.Deserialize(json);
        if (CalibratorActuals.WhyNotExportable(run) is { } reason)
        {
            Log.Write($"Actuals not offered: {reason}");
            return;
        }
        if (!ConsoleUi.Confirm($"Write the measured values to {CalibratorActuals.FileName} for the power meter app?"))
            return;
        var path = CalibratorActuals.From(run, Path.GetFileName(resultsPath), json).Save(Config.ResultsDirectory);
        Log.Write($"Actuals written to {Path.GetFullPath(path)}");
        AnsiConsole.MarkupLine($"[grey]Actuals: {Markup.Escape(Path.GetFullPath(path))}"
            + (run.Conditions.SimulationScenario is null ? "" : " [yellow](marked simulated)[/]") + "[/]");
    }

    private static IReadOnlyList<string> AskNotes()
    {
        if (!ConsoleUi.Interactive)
            return [];
        var text = AnsiConsole.Prompt(new TextPrompt<string>("Operator notes for the report [grey](Enter for none)[/]:").AllowEmpty());
        return text.Trim() == "" ? [] : [text.Trim()];
    }

    public static MeasuredPoint ToMeasured(DvmFunction function, PointAcquisition a)
    {
        var configuration = a.Configuration ?? throw new InvalidOperationException("A measured point has a configuration.");
        return new MeasuredPoint(a.Point!.RangeId, function, configuration.Range, configuration.Nplc, configuration.InputResistance,
            a.Readings.Select(r => new TimedValue(r.Value, r.Time)).ToList(), a.Settled, a.SettlingTime.TotalSeconds, a.Attempts);
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public void Dispose() => Bench.Dispose();
}
