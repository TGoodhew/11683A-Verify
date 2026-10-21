using System.Diagnostics;

namespace Verify11683A.App.Procedures;

/// <summary>
/// Reads a power meter over and over on a thread of its own, so the countdown while the 11683A settles can show what
/// the meter sees. A reading starts only while the time left is longer than the last one took, so a slow filter (an
/// E441x at 512 takes about 26 s) does not hold the countdown up. A failed reading ends the watch quietly: the step
/// that follows meets the fault and reports it.
/// </summary>
public sealed class MeterWatch : IDisposable
{
    private readonly Func<double> _read;
    private readonly Func<TimeSpan> _timeLeft;
    private readonly Thread _thread;
    private readonly Lock _lock = new();
    private volatile bool _stopping;
    private double? _latest;
    private double? _previous;
    private int _count;

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
                return (_latest, _previous, _count);
        }
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
                {
                    _previous = _latest;
                    _latest = value;
                    _count++;
                }
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
