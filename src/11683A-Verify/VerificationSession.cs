using System.Globalization;
using Spectre.Console;
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
        grid.AddRow("[grey]Ambient[/]", AmbientC is { } a ? Markup.Escape(a.ToString("0.0", CultureInfo.InvariantCulture) + " °C") : "[grey]not entered (no temperature coefficient added)[/]");
        if (Config.Operator != "")
            grid.AddRow("[grey]Operator[/]", Markup.Escape(Config.Operator));
        AnsiConsole.Write(grid);
        if (DvmDetails.StateOn(DateOnly.FromDateTime(Bench.Clock.Now.Date)) == CalibrationState.PastDue)
            AnsiConsole.MarkupLine("[red]The DVM's calibration is past due: the results are not traceable.[/]");
        AnsiConsole.WriteLine();
        Log.Write($"Unit {c.Model} s/n {c.Serial} {c.Options}; DVM {DvmDetails}; spec {Specs.Model} {Period.Key()}; ambient {AmbientC?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
    }

    public PointAcquirer Acquirer(RangeDetector detector) =>
        new(Dvm, detector, Config.Measurement.Settling(detector.Function), Config.Measurement.Nplc, Config.Measurement.ReadingsPerPoint)
        {
            Log = Log.Write,
        };

    /// <summary>
    /// Measures whichever point the 11683A is set to, talking the operator through warnings and a point that will
    /// not settle. Returns null when the operator chooses to stop.
    /// </summary>
    /// <param name="beforeRetry">Run before every retry (Part 2 re-checks that the output is not live).</param>
    public MeasuredPoint? MeasureNext(PointAcquirer acquirer, double? startRange = null, Func<bool>? beforeRetry = null)
    {
        var allowUnsettled = false;
        while (true)
        {
            var acquisition = AcquireWithStatus(acquirer, allowUnsettled, startRange);
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

                default:
                    AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(Capitalise(acquisition.Message))}.[/]");
                    if (Bench.IsSimulation || ConsoleUi.Choose("Leave the knobs alone while a point is measured, then:", ["Try again", "Stop"], "Stop") == "Stop")
                        return null;
                    break;
            }

            if (beforeRetry is not null && !beforeRetry())
                return null;
        }
    }

    private PointAcquisition AcquireWithStatus(PointAcquirer acquirer, bool allowUnsettled, double? startRange)
    {
        var unit = acquirer.Function.Unit();
        PointAcquisition result = null!;
        AnsiConsole.Status().Start("Detecting the range", ctx =>
        {
            acquirer.Progress = p => ctx.Status(p.Phase switch
            {
                AcquisitionPhase.Scouting => $"Detecting the range: {Si.Format(p.Reading.Value, unit, 5)}",
                AcquisitionPhase.Settling => $"Waiting for the output to settle: reading {p.Done}, {Si.Format(p.Reading.Value, unit, 7)}",
                _ => $"Reading {p.Done} of {p.Total}: {Si.Format(p.Reading.Value, unit, 7)}",
            } + (p.Attempt > 1 ? $" (attempt {p.Attempt})" : ""));
            result = acquirer.Acquire(allowUnsettled, startRange);
        });
        acquirer.Progress = null;
        return result;
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
