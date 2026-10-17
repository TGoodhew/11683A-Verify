namespace Verify11683A;

/// <summary>Time source. The simulated bench advances a <see cref="SimulatedClock"/> by the real reading time.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset Now => DateTimeOffset.Now;
}

public sealed class SimulatedClock(DateTimeOffset start) : IClock
{
    public SimulatedClock() : this(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero)) { }

    public DateTimeOffset Now { get; private set; } = start;

    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        Now += by;
    }
}
