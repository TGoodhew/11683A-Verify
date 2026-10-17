namespace Verify11683A.Measurement;

/// <summary>A reading and when it was taken.</summary>
public readonly record struct TimedValue(double Value, DateTimeOffset Time);

/// <summary>When a point counts as settled, and when a settled point counts as disturbed.</summary>
public sealed record SettlingCriteria
{
    public const double DefaultBandPpm = 20;
    public const double DefaultFloorVolts = 2e-6;
    public const double DefaultFloorOhms = 2e-3;

    /// <summary>Half-width of the stability band, in ppm of the reading.</summary>
    public double BandPpm { get; init; } = DefaultBandPpm;

    /// <summary>The smallest half-width, in V or Ω, so low readings are not held to a band below the DVM's noise.</summary>
    public double Floor { get; init; }

    /// <summary>How long the readings must stay inside the band.</summary>
    public TimeSpan HoldTime { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The fewest readings that can show the output is holding still.</summary>
    public int MinReadings { get; init; } = 3;

    /// <summary>Give up waiting after this long.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Once settled, a reading further than this many σ (of the settled window) from the recent readings is an
    /// interruption, if that is wider than the band. It keeps a point accepted while still noisy from being
    /// abandoned on noise alone.
    /// </summary>
    public double InterruptSigmas { get; init; } = 5;

    public static SettlingCriteria For(DvmFunction function) => new()
    {
        Floor = function == DvmFunction.DcVoltage ? DefaultFloorVolts : DefaultFloorOhms,
    };

    /// <summary>The band half-width around a value: max(ppm × |value|, floor).</summary>
    public double Band(double value) => Math.Max(Math.Abs(value) * BandPpm * 1e-6, Floor);
}

public enum SettlingStatus
{
    Settling,
    Settled,
    TimedOut,
}

/// <summary>
/// Waits for the output to settle after a knob is turned. Settled means that every reading over the last
/// <see cref="SettlingCriteria.HoldTime"/> (and at least <see cref="SettlingCriteria.MinReadings"/> of them) lies
/// within ± the band of their mean.
/// </summary>
public sealed class SettlingDetector(SettlingCriteria criteria)
{
    private readonly List<TimedValue> _window = [];
    private DateTimeOffset? _started;

    public SettlingCriteria Criteria => criteria;

    public SettlingStatus Status { get; private set; }

    /// <summary>The readings that decide the status: the last hold time's worth, plus the one just before it.</summary>
    public IReadOnlyList<TimedValue> Window => _window;

    /// <summary>Readings seen since the detector started or was reset.</summary>
    public int Count { get; private set; }

    public TimeSpan Elapsed => _window.Count == 0 || _started is not { } start ? TimeSpan.Zero : _window[^1].Time - start;

    public double Mean => _window.Count == 0 ? double.NaN : _window.Average(r => r.Value);

    /// <summary>The largest departure of a window reading from the window mean.</summary>
    public double Spread
    {
        get
        {
            if (_window.Count == 0)
                return double.NaN;
            var mean = Mean;
            return _window.Max(r => Math.Abs(r.Value - mean));
        }
    }

    public double Band => criteria.Band(Mean);

    public SettlingStatus Add(double value, DateTimeOffset time)
    {
        _started ??= time;
        _window.Add(new TimedValue(value, time));
        Count++;

        // Keep the newest reading at or before the start of the hold time, so the window always spans it, and
        // never fewer than the minimum number of readings.
        var holdStart = time - criteria.HoldTime;
        while (_window.Count > criteria.MinReadings && _window[1].Time <= holdStart)
            _window.RemoveAt(0);

        var held = _window[0].Time <= holdStart && _window.Count >= criteria.MinReadings;
        if (held && Spread <= Band)
            return Status = SettlingStatus.Settled;
        if (time - _started >= criteria.Timeout)
            return Status = SettlingStatus.TimedOut;
        return Status = SettlingStatus.Settling;
    }

    public void Reset()
    {
        _window.Clear();
        _started = null;
        Count = 0;
        Status = SettlingStatus.Settling;
    }
}

/// <summary>
/// Watches the readings of a point once it has settled. A reading that leaves the band around the most recent
/// readings means a knob moved (or something came loose), so the point is abandoned.
/// </summary>
/// <remarks>
/// The reference is the mean of the last <see cref="SettlingCriteria.MinReadings"/> readings, not the settled mean,
/// so slow drift over a long point is not mistaken for a step.
/// </remarks>
public sealed class InterruptionMonitor
{
    private readonly SettlingCriteria _criteria;
    private readonly List<double> _recent;
    private readonly double _noiseAllowance;

    public InterruptionMonitor(SettlingCriteria criteria, IEnumerable<double> settledWindow)
    {
        _criteria = criteria;
        var window = settledWindow.ToList();
        if (window.Count == 0)
            throw new ArgumentException("The settled window is empty.", nameof(settledWindow));
        _noiseAllowance = window.Count > 1 ? criteria.InterruptSigmas * ReadingStatistics.Of(window).StandardDeviation : 0;
        _recent = window.TakeLast(criteria.MinReadings).ToList();
    }

    public double Reference => _recent.Average();

    /// <summary>How far a reading may be from <see cref="Reference"/>.</summary>
    public double Threshold => Math.Max(_criteria.Band(Reference), _noiseAllowance);

    /// <summary>True if the reading is an interruption. Accepted readings join the reference.</summary>
    public bool IsInterruption(double value)
    {
        if (Math.Abs(value - Reference) > Threshold)
            return true;
        _recent.Add(value);
        if (_recent.Count > _criteria.MinReadings)
            _recent.RemoveAt(0);
        return false;
    }
}
