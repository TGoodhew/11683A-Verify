namespace Verify11683A.Measurement;

/// <summary>How long an integrating DVM reading takes, for timeouts, progress and the simulated clock.</summary>
public static class ReadingTime
{
    /// <summary>
    /// Integration time × 2 with autozero (a zero conversion follows each reading) × 2 with offset-compensated
    /// ohms, plus a fixed allowance for triggering and bus transfer.
    /// </summary>
    public static TimeSpan Estimate(double nplc, double lineHz, bool autoZero, bool offsetCompensated = false)
    {
        var seconds = nplc / lineHz;
        if (autoZero) seconds *= 2;
        if (offsetCompensated) seconds *= 2;
        return TimeSpan.FromSeconds(seconds + 0.05);
    }
}
