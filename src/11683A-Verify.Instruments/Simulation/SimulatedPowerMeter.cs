using Verify11683A.Instruments.PowerMeter;

namespace Verify11683A.Instruments.Simulation;

/// <summary>
/// A simulated power meter on the 11683A's POWER METER output. Zero and CAL behave like the front-panel keys: zero
/// stores the present input as the offset, CAL scales the present input to read 1 mW.
/// </summary>
public sealed class SimulatedPowerMeter(Simulated11683A source, int seed = 4418) : IPowerMeter
{
    private readonly Random _random = new(seed);
    private double _offset;
    private double _gain = 1;

    public string Description { get; init; } = "SIMULATED E4418B";
    public bool OverBus => true;

    /// <summary>Reading noise, as a fraction of the reading (1 sigma).</summary>
    public double NoiseFraction { get; init; } = 20e-6;

    public void Identify() { }

    /// <summary>The front-panel CAL FACTOR setting: the reading is divided by it.</summary>
    public double CalFactorPercent { get; set; } = 100;

    /// <summary>
    /// How this model reads each 11683A range relative to its true full-scale power (e.g. the E4418B reads the
    /// 100 mW position as 101.3 mW, per its Service Guide). 1 where not given.
    /// </summary>
    public Func<string, double>? RangeResponse { get; set; }

    public void Zero() => _offset = source.OutputPowerWatts();

    public void Cal() => _gain = 1e-3 / (source.OutputPowerWatts() - _offset);

    /// <summary>What the meter displays, without noise.</summary>
    public double DisplayedWatts()
    {
        var response = source.Function == Calibrator.FunctionSwitch.Calibrate && source.Reference == Calibrator.ReferenceSwitch.Int
            ? RangeResponse?.Invoke(source.Range.Id) ?? 1
            : 1;
        return (source.OutputPowerWatts() - _offset) * _gain * response / (CalFactorPercent / 100);
    }

    public double ReadWatts()
    {
        var value = DisplayedWatts();
        var u1 = 1.0 - _random.NextDouble();
        var u2 = _random.NextDouble();
        return value * (1 + NoiseFraction * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
    }

    public void Dispose() { }
}
