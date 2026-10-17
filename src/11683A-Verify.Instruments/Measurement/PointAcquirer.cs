using System.Globalization;
using Verify11683A.Formatting;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Measurement;

namespace Verify11683A.Instruments.Measurement;

public enum AcquisitionPhase
{
    Scouting,
    Settling,
    Measuring,
}

/// <summary>Reported after every reading, for progress displays.</summary>
public sealed record AcquisitionProgress(AcquisitionPhase Phase, DvmReading Reading, int Done, int Total, int Attempt);

public enum AcquisitionOutcome
{
    /// <summary>Detected, settled (or accepted unsettled) and every reading taken.</summary>
    Measured,

    /// <summary>The scout reading did not match an expected value. See <see cref="PointAcquisition.Detection"/>.</summary>
    NotDetected,

    /// <summary>The output did not settle before the timeout.</summary>
    NotSettled,

    /// <summary>The output kept moving: every attempt was abandoned.</summary>
    Interrupted,
}

/// <summary>The outcome of measuring one point. Only <see cref="AcquisitionOutcome.Measured"/> carries a result.</summary>
public sealed record PointAcquisition(
    AcquisitionOutcome Outcome,
    Detection Detection,
    DvmConfiguration? Configuration,
    IReadOnlyList<DvmReading> Readings,
    bool Settled,
    TimeSpan SettlingTime,
    int Attempts,
    string Message)
{
    public ExpectedValue? Point => Detection.Candidate;

    public ReadingStatistics? Statistics => Readings.Count == 0 ? null : ReadingStatistics.Of(Readings.Select(r => r.Value).ToList());
}

/// <summary>
/// Measures whichever expected point the 11683A is set to: a quick scout reading detects the RANGE setting, the
/// DVM goes to the fixed range for that value, the readings must settle, then N readings are taken. If a reading
/// leaves the band partway through (a knob was turned), the point is abandoned and detected again.
/// </summary>
public sealed class PointAcquirer
{
    private readonly IDvm _dvm;
    private readonly RangeDetector _detector;
    private readonly SettlingCriteria _criteria;
    private readonly IReadOnlyList<double> _scoutRanges;

    public PointAcquirer(IDvm dvm, RangeDetector detector, SettlingCriteria criteria, double nplc, int readings)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(readings, 1);
        _dvm = dvm;
        _detector = detector;
        _criteria = criteria;
        Nplc = nplc;
        ReadingsPerPoint = readings;

