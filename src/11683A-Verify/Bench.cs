using Verify11683A.Calibrator;
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

            log.Write($"Opening {config.Dvm.Model} at {config.Dvm.Resource}, timeout {config.Dvm.TimeoutMs} ms");
            var visa = VisaSession.Open(config.Dvm.Resource, config.Dvm.TimeoutMs);
            log.Write($"VISA implementation: {visa.Implementation}");
            var session = new LoggingVisaSession(visa, log.Write);
            IDvm real = config.Dvm.Model.ToUpperInvariant() switch
            {
                "34401A" => new Hp34401A(session),
                "3458A" => new Hp3458A(session),
                _ => throw new ArgumentException($"Unsupported DVM model '{config.Dvm.Model}'. Use 34401A or 3458A."),
            };
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
        return PowerMeters.Open(config.Model, new LoggingVisaSession(visa, Log.Write), config.Channel);
    }

    public void Dispose()
    {
        Dvm.Dispose();
        Log.Dispose();
    }
}
