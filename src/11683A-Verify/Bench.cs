using Spectre.Console;
using Verify11683A.Calibrator;
using Verify11683A.Instruments.DcSource;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Instruments.PowerMeter;
using Verify11683A.Instruments.Simulation;
using Verify11683A.Instruments.Visa;

namespace Verify11683A.App;

/// <summary>The instruments for one session: a real DVM on GPIB, or the simulated 11683A and DVM.</summary>
public sealed class Bench : IDisposable
{
    private Bench(IDvm dvm, Simulated11683A? simulator, IClock clock, SessionLog log)
    {
        Dvm = dvm;
        Simulator = simulator;
        Clock = clock;
        Log = log;
    }

    public IDvm Dvm { get; }

    /// <summary>The simulated 11683A whose knobs the app turns in --simulate mode; null on the real bench.</summary>
    public Simulated11683A? Simulator { get; }

    public IClock Clock { get; }
    public SessionLog Log { get; }
    public bool IsSimulation => Simulator is not null;

    /// <summary>
    /// Simulation with a person at the console: every bench prompt is shown, and the simulated knobs turn when the
    /// operator answers it. Scripted (redirected) simulation runs straight through instead.
    /// </summary>
    public bool Walkthrough => IsSimulation && ConsoleUi.Interactive;

    /// <summary>
    /// A bench step: on the bench and in a walkthrough the operator is prompted; in simulation the 11683A is then set
    /// as the prompt asked.
    /// </summary>
    public void Step(string prompt, Action<Simulated11683A>? simulate = null)
    {
        if (!IsSimulation || Walkthrough)
            ConsoleUi.Prompt(Walkthrough ? $"{prompt} [simulator: Enter sets it]" : prompt);
        if (Simulator is { } sim)
            simulate?.Invoke(sim);
    }

    /// <summary>
    /// Before the first measurement: the DVM must be on its FRONT terminals (<see cref="TerminalsCheck"/>). Until it is,
    /// the operator is told and the switch is read again after Enter; in simulation, Enter presses the simulated button.
    /// Each reading of the switch goes to the session log.
    /// </summary>
    /// <exception cref="DvmException">On the REAR terminals with no operator at the console to wait for.</exception>
    public DvmTerminals CheckTerminals()
    {
        var terminals = TerminalsCheck.Ensure(Dvm, problem =>
        {
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(problem)}[/]");
            if (!IsSimulation && Console.IsInputRedirected)
                throw new DvmException($"{problem} No operator at the console to wait for.");
            Step("Press Enter when FRONT is selected", _ =>
            {
                if (Dvm is not SimulatedDvm simulated)
                    return;
                simulated.Terminals = DvmTerminals.Front;
                if (!Walkthrough)
                    AnsiConsole.MarkupLine("[grey]Simulator: Front/Rear → FRONT[/]");
            });
            return true;
        }, Log.Write);

