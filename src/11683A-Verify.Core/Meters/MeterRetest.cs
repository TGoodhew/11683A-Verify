using Verify11683A.Uncertainty;

namespace Verify11683A.Meters;

/// <summary>
/// A retest: the tests a meter's last run did not pass (a FAIL, or rows not measured) run again, and the record made of
/// those new results with the earlier run's passing tests, so the meter has one complete record.
/// </summary>
public sealed record MeterRetest(string PreviousPath, MeterRun Previous)
{
    /// <summary>
    /// The newest saved run in <paramref name="directory"/> for this model and serial (a simulated run only for a
    /// simulation, a real one only for the bench); null if there is none or no serial was given.
    /// </summary>
    public static MeterRetest? Latest(string directory, string model, string serial, bool simulated)
    {
        if (serial == "" || !Directory.Exists(directory))
            return null;
        MeterRetest? latest = null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            MeterRun run;
            try
            {
                run = MeterRun.Load(path);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException)
            {
                continue;   // a verification run, the actuals file, or a damaged file
            }
            if (run.Schema != MeterRun.CurrentSchema || run.Meter is null
                || !string.Equals(run.Meter.Model, model, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(run.Meter.Serial, serial, StringComparison.OrdinalIgnoreCase)
                || (run.SimulationScenario is not null) != simulated)
                continue;
            if (latest is null || run.Finished > latest.Previous.Finished)
                latest = new MeterRetest(path, run);
        }
        return latest;
    }

    /// <summary>The earlier run's tests (per channel) that did not pass: a FAIL, or a row not measured.</summary>
    public IReadOnlyList<MeterTestResult> ToRerun => [.. Previous.Tests.Where(t => !t.Complete || t.Verdict != Verdict.Pass)];

    /// <summary>
    /// The earlier run's tests with each retested one (same test and channel) in its place, in the earlier order; a
    /// retested one the earlier run lacked goes at the end.
    /// </summary>
    public IReadOnlyList<MeterTestResult> Merge(IReadOnlyList<MeterTestResult> retested)
    {
        static bool Same(MeterTestResult a, MeterTestResult b) => a.TestId == b.TestId && a.Channel == b.Channel;
        return
        [
            .. Previous.Tests.Select(old => retested.FirstOrDefault(n => Same(n, old)) ?? old),
            .. retested.Where(n => !Previous.Tests.Any(old => Same(old, n))),
        ];
    }

    /// <summary>The note on the combined record: what was retested, and where the rest came from.</summary>
    public string Note(IReadOnlyList<MeterTestResult> retested) =>
        $"Retest: {string.Join("; ", retested.Select(t => t.Title + (t.Channel is "" ? "" : $" ({t.Channel})")))} run again. "
        + $"The other tests are from the run of {Previous.Started:yyyy-MM-dd HH:mm} ({Path.GetFileName(PreviousPath)}).";
}
