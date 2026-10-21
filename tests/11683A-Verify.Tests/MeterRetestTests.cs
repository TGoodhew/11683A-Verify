using Verify11683A.Meters;
using Verify11683A.Results;
using Verify11683A.Uncertainty;

namespace Verify11683A.Tests;

/// <summary>Running again only what a meter's last run did not pass, and the record that results.</summary>
public class MeterRetestTests
{
    private static readonly MeterDefinition E4419B = MeterDefinition.LoadModel("E4419B");

    /// <summary>Zero Test and Procedure 1 on A and B; <paramref name="failing"/> (test id, channel) gets a reading far outside its limits.</summary>
    private static MeterRun Run(string serial, DateTimeOffset finished, params (string Test, string Channel)[] failing)
    {
        var results = new List<MeterTestResult>();
        foreach (var channel in new[] { "A", "B" })
        foreach (var test in E4419B.Tests.Where(t => t.Id is "zero-test" or "instrument-accuracy-procedure-1"))
        {
            var bad = failing.Contains((test.Id, channel));
            results.Add(new MeterTestResult(test.Id, test.Title, test.Source, channel,
                [.. test.Points.Select((p, i) => new MeterPointResult(p, ReadingSource.Meter, bad && i == 0 ? p.Max * 10 + 1 : p.Expected, false))]));
        }
        return new MeterRun
        {
            Started = finished.AddHours(-1),
            Finished = finished,
            Meter = new MeterUnderTest("E4419B", E4419B.Title, serial, "Agilent E4419B", "E4419B driver at GPIB0::13::INSTR",
                new DataFileVersion("E4419B.json", E4419B.Manual, new string('c', 64))),
            Calibrator = new UnitUnderTest("11683A", "SG52060005", "H01"),
            Tests = results,
        };
    }

    private static string Directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "11683A-Verify-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void The_newest_run_of_the_same_meter_is_found()
    {
        var dir = Directory();
        var t0 = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        Run("GB40202094", t0, ("zero-test", "A")).Save(dir);
        var newer = Run("GB40202094", t0.AddHours(2), ("instrument-accuracy-procedure-1", "A")).Save(dir);
        Run("OTHER", t0.AddHours(3), ("zero-test", "B")).Save(dir);
        File.WriteAllText(Path.Combine(dir, "not-a-meter-run.json"), "{ \"schema\": \"something else\" }");

        var retest = MeterRetest.Latest(dir, "E4419B", "GB40202094", simulated: false)!;
        Assert.Equal(newer, retest.PreviousPath);
        Assert.Null(MeterRetest.Latest(dir, "E4419B", "", simulated: false));
        Assert.Null(MeterRetest.Latest(dir, "E4419B", "GB40202094", simulated: true));
    }

    [Fact]
    public void Only_the_tests_that_failed_or_were_left_incomplete_are_run_again()
    {
        var run = Run("S1", DateTimeOffset.Now, ("instrument-accuracy-procedure-1", "A"));
        var incomplete = run.Tests.Single(t => t.TestId == "zero-test" && t.Channel == "B");
        run = run with
        {
            Tests = [.. run.Tests.Select(t => t == incomplete ? t with { Points = [.. t.Points.Select(p => p with { Reading = null })] } : t)],
        };
        var redo = new MeterRetest("x.json", run).ToRerun.Select(t => (t.TestId, t.Channel)).ToList();
        Assert.Equal([("instrument-accuracy-procedure-1", "A"), ("zero-test", "B")], redo);
    }

    [Fact]
    public void The_combined_record_has_the_new_results_in_place_of_the_old_and_passes()
    {
        var previous = Run("S1", DateTimeOffset.Now, ("instrument-accuracy-procedure-1", "A"));
        var retest = new MeterRetest("E4419B_S1_old.json", previous);
        Assert.Equal(Verdict.Fail, previous.Verdict);

        var again = Run("S1", DateTimeOffset.Now).Tests.Where(t => t.TestId == "instrument-accuracy-procedure-1" && t.Channel == "A").ToList();
        var merged = retest.Merge(again);

        Assert.Equal(previous.Tests.Select(t => (t.TestId, t.Channel)), merged.Select(t => (t.TestId, t.Channel)));
        Assert.Same(again[0], merged[1]);
        Assert.Equal(Verdict.Pass, (previous with { Tests = merged }).Verdict);
        Assert.Contains("E4419B_S1_old.json", retest.Note(again));
    }
}
