using System.Diagnostics;

namespace Verify11683A.App.Procedures;

/// <summary>
/// Reads a power meter over and over on a thread of its own, so the countdown while the 11683A settles can show what
/// the meter sees. A reading starts only while the time left is longer than the last one took, so a slow filter (an
/// E441x at 512 takes about 26 s) does not hold the countdown up. A failed reading ends the watch quietly: the step
/// that follows meets the fault and reports it. The readings are kept with their times, so the drift can be judged.
/// </summary>
public sealed class MeterWatch : IDisposable
{
    private readonly Func<double> _read;
    private readonly Func<TimeSpan> _timeLeft;
    private readonly Thread _thread;
    private readonly Lock _lock = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly List<(TimeSpan At, double Value)> _history = [];
    private volatile bool _stopping;

    public MeterWatch(Func<double> read, Func<TimeSpan> timeLeft)
    {
        _read = read;
        _timeLeft = timeLeft;
        _thread = new Thread(Watch) { IsBackground = true, Name = "Meter watch" };
        _thread.Start();
    }

    /// <summary>The last reading, the one before it, and how many there have been.</summary>
    public (double? Latest, double? Previous, int Count) Readings
    {
        get
        {
            lock (_lock)
                return (_history.Count > 0 ? _history[^1].Value : null, _history.Count > 1 ? _history[^2].Value : null, _history.Count);
        }
    }

    /// <summary>The window the drift is judged over: long enough that a reading's noise does not pass for drift.</summary>
    public static readonly TimeSpan DriftWindow = TimeSpan.FromMinutes(1);

    /// <summary>How fast the readings are moving, per minute, over the last <see cref="DriftWindow"/>; null until it is covered.</summary>
    public double? DriftPerMinute
    {
        get
        {
            lock (_lock)
                return Drift(_history, DriftWindow);
        }
    }

    /// <summary>
    /// The least-squares slope of the readings in the last <paramref name="window"/>, per minute: null until there are
    /// three readings spanning at least two-thirds of it. A fit, not the last two readings, so the noise averages out.
    /// </summary>
    public static double? Drift(IReadOnlyList<(TimeSpan At, double Value)> readings, TimeSpan window)
    {
        if (readings.Count == 0)
            return null;
        var from = readings[^1].At - window;
        var recent = readings.Where(r => r.At >= from).ToList();
        if (recent.Count < 3 || recent[^1].At - recent[0].At < window * 2 / 3)
            return null;
        var meanT = recent.Average(r => r.At.TotalMinutes);
        var meanV = recent.Average(r => r.Value);
        var sxx = recent.Sum(r => Math.Pow(r.At.TotalMinutes - meanT, 2));
        return recent.Sum(r => (r.At.TotalMinutes - meanT) * (r.Value - meanV)) / sxx;
    }

    /// <summary>What ended the watch, if a reading failed.</summary>
    public Exception? Error { get; private set; }

    /// <summary>True while a reading is under way.</summary>
    public bool Busy { get; private set; }

    /// <summary>True once the watch has ended of itself: too little time left for a reading, or a reading failed.</summary>
    public bool Done => !_thread.IsAlive;

    private void Watch()
    {
        var last = TimeSpan.Zero;
        while (!_stopping && _timeLeft() > last)
        {
            var started = Stopwatch.GetTimestamp();
            Busy = true;
            try
            {
                var value = _read();
                lock (_lock)
                    _history.Add((Stopwatch.GetElapsedTime(_started), value));
            }
            catch (Exception ex)
            {
                Error = ex;
                return;
            }
            finally
            {
                Busy = false;
            }
            last = Stopwatch.GetElapsedTime(started);
        }
    }

    /// <summary>Stops the watch, waiting for a reading under way: the meter has to be free for the next step.</summary>
    public void Dispose()
    {
        _stopping = true;
        _thread.Join();
    }
}
