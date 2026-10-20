using System.Globalization;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Measurement;

namespace Verify11683A.Instruments.Simulation;

/// <summary>
/// A simulated 34401A measuring a <see cref="Simulated11683A"/>. It has the 34401A's ranges, 20% overrange and
/// test currents, quantises to the integration time's resolution, and moves a <see cref="SimulatedClock"/>
/// forward by the real reading time, so settling and drift behave as they would on the bench.
/// </summary>
public sealed class SimulatedDvm(Simulated11683A source, IClock clock, double lineHz = 60, int seed = 34401) : IDvm
{
    private const double Overrange = 1.2;
    private const double OverloadValue = 9.9e37;

    private static readonly double[] DcVoltageRanges = [0.1, 1, 10, 100, 1000];
    private static readonly double[] FourWireRanges = [100, 1e3, 1e4, 1e5, 1e6, 1e7];

    /// <summary>34401A ohms test current per range (Service Guide p. 12).</summary>
    private static readonly Dictionary<double, double> TestCurrent = new()
    {
        [100] = 1e-3, [1e3] = 1e-3, [1e4] = 100e-6, [1e5] = 10e-6, [1e6] = 5e-6, [1e7] = 500e-9,
    };

    private readonly Random _random = new(seed);

    /// <summary>Real time to wait per reading, so the console feels like a bench. Zero for tests.</summary>
    public TimeSpan Pacing { get; set; }

    public Simulated11683A Source => source;

    /// <summary>The front-panel Front/Rear switch, from the scenario (Front unless it says otherwise).</summary>
    public DvmTerminals Terminals { get; set; } = source.Scenario.DvmTerminals;

    /// <summary>What the open rear input reads on DC volts: about 11 mV, as seen on the bench.</summary>
    public const double FloatingVolts = 11e-3;

    /// <summary>Uses the 34401A's spec table.</summary>
    public string Model => "34401A";

    public string ResourceName => "SIM::34401A";

    public DvmConfiguration? Configuration { get; private set; }

    public DvmIdentity Identify() =>
        new("SIMULATED", "34401A", "0", "sim", "SIMULATED,34401A,0,sim");

    public void Reset() => Configuration = null;

    public IReadOnlyList<double> Ranges(DvmFunction function) => function switch
    {
        DvmFunction.DcVoltage => DcVoltageRanges,
        DvmFunction.FourWireResistance => FourWireRanges,
        _ => throw new ArgumentOutOfRangeException(nameof(function)),
    };

    public DvmConfiguration Configure(DvmFunction function, double range, double nplc)
    {
        if (!Ranges(function).Contains(range))
            throw new ArgumentOutOfRangeException(nameof(range), $"34401A has no {function} range {range}.");
        var highZ = function == DvmFunction.DcVoltage && range <= Hp34401A.HighImpedanceMaxRange;
        var inputResistance = function switch
        {
            DvmFunction.DcVoltage when highZ => ">10 GΩ",
            DvmFunction.DcVoltage => "10 MΩ ±1%",
            _ => "n/a (4-wire ohms)",
        };
        return Configuration = new DvmConfiguration(function, range, nplc, AutoZero: true, highZ, inputResistance);
    }

    private int _readings;

    public DvmReading Read()
    {
        var config = Configuration ?? throw new InvalidOperationException("Configure the simulated DVM before reading it.");
        if (++_readings == source.Scenario.DvmTimeoutAtReading)
            throw new Visa.InstrumentTimeoutException("SIMULATED DVM did not respond within 20 s while waiting for the reply to 'READ?' (the dvm-timeout scenario).");

        var duration = ReadingTime.Estimate(config.Nplc, lineHz, config.AutoZero);
        if (clock is SimulatedClock simulated)
            simulated.Advance(duration);
        if (Pacing > TimeSpan.Zero)
            Thread.Sleep(Pacing);

        // On the rear terminals the cable is not connected: a floating input reads some millivolts and ohms overload.
        var value = Terminals != DvmTerminals.Front
            ? config.Function == DvmFunction.DcVoltage ? FloatingVolts : double.PositiveInfinity
            : config.Function == DvmFunction.DcVoltage
                ? source.MeasureVolts()
                : source.MeasureFourWireOhms(TestCurrent[config.Range]);

        if (Math.Abs(value) > config.Range * Overrange)
            return new DvmReading(Math.Sign(value) * OverloadValue, Overload: true, clock.Now);

        // The meter's own noise and resolution: 6½ digits at NPLC ≥ 10, 5½ at NPLC ≥ 1, 4½ below.
        var resolution = config.Range * (config.Nplc >= 10 ? 1e-6 : config.Nplc >= 1 ? 1e-5 : 1e-4);
        value += Gaussian() * resolution * 0.3;
        value = Math.Round(value / resolution) * resolution;
        return new DvmReading(value, Overload: false, clock.Now);
    }

    public IReadOnlyList<string> ReadErrors() => [];

    public IReadOnlyList<KeyValuePair<string, string>> ReadBackSettings()
    {
        var c = Configuration;
        if (c is null)
            return [new("CONF?", "(not configured)")];
        var f = c.Function == DvmFunction.DcVoltage ? "VOLT" : "FRES";
        return
        [
            new("CONF?", $"\"{f} {c.Range.ToString("+0.000000E+00;-0.000000E+00", CultureInfo.InvariantCulture)}\""),
            new("NPLC?", c.Nplc.ToString(CultureInfo.InvariantCulture)),
            new("ZERO:AUTO?", "1"),
            new("INP:IMP:AUTO?", c.HighImpedance ? "1" : "0"),
            new("ROUT:TERM?", Terminals == DvmTerminals.Front ? "FRON" : "REAR"),
        ];
    }

    public DvmTerminals ReadTerminals() => Terminals;

    public void Dispose() { }

    private double Gaussian()
    {
        var u1 = 1.0 - _random.NextDouble();
        var u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
