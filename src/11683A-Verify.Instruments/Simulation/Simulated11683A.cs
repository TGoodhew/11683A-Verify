using Verify11683A.Calibrator;

namespace Verify11683A.Instruments.Simulation;

/// <summary>
/// A simulated 11683A as seen at its rear DC REFERENCE OUTPUT. The operator turns its knobs from the
/// console; the simulated DVM measures it.
/// </summary>
/// <remarks>
/// Part 1 and Part 2 ranges take their nominal values from the manual's limits. The other positions are
/// illustrative only: CALIBRATE voltages below 300 µW scale with the true full-scale power (the "3" ranges are
/// √10 multiples, e.g. 3.162 µW), and STANDBY resistances from 1 mW up follow the ≈10 MΩ/W pattern of the
/// Part 2 limits. STANDBY grounds the output; REF EXT with nothing on the REF EXT input gives about 0 V.
/// REVERSE is assumed to reverse the rear output as well (Part 1 checks the 100 mW reading is positive).
/// </remarks>
public sealed class Simulated11683A
{
    private readonly CalibratorLimits _limits;
    private readonly IClock _clock;
    private readonly Random _random;
    private readonly DateTimeOffset _start;

    private State _state = new(RangeSwitch.Get("100mW"), FunctionSwitch.Standby, PolaritySwitch.Normal, ReferenceSwitch.Int);
    private State _previous;
    private DateTimeOffset _changedAt;

    public Simulated11683A(CalibratorLimits limits, SimulationScenario scenario, IClock clock, int seed = 11683)
    {
        _limits = limits;
        Scenario = scenario;
        SupplyScale = scenario.SupplyScale;
        _clock = clock;
        _random = new Random(seed);
        _start = clock.Now;
        _previous = _state;
        _changedAt = clock.Now - TimeSpan.FromHours(1);
    }

    public SimulationScenario Scenario { get; }

    /// <summary>The A2R1 supply setting as a scale on every CALIBRATE voltage. Turned during the Part 3 assist.</summary>
    public double SupplyScale { get; set; }

    public RangePosition Range => _state.Range;
    public FunctionSwitch Function => _state.Function;
    public PolaritySwitch Polarity => _state.Polarity;
    public ReferenceSwitch Reference => _state.Reference;

    public void SetRange(string rangeId) => Change(_state with { Range = RangeSwitch.Get(rangeId) });
    public void SetFunction(FunctionSwitch function) => Change(_state with { Function = function });
    public void SetPolarity(PolaritySwitch polarity) => Change(_state with { Polarity = polarity });
    public void SetReference(ReferenceSwitch reference) => Change(_state with { Reference = reference });

    /// <summary>The settled, noise-free CALIBRATE voltage of a range, including the scenario's errors.</summary>
    public double SettledVolts(RangePosition range)
    {
        double nominal;
        if (_limits.Part1.Points.Any(p => p.Range == range.Id))
        {
            nominal = _limits.NominalPart1Volts(range.Id);
        }
        else
        {
            var lowest = _limits.Part1.Points.Select(p => RangeSwitch.Get(p.Range)).MinBy(r => r.NominalWatts)!;
            nominal = _limits.NominalPart1Volts(lowest.Id) * TrueFullScaleWatts(range) / TrueFullScaleWatts(lowest);
        }
        var error = Scenario.VoltageErrors.TryGetValue(range.Id, out var e) ? e : 0.0;
        return nominal * SupplyScale * (1 + error);
    }

    /// <summary>The settled, noise-free STANDBY four-wire resistance of a range, including the scenario's errors.</summary>
    public double SettledOhms(RangePosition range)
    {
        var point = _limits.Part2.Points.FirstOrDefault(p => p.Range == range.Id);
        var nominal = point?.OhmsMid ?? 1e7 * TrueFullScaleWatts(range);
        var error = Scenario.OhmsErrors.TryGetValue(range.Id, out var e) ? e : 0.0;
        return nominal * (1 + error);
    }

    /// <summary>What a high-impedance DVM sees at the DC REFERENCE OUTPUT now: settling, drift and noise included.</summary>
    public double MeasureVolts()
    {
        var value = Blend(OutputVolts(_previous), OutputVolts(_state));
        return value * Drift() + Noise(value, Scenario.NoiseFloorVolts);
    }

    /// <summary>
    /// What a four-wire ohmmeter sees at the DC REFERENCE OUTPUT, driving <paramref name="testCurrentAmps"/>.
    /// With FUNCTION in CALIBRATE the live output adds V/I, so the reading is far from any Part 2 value.
    /// </summary>
    public double MeasureFourWireOhms(double testCurrentAmps)
    {
        var value = Blend(Ohms(_previous, testCurrentAmps), Ohms(_state, testCurrentAmps));
        return value * Drift() + Noise(value, Scenario.NoiseFloorOhms);
    }

    /// <summary>The power a 3 µW, 30 µW … position really delivers at full scale: a √10 step, e.g. 3.162 µW.</summary>
    public static double TrueFullScaleWatts(RangePosition range) =>
        Math.Pow(10, Math.Round(Math.Log10(range.NominalWatts) * 2) / 2);

    private double OutputVolts(State s)
    {
        if (s.Function == FunctionSwitch.Standby || s.Reference == ReferenceSwitch.Ext)
            return 0.4e-6;   // grounded output: a thermal EMF of a few tenths of a µV
        var v = SettledVolts(s.Range);
        return s.Polarity == PolaritySwitch.Reverse ? -v : v;
    }

    private double Ohms(State s, double testCurrentAmps) =>
        SettledOhms(s.Range) + OutputVolts(s) / testCurrentAmps;

    private void Change(State next)
    {
        if (next == _state)
            return;
        _previous = _state;
        _state = next;
        _changedAt = _clock.Now;
    }

    /// <summary>
    /// The switch itself steps almost at once (τ = 0.3 s); what remains is a small thermal tail of
    /// <see cref="SimulationScenario.SettlingTailPpm"/> decaying with <see cref="SimulationScenario.SettlingTauSeconds"/>.
    /// </summary>
    private double Blend(double from, double to)
    {
        const double switchTauSeconds = 0.3;
        var seconds = (_clock.Now - _changedAt).TotalSeconds;
        var step = (from - to) * Math.Exp(-seconds / switchTauSeconds);
        var tail = to * Scenario.SettlingTailPpm * 1e-6 * Math.Exp(-seconds / Scenario.SettlingTauSeconds);
        return to + step + tail;
    }

    private double Drift() => 1 + Scenario.DriftPpmPerMinute * 1e-6 * (_clock.Now - _start).TotalMinutes;

    private double Noise(double value, double floor)
    {
        var sigma = Math.Abs(value) * Scenario.NoisePpm * 1e-6 + floor;
        // Box-Muller
        var u1 = 1.0 - _random.NextDouble();
        var u2 = _random.NextDouble();
        return sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private sealed record State(RangePosition Range, FunctionSwitch Function, PolaritySwitch Polarity, ReferenceSwitch Reference);
}