        // Scout only over the ranges the expected values need; the lowest still shows "about zero".
        var ranges = dvm.Ranges(detector.Function);
        var picks = detector.Candidates.Select(c => RangePicker.Pick(ranges, c.Nominal)).ToList();
        _scoutRanges = ranges.Where(r => r >= picks.Min() && r <= picks.Max()).Order().ToList();
    }

    public DvmFunction Function => _detector.Function;

    public double Nplc { get; }

    public int ReadingsPerPoint { get; }

    /// <summary>Integration time for scout readings: fast, with enough resolution to tell the ranges apart.</summary>
    public double ScoutNplc { get; init; } = 1;

    /// <summary>How many times a point may be abandoned and restarted before giving up.</summary>
    public int MaxAttempts { get; init; } = 5;

    public Action<string>? Log { get; init; }

    public Action<AcquisitionProgress>? Progress { get; init; }

    /// <summary>Scout readings must agree this closely across <see cref="ScoutHoldTime"/>.</summary>
    public double ScoutAgreement { get; init; } = 0.01;

    /// <summary>
    /// How long the scout readings must agree, so neither a switch caught mid-transition nor a brief pause on the
    /// way to another position is detected. If one is, the settling readings no longer match it and the point is
    /// detected again.
    /// </summary>
    public TimeSpan ScoutHoldTime { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The most scout readings before detecting on whatever the last one was.</summary>
    public int ScoutMaxReadings { get; init; } = 60;

    /// <summary>
    /// Scouts for the RANGE setting: starts on <paramref name="startRange"/> (default the highest range needed),
    /// steps up on overload or down when the reading is near the bottom of the range, and reads until the readings
    /// have agreed for <see cref="ScoutHoldTime"/>.
    /// </summary>
    public (Detection Detection, DvmReading Reading, double Range) Scout(double? startRange = null, int attempt = 1)
    {
        var index = startRange is { } s && _scoutRanges.Contains(s) ? IndexOf(s) : _scoutRanges.Count - 1;
        double? configured = null;
        DvmReading? runStart = null;
        DvmReading reading = default;
        for (var n = 0; n < ScoutMaxReadings; n++)
        {
            var range = _scoutRanges[index];
            if (configured != range)
            {
                _dvm.Configure(Function, range, ScoutNplc);
                configured = range;
                runStart = null;
            }
            reading = _dvm.Read();
            Log?.Invoke($"scout {Function} {range}: {reading.Value}{(reading.Overload ? " (overload)" : "")}");
            Progress?.Invoke(new AcquisitionProgress(AcquisitionPhase.Scouting, reading, 0, 0, attempt));

            var magnitude = Math.Abs(reading.Value);
            if ((reading.Overload || magnitude > range * RangePicker.NearTopFraction) && index < _scoutRanges.Count - 1)
                index++;
            else if (!reading.Overload && magnitude < range * RangePicker.NearBottomFraction && index > 0)
                index--;
            else if (runStart is { } first && Agree(first, reading))
            {
                if (reading.Time - first.Time >= ScoutHoldTime)
                    break;
            }
            else
                runStart = reading;
        }

        var detection = _detector.Detect(reading.Value, reading.Overload);
        Log?.Invoke($"detected: {detection.Kind}: {detection.Message}");
        return (detection, reading, _scoutRanges[index]);
    }

    /// <summary>
    /// Measures the point the 11683A is set to. With <paramref name="allowUnsettled"/>, a point that times out
    /// waiting to settle is measured anyway and flagged as not settled.
    /// </summary>
    public PointAcquisition Acquire(bool allowUnsettled = false, double? startRange = null)
    {
        Detection detection = null!;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            (detection, _, var scoutRange) = Scout(startRange, attempt);
            startRange = scoutRange;
            if (!detection.IsMatch)
                return new PointAcquisition(AcquisitionOutcome.NotDetected, detection, null, [], false, TimeSpan.Zero, attempt, detection.Message);

            var expected = detection.Candidate!;
            var range = RangePicker.Pick(_dvm.Ranges(Function), expected.Nominal);
            var configuration = _dvm.Configure(Function, range, Nplc);
            Log?.Invoke($"{expected.Range}: {Function} range {range}, NPLC {Nplc}");

            // Settle.
            var settling = new SettlingDetector(_criteria);
            var moved = false;
            while (true)
            {
                var r = _dvm.Read();
                Progress?.Invoke(new AcquisitionProgress(AcquisitionPhase.Settling, r, settling.Count + 1, 0, attempt));
                if (Moved(r, expected, range))
                {
                    Log?.Invoke($"settling reading {r.Value}{(r.Overload ? " (overload)" : "")} no longer matches {expected.Range}: detecting again");
                    moved = true;
                    break;
                }
                var status = settling.Add(r.Value, r.Time);
                Log?.Invoke($"settling {settling.Count}: {r.Value} (spread {settling.Spread:G3}, band {settling.Band:G3}) {status}");
                if (status == SettlingStatus.Settled)
                    break;
                if (status == SettlingStatus.TimedOut)
                {
                    if (!allowUnsettled)
                        return new PointAcquisition(AcquisitionOutcome.NotSettled, detection, configuration, [], false, settling.Elapsed, attempt,
                            $"{expected.Range} did not settle within {Seconds(_criteria.Timeout)}: the readings spread "
                            + $"±{Si.Format(settling.Spread, Function.Unit(), 2)} against a band of ±{Si.Format(settling.Band, Function.Unit(), 2)}");
                    Log?.Invoke($"{expected.Range}: not settled after {Seconds(settling.Elapsed)}; measuring anyway");
                    break;
                }
            }
            if (moved)
                continue;

            // Measure.
            var settled = settling.Status == SettlingStatus.Settled;
            var monitor = new InterruptionMonitor(_criteria, settling.Window.Select(w => w.Value));
            var readings = new List<DvmReading>(ReadingsPerPoint);
            while (readings.Count < ReadingsPerPoint)
            {
                var r = _dvm.Read();
                Progress?.Invoke(new AcquisitionProgress(AcquisitionPhase.Measuring, r, readings.Count + 1, ReadingsPerPoint, attempt));
                if (Moved(r, expected, range) || monitor.IsInterruption(r.Value))
                {
                    Log?.Invoke($"reading {readings.Count + 1}: {r.Value} left the band (±{monitor.Threshold:G3} around {monitor.Reference}): abandoning the point");
                    moved = true;
                    break;
                }
                Log?.Invoke($"reading {readings.Count + 1}: {r.Value}");
                readings.Add(r);
            }
            if (moved)
                continue;

            return new PointAcquisition(AcquisitionOutcome.Measured, detection, configuration, readings, settled, settling.Elapsed, attempt,
                settled ? $"{expected.Range}: settled in {Seconds(settling.Elapsed)}" : $"{expected.Range}: measured without settling");
        }

        return new PointAcquisition(AcquisitionOutcome.Interrupted, detection, null, [], false, TimeSpan.Zero, MaxAttempts,
            $"the output kept moving; abandoned {MaxAttempts} times");
    }

    /// <summary>A reading that no longer belongs to the detected point: overload, wrong range, or a different value.</summary>
    private bool Moved(DvmReading reading, ExpectedValue expected, double range)
    {
        if (reading.Overload || RangePicker.ShouldRerange(_dvm.Ranges(Function), range, reading.Value))
            return true;
        var again = _detector.Detect(reading.Value, reading.Overload);
        return !again.IsMatch || again.Candidate != expected;
    }

    private bool Agree(DvmReading a, DvmReading b)
    {
        if (a.Overload || b.Overload)
            return a.Overload && b.Overload;
        return Math.Abs(a.Value - b.Value) <= ScoutAgreement * Math.Max(Math.Abs(a.Value), Math.Abs(b.Value)) + _detector.ZeroThreshold;
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s";

    private int IndexOf(double range) => _scoutRanges.ToList().IndexOf(range);
}
