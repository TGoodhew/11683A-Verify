using System.Globalization;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;

namespace Verify11683A.Measurement;

/// <summary>An 11683A RANGE position the detector can recognise, and the value expected at the rear output.</summary>
public sealed record ExpectedValue(string RangeId, double Nominal)
{
    public RangePosition Range => RangeSwitch.Get(RangeId);
}

public enum DetectionKind
{
    /// <summary>The reading is close to one expected value: that is the RANGE setting.</summary>
    Match,

    /// <summary>About zero (DCV: STANDBY or REF EXT; ohms: shorted leads).</summary>
    NearZero,

    /// <summary>Negative (DCV: POLARITY REVERSE; ohms: the output is live).</summary>
    Reversed,

    /// <summary>Not close to any expected value.</summary>
    OutOfFamily,

    /// <summary>Close to more than one expected value.</summary>
    Ambiguous,

    /// <summary>The DVM overloaded (ohms: open circuit).</summary>
    Overload,
}

/// <summary>What a scout reading says about the 11683A's switches. Anything but a match is a warning, never a result.</summary>
public sealed record Detection(DetectionKind Kind, double Reading, ExpectedValue? Candidate, double Deviation, string Message)
{
    public bool IsMatch => Kind == DetectionKind.Match;
}

/// <summary>
/// Recognises the RANGE setting from a quick reading by matching it to the nearest expected value
/// (Part 1 voltages or Part 2 resistances). The "3" ranges are √10 steps, so neighbouring values are about
/// 3.16× apart and a ±10% tolerance is never ambiguous while still recognising a range that fails its limits.
/// </summary>
public sealed class RangeDetector
{
    public const double DefaultTolerance = 0.10;

    /// <summary>A grounded output reads a few µV. The 3 µW range in CALIBRATE gives about 0.46 mV, well above this.</summary>
    public const double ZeroThresholdVolts = 50e-6;

    /// <summary>Well below the smallest Part 2 value (31.6 Ω).</summary>
    public const double ZeroThresholdOhms = 1.0;

    private readonly IReadOnlyList<ExpectedValue> _candidates;

    /// <param name="scope">What the candidates belong to, e.g. "Part 1", for telling the operator a RANGE position isn't used.</param>
    public RangeDetector(DvmFunction function, IReadOnlyList<ExpectedValue> candidates, double tolerance = DefaultTolerance, string scope = "this test")
    {
        if (candidates.Count == 0)
            throw new ArgumentException("No expected values to match.", nameof(candidates));
        if (candidates.Any(c => c.Nominal <= 0))
            throw new ArgumentException("Expected values must be positive.", nameof(candidates));
        Function = function;
        _candidates = candidates;
        Tolerance = tolerance;
        Scope = scope;
    }

    public string Scope { get; }

    public DvmFunction Function { get; }

    public IReadOnlyList<ExpectedValue> Candidates => _candidates;

    /// <summary>The largest fractional departure from a nominal value that still counts as a match.</summary>
    public double Tolerance { get; }

    public double ZeroThreshold => Function == DvmFunction.DcVoltage ? ZeroThresholdVolts : ZeroThresholdOhms;

    public Detection Detect(double reading, bool overload)
    {
        var unit = Function.Unit();
        var volts = Function == DvmFunction.DcVoltage;

        if (overload)
            return new Detection(DetectionKind.Overload, reading, null, double.NaN, volts
                ? "the DVM overloaded on its highest range"
                : "open circuit: check the 4-wire cable and its connection at DC REFERENCE OUTPUT");

        if (Math.Abs(reading) <= ZeroThreshold)
            return new Detection(DetectionKind.NearZero, reading, null, double.NaN, volts
                ? $"about 0 V ({Si.Format(reading, unit, 3)}): FUNCTION is in STANDBY, or REF is on EXT"
                : $"about 0 Ω ({Si.Format(reading, unit, 3)}): the leads are shorted or not on the 11683A");

        var magnitude = Math.Abs(reading);
        var ranked = _candidates
            .Select(c => (Candidate: c, Distance: Math.Abs(Math.Log(magnitude / c.Nominal))))
            .OrderBy(x => x.Distance)
            .ToList();
        var nearest = ranked[0].Candidate;
        var deviation = magnitude / nearest.Nominal - 1;
        var limit = Math.Log(1 + Tolerance);
        var within = ranked.Count(x => x.Distance <= limit);

        if (reading < 0)
            return new Detection(DetectionKind.Reversed, reading, within == 1 ? nearest : null, deviation, volts
                ? $"negative ({Si.Format(reading, unit, 5)}): POLARITY is REVERSE"
                : $"negative resistance ({Si.Format(reading, unit, 5)}): the output is live; set FUNCTION to STANDBY");

        if (within > 1)
            return new Detection(DetectionKind.Ambiguous, reading, null, deviation,
                $"{Si.Format(reading, unit, 5)} is close to both {ranked[0].Candidate.Range} and {ranked[1].Candidate.Range}");

        if (within == 0)
            return new Detection(DetectionKind.OutOfFamily, reading, null, deviation,
                OtherPosition(magnitude, unit, nearest)
                ?? $"{Si.Format(reading, unit, 5)} is not close to any expected value; the nearest is {nearest.Range} "
                + $"({Si.Format(nearest.Nominal, unit, 5)}, {deviation.ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture)}). Check RANGE"
                + (volts ? "." : ", and that FUNCTION is in STANDBY."));

        return new Detection(DetectionKind.Match, reading, nearest, deviation, $"RANGE {nearest.Range}");
    }

    /// <summary>
    /// A reading a whole number of RANGE steps from the nearest candidate is another knob position: name it and say which
    /// way to turn. The positions are √10 apart and the output scales with them. Null if the reading isn't on a step.
    /// </summary>
    private string? OtherPosition(double magnitude, string unit, ExpectedValue nearest)
    {
        var step = Math.Log(Math.Sqrt(10));
        var steps = (int)Math.Round(Math.Log(magnitude / nearest.Nominal) / step);
        var index = RangeSwitch.IndexOf(nearest.RangeId) + steps;
        if (steps == 0 || index < 0 || index >= RangeSwitch.Positions.Count
            || Math.Abs(Math.Log(magnitude / nearest.Nominal) - steps * step) > Math.Log(1 + Tolerance))
            return null;

        var on = RangeSwitch.Positions[index];
        var used = _candidates.Select(c => RangeSwitch.IndexOf(c.RangeId)).ToList();
        var turn = _candidates.Count == 1 ? $"turn RANGE to {nearest.Range}"
            : index < used.Min() ? $"turn RANGE UP to {RangeSwitch.Positions[used.Min()]} or higher"
            : index > used.Max() ? $"turn RANGE DOWN to {RangeSwitch.Positions[used.Max()]} or lower"
            : $"turn RANGE to {nearest.Range}";
        return $"RANGE is on {on} ({Si.Format(magnitude, unit, 5)}), which {Scope} doesn't use: {turn}";
    }
}
