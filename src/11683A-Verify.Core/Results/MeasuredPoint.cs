using Verify11683A.Measurement;
using Verify11683A.Uncertainty;

namespace Verify11683A.Results;

/// <summary>The raw record of one point: how it was measured and every reading, as saved in the results file.</summary>
public sealed record MeasuredPoint(
    string RangeId,
    DvmFunction Function,
    double DvmRange,
    double Nplc,
    string InputResistance,
    IReadOnlyList<TimedValue> Readings,
    bool Settled,
    double SettlingSeconds,
    int Attempts)
{
    public ReadingStatistics Statistics => ReadingStatistics.Of(Readings.Select(r => r.Value).ToList());
}

public static class Verdicts
{
    /// <summary>The worst of several verdicts: FAIL, then INDETERMINATE, then PASS. INFO only if there is nothing else.</summary>
    public static Verdict Combine(IEnumerable<Verdict> verdicts)
    {
        var all = verdicts.ToList();
        if (all.Contains(Verdict.Fail)) return Verdict.Fail;
        if (all.Contains(Verdict.Indeterminate)) return Verdict.Indeterminate;
        if (all.Contains(Verdict.Pass)) return Verdict.Pass;
        return Verdict.Info;
    }
}