        AnsiConsole.MarkupLine(terminals == DvmTerminals.Front
            ? $"[green]✓[/] The {Markup.Escape(Dvm.Model)} is reading its FRONT terminals."
            : $"[yellow]⚠ {Markup.Escape(TerminalsCheck.Problem(Dvm.Model, terminals)!)}[/]");
        return terminals;
    }

    public static Bench Open(AppConfig config, CommandLine options, string sessionName)
    {
        var log = new SessionLog(Path.Combine(config.ResultsDirectory, "logs"), sessionName);
        try
        {
            if (options.SimulateScenario is { } scenarioName)
            {
                var scenario = SimulationScenario.Get(scenarioName);
                var clock = new SimulatedClock(new DateTimeOffset(DateTime.Now));
                var simulator = new Simulated11683A(CalibratorLimits.LoadDefault(), scenario, clock);
                var dvm = new SimulatedDvm(simulator, clock, config.Measurement.LineFrequencyHz)
                {
                    Pacing = TimeSpan.FromMilliseconds(40),
                };
                log.Write($"Simulation: scenario '{scenario.Name}': {scenario.Description}");
                return new Bench(dvm, simulator, clock, log);
            }

            var model = config.Dvm.Model.ToUpperInvariant();
            if (model is not ("34401A" or "3458A"))
                throw new OperatorException($"Unsupported DVM model '{config.Dvm.Model}' in the settings. Use 34401A or 3458A.");
            log.Write($"Opening {config.Dvm.Model} at {config.Dvm.Resource}, timeout {config.Dvm.TimeoutMs} ms");
            var visa = VisaSession.Open(config.Dvm.Resource, config.Dvm.TimeoutMs);
            log.Write($"VISA implementation: {visa.Implementation}");
            var session = new LoggingVisaSession(visa, log.Write);
            IDvm real = model == "34401A" ? new Hp34401A(session) : new Hp3458A(session);
            return new Bench(real, null, SystemClock.Instance, log);
        }
        catch
        {
            log.Dispose();
            throw;
        }
    }

    /// <summary>The Part 4 power meter: simulated, typed in by the operator, or on the bus.</summary>
    public IPowerMeter OpenPowerMeter(PowerMeterConfig config)
    {
        if (Simulator is { } sim)
            return new SimulatedPowerMeter(sim);
        if (config.Model.Equals("manual", StringComparison.OrdinalIgnoreCase))
            return new TypedPowerMeter(config.Model);
        Log.Write($"Opening power meter {config.Model} at {config.Resource}, timeout {config.TimeoutMs} ms");
        var visa = VisaSession.Open(config.Resource, config.TimeoutMs);
        try
        {
            return PowerMeters.Open(config.Model, new LoggingVisaSession(visa, Log.Write), config.Channel);
        }
        catch
        {
            visa.Dispose();
            throw;
        }
    }

    /// <summary>Emergency Standby for each bus DC calibrator opened in this session, released when the bench closes.</summary>
    private readonly List<IDisposable> _emergency = [];

    /// <summary>
    /// The DC calibrator on REF EXT: on the bus (or simulated), identified and in Standby; null when the operator sets
    /// it by hand. Without a DVM watching it (the meter tests), <paramref name="useDivider"/> is off.
    /// </summary>
    public IDcSource? OpenDcSource(DcSourceConfig config, bool useDivider)
    {
        if (DcSources.IsManual(config.Model))
            return null;
        IDcSource source;
        if (Simulator is { } sim)
        {
            source = new SimulatedDcSource(sim, useDivider);
        }
        else
        {
            Log.Write($"Opening DC calibrator {config.Model} at {config.Resource}, timeout {config.TimeoutMs} ms");
            var visa = VisaSession.Open(config.Resource, config.TimeoutMs);
            try
            {
                source = DcSources.Open(config.Model, new LoggingVisaSession(visa, Log.Write), useDivider);
            }
            catch
            {
                visa.Dispose();
                throw;
            }
            if (source is Fluke5440B fluke)
            {
                fluke.StandbyFailed = message =>
                {
                    Log.Write($"DC calibrator Standby failed on closing: {message}");
                    AnsiConsole.Write(new Panel(new Markup($"[bold red]The {Markup.Escape(config.Model)} did not confirm Standby.[/] It may still be in "
                        + "OPERATE: press its STBY key now.")).BorderColor(Color.Red));
                };
                // Ctrl+C runs no cleanup: the handler puts the calibrator in Standby on a session of its own.
                var resource = config.Resource;
                _emergency.Add(EmergencyStop.Register($"{config.Model} at {resource} to Standby", () =>
                {
                    using var emergency = VisaSession.Open(resource, 3000);
                    emergency.Write("STBY");
                }));
            }
        }
        try
        {
            source.Identify();
        }
        catch
        {
            source.Dispose();
            throw;
        }
        Log.Write($"DC calibrator: {source.Description}" + (Simulator is null ? $" at {config.Resource}" : ""));
        if (Simulator is null)
            OfferInternalCalibration(source, config.Model);
        return source;
    }

    /// <summary>
    /// At a console, offers the DC calibrator's internal calibration before it is used: on the bench (2026-10-21) the
    /// 5440B's output read 33 to 80 µV low until it was run, enough to fail the lowest REF EXT rows. Its leads must be
    /// off the front terminals meanwhile, so this comes before any connections are asked for.
    /// </summary>
    private void OfferInternalCalibration(IDcSource source, string model)
    {
        if (!source.HasInternalCalibration || !ConsoleUi.Interactive)
            return;
        AnsiConsole.MarkupLine($"[grey]The {Markup.Escape(model)}'s internal calibration corrects its zero and gain drift. Its manual asks for it daily, "
            + "after the 2 h warm-up; it takes about 6 minutes. Without it the lowest REF EXT voltages can be tens of µV off.[/]");
        if (!ConsoleUi.Confirm($"Run the {model}'s internal calibration now?", defaultValue: true))
        {
            Log.Write($"{model} internal calibration offered and declined");
            return;
        }
        ConsoleUi.Prompt($"Disconnect every lead from the {model}'s front terminals, then press Enter");
        var started = DateTime.Now;
        var done = ConsoleUi.Attempt($"{model} internal calibration", () => ConsoleUi.Busy($"{model} internal calibration", status =>
        {
            source.RunInternalCalibration(step => status($"{model} internal calibration: step {step}"));
            return true;
        }), Log, canSkip: true);
        if (!done)
        {
            Log.Write($"{model} internal calibration skipped after an error");
            return;
        }
        var minutes = (DateTime.Now - started).TotalMinutes;
        Log.Write($"{model} internal calibration done in {minutes:0.0} min: {source.Description}");
        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(model)} internal calibration done ({minutes:0} min). Its leads go back on when the connections are shown.");
    }

    /// <summary>Closes everything even if a session fails to close, so the error that got here is the one reported.</summary>
    public void Dispose()
    {
        foreach (var registration in _emergency)
            registration.Dispose();
        try
        {
            Dvm.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write($"Closing the DVM session failed: {ex.Message}");
        }
        Log.Dispose();
    }
}
