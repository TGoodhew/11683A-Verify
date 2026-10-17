using Verify11683A.Results;

namespace Verify11683A.Tests;

public class CalibratorActualsTests
{
    private static CalibratorActuals Export(VerificationRun run) =>
        CalibratorActuals.From(run, "11683A_SG52060005_20261017-1430.json", ResultsFile.Serialize(run));

    [Fact]
    public void A_passing_run_exports_ratios_voltages_and_resistances()
    {
        var run = ResultsFileTests.SampleRun();
        var actuals = Export(run);

        Assert.Equal("11683A-actuals/1", actuals.Format);
        Assert.Equal("SG52060005", actuals.Unit.Serial);
        Assert.False(actuals.Simulated);
        Assert.Equal(2, actuals.CoverageFactor);
        Assert.Equal(["30mW", "10mW", "3mW", "1mW", "300uW"], actuals.Ratios.Select(r => r.Range));
        Assert.Equal(6, actuals.Voltages.Count);
        Assert.Equal(5, actuals.Resistances.Count);
        Assert.Matches("^[0-9a-f]{64}$", actuals.SourceSha256);

        // The 10 mW ratio: value and U from the result, nominal the middle of 10.768–10.815.
        var tenMw = actuals.Ratios.Single(r => r.Range == "10mW");
        var result = run.Part1!.Point("10mW")!.RatioAssessment!;
        Assert.Equal(result.Value, tenMw.Value);
        Assert.Equal(result.ExpandedUncertainty, tenMw.ExpandedUncertainty);
        Assert.Equal(10.7915, tenMw.Nominal, 9);
        Assert.Equal(tenMw.Value / 10.7915 - 1, tenMw.Deviation, 12);
        Assert.Equal(0.01, tenMw.NominalWatts);
    }

    [Fact]
    public void Only_the_1_mW_voltage_has_a_nominal_and_limits()
    {
        var actuals = Export(ResultsFileTests.SampleRun());
        var oneMw = actuals.Voltages.Single(v => v.Range == "1mW");
        Assert.Equal(0.145, oneMw.Nominal, 9);
        Assert.Equal(0.143, oneMw.LimitMin);
        Assert.All(actuals.Voltages.Where(v => v.Range != "1mW"), v => Assert.True(double.IsNaN(v.Nominal)));
    }

    [Fact]
    public void A_failing_or_incomplete_run_is_not_exported()
    {
        var failing = ResultsFileTests.SampleRun((_, v) => v * 0.98);
        Assert.Contains("Part 1 did not pass", CalibratorActuals.WhyNotExportable(failing));
        Assert.Throws<InvalidOperationException>(() => Export(failing));

        var run = ResultsFileTests.SampleRun();
        var incomplete = run with { Part1 = run.Part1! with { Missing = ["3mW"] } };
        Assert.Contains("incomplete", CalibratorActuals.WhyNotExportable(incomplete));
        Assert.Equal("Part 1 was not run.", CalibratorActuals.WhyNotExportable(run with { Part1 = null }));
    }

    [Fact]
    public void Part_2_is_optional_but_must_pass_if_run()
    {
        var run = ResultsFileTests.SampleRun();
        Assert.Empty(Export(run with { Part2 = null }).Resistances);
        var badPart2 = run with { Part2 = run.Part2! with { Missing = ["10uW"] } };
        Assert.Equal("Part 2 is incomplete.", CalibratorActuals.WhyNotExportable(badPart2));
    }

    [Fact]
    public void A_simulated_run_is_exported_but_marked()
    {
        var run = ResultsFileTests.SampleRun();
        var simulated = run with { Conditions = run.Conditions with { SimulationScenario = "nominal" } };
        Assert.True(Export(simulated).Simulated);
    }

    [Fact]
    public void The_file_round_trips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "11683A-Verify-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var actuals = Export(ResultsFileTests.SampleRun());
            var path = actuals.Save(directory);
            Assert.EndsWith("11683A-actuals.json", path);
            var back = CalibratorActuals.Load(path);
            Assert.Equal(actuals.Ratios, back.Ratios);
            Assert.Equal(actuals.Resistances, back.Resistances);
            Assert.Equal(actuals.Voltages.Count, back.Voltages.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
