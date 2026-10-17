namespace Verify11683A.Measurement;

/// <summary>Mean, sample standard deviation and extremes of the readings taken at one point.</summary>
public sealed record ReadingStatistics(int Count, double Mean, double StandardDeviation, double Min, double Max)
{
    /// <summary>The Type A standard uncertainty of the mean, σ/√N.</summary>
    public double StandardError => StandardDeviation / Math.Sqrt(Count);

    public static ReadingStatistics Of(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
            throw new ArgumentException("No readings.", nameof(values));
        var mean = values.Average();
        var sigma = values.Count > 1
            ? Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1))
            : 0;
        return new ReadingStatistics(values.Count, mean, sigma, values.Min(), values.Max());
    }
}
