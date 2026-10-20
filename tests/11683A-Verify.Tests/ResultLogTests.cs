using Verify11683A.App.Reports;
using Verify11683A.Calibrator;
using Verify11683A.Measurement;
using Verify11683A.Meters;
using Verify11683A.Results;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

namespace Verify11683A.Tests;

public class ResultLogTests
{
    private static readonly CalibratorLimits Limits = CalibratorLimits.LoadDefault();
    private static readonly DvmSpecTable Specs = DvmSpecTable.LoadForModel("34401A");

    [Fact]
    public void The_1_mW_line_has_the_mean_sigma_n_spec_U_limits_and_verdict()
    {
        var stats = ReadingStatistics.Of([0.1450010, 0.1450012, 0.1450011, 0.1450013, 0.1450009]);
        var spec = Specs.Limit(DvmFunction.DcVoltage, 1, stats.Mean, SpecPeriod.Year1, null);
        var u = MeasurementUncertainty.Of(spec, stats, UncertaintyConvention.Default);
        var point = Limits.Part1.Point("1mW");
        var assessment = Assessment.Of(stats.Mean, u.Expanded, new Tolerance(point.VoltsMin!.Value, point.VoltsMax!.Value), UncertaintyConvention.Default);

        var line = ResultLog.OneMilliwatt(stats, u, assessment, "34401A", Limits.Part1.Source, settled: true);

        Assert.StartsWith("1 mW result: mean 145.00110 mV, σ", line);
        Assert.Contains(", n 5;", line);
        Assert.Contains($"34401A {SpecPeriod.Year1.Key()} spec ±", line);
        Assert.Contains("U (k = 2) ±", line);
        Assert.Contains("manual limits 143.00 mV to 147.00 mV", line);
        Assert.Contains(Limits.Part1.Source, line);
        Assert.EndsWith("verdict PASS", line);
        Assert.DoesNotContain("not settled", line);
    }

    [Fact]
    public void Part_1_logs_every_range_and_the_overall_verdict()
    {
        var points = Limits.Part1.Points.Select(p => Part1EvaluatorTests.Point(p.Range, Limits.NominalPart1Volts(p.Range))).ToList();
        var result = Part1Evaluator.Evaluate(Limits, points, Specs, SpecPeriod.Year1, null, UncertaintyConvention.Default);

        var lines = ResultLog.Part1(result, "34401A").ToList();

        Assert.Equal(result.Points.Count + 1, lines.Count);
        var oneMw = Assert.Single(lines, l => l.StartsWith("Part 1 result 1mW:", StringComparison.Ordinal));
        Assert.Contains("volts manual limits", oneMw);
        Assert.Contains("ratio ", oneMw);
        Assert.Equal("Part 1 overall: PASS, all 6 ranges measured (" + Limits.Part1.Source + ")", lines[^1]);
    }

    [Fact]
    public void Part_1_names_the_ranges_not_measured()
    {
        var points = new[] { Part1EvaluatorTests.Point("100mW", Limits.NominalPart1Volts("100mW")) };
        var result = Part1Evaluator.Evaluate(Limits, points, Specs, SpecPeriod.Year1, null, UncertaintyConvention.Default);

        Assert.Contains("not measured: ", ResultLog.Part1(result, "34401A").Last());
    }

    [Fact]
    public void The_meter_run_logs_each_test_and_the_overall_verdict()
    {
        var d = MeterDefinition.LoadModel("437B");
        var test = d.Tests.Single(t => t.Id == "instrument-accuracy");
        var results = test.Points.Select(p => new MeterPointResult(p, ReadingSource.Meter, p.Expected, false)).ToList();
        var run = new MeterRun
        {
            Meter = new MeterUnderTest("437B", d.Title, "123", "", "", new DataFileVersion("437B.json", d.Manual, "")),
            Calibrator = new UnitUnderTest("11683A", "1", ""),
            Tests = [new MeterTestResult(test.Id, test.Title, test.Source, "A", results)],
        };

        var lines = ResultLog.Meter(run).ToList();

        Assert.StartsWith($"Meter test instrument-accuracy channel A: PASS, {results.Count} of {results.Count} rows", lines[0]);
        Assert.Equal("Meter run 437B s/n 123: PASS, complete", lines[^1]);
    }
}
