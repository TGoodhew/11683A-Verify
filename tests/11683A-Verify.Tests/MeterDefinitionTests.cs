using System.Text.Json;
using Verify11683A.App.Reports;
using Verify11683A.Calibrator;
using Verify11683A.Instruments.PowerMeter;
using Verify11683A.Meters;
using Verify11683A.Results;
using Verify11683A.Tests.Fakes;

namespace Verify11683A.Tests;

/// <summary>The meter definitions load, and their values match the manuals (spot checks against the printed tables).</summary>
public class MeterDefinitionTests
{
    private static readonly IReadOnlyList<MeterDefinition> All = MeterDefinition.LoadAll();

    private static MeterTestPoint Row(string model, string test, string label) =>
        All.Single(d => d.Model == model).Tests.Single(t => t.Id == test).Points.Single(p => p.Label == label);

    [Fact]
    public void All_seven_meters_load()
    {
        Assert.Equal(["435A", "435B", "436A", "437B", "438A", "E4418B", "E4419B"], All.Select(d => d.Model));
        Assert.All(All, d => Assert.NotEmpty(d.Manual));
        Assert.All(All.SelectMany(d => d.Tests), t => Assert.Contains("p.", t.Source));
    }

    [Theory]
    [InlineData("435A", null)]
    [InlineData("435B", null)]
    [InlineData("436A", "436A")]
    [InlineData("437B", "437B")]
    [InlineData("438A", "438A")]
    [InlineData("E4418B", "E4418B")]
    [InlineData("E4419B", "E4419B")]
    public void Bus_drivers(string model, string? driver) => Assert.Equal(driver, MeterDefinition.LoadModel(model).BusDriver);

    [Fact]
    public void The_435s_read_the_recorder_output_in_millivolts()
    {
        foreach (var model in new[] { "435A", "435B" })
        {
            var accuracy = MeterDefinition.LoadModel(model).Tests.Single(t => t.Id == "instrumentation-accuracy");
            Assert.Equal(ReadingSource.Recorder, accuracy.Reading);
            Assert.All(accuracy.Points, p => Assert.Equal("mV", p.Unit));
            Assert.Equal((975, 1025), (Row(model, "instrumentation-accuracy", "3 µW").Min, Row(model, "instrumentation-accuracy", "3 µW").Max));
            Assert.Equal((998, 1002), (Row(model, "instrumentation-accuracy", "1 mW").Min, Row(model, "instrumentation-accuracy", "1 mW").Max));
        }
        // 435A Table 4-1: 10 µW zero carryover is printed ±17 mVdc, wider than 3 µW.
        Assert.Equal(17, Row("435A", "zero-carryover", "10 µW").Max);
    }

    [Theory]
    // Model, test, row, min, max as printed.
    [InlineData("437B", "instrument-accuracy", "3 µW", 3.10, 3.23)]
    [InlineData("437B", "instrument-accuracy", "300 µW", 0.314, 0.318)]
    [InlineData("437B", "zero-carryover", "RNG 3", -0.001, 0.001)]
    [InlineData("438A", "instrument-accuracy", "100 mW", 99.0, 101.0)]
    [InlineData("438A", "instrument-accuracy", "-10 dBm", -10.04, -9.96)]
    [InlineData("436A", "instrument-accuracy", "3 mW (W)", 3.14, 3.18)]
    [InlineData("436A", "calibration-factor", "CAL FACTOR 85%", 1.170, 1.182)]
    [InlineData("435B", "calibration-factor", "CAL FACTOR 99%", 1.004, 1.016)]
    [InlineData("E4418B", "instrument-accuracy-procedure-1", "100 mW", 100.9, 101.8)]
    [InlineData("E4418B", "zero-test", "Zero, filter 512", -76.40, 76.40)]
    public void Limits_as_printed(string model, string test, string row, double min, double max)
    {
        var p = Row(model, test, row);
        Assert.Equal(min, p.Min, 9);
        Assert.Equal(max, p.Max, 9);
    }

    [Fact]
    public void E4418B_procedure_2_is_table_2_4()
    {
        var points = MeterDefinition.LoadModel("E4418B").Tests.Single(t => t.Id == "instrument-accuracy-procedure-2").Points;
        Assert.Equal(10, points.Count);
        Assert.All(points, p => Assert.Equal(ReferenceSwitch.Ext, p.Calibrator!.Reference));
        Assert.Equal(15.588, points[^1].Calibrator!.ExternalVolts);
        Assert.Equal((99.54, 100.46), (points[^1].Min, points[^1].Max));
        Assert.True(MeterDefinition.LoadModel("E4418B").Tests.Single(t => t.Id == "instrument-accuracy-procedure-2").NeedsH01);
    }

    [Fact]
    public void The_E4419B_runs_the_E4418B_tests_on_both_channels_with_the_same_limits()
    {
        var e4418b = MeterDefinition.LoadModel("E4418B");
        var e4419b = MeterDefinition.LoadModel("E4419B");
        Assert.Equal(["A", "B"], e4419b.Channels);
        Assert.All(e4419b.Tests, t => Assert.True(t.PerChannel));
        Assert.Equal(e4418b.Tests.Select(t => t.Id), e4419b.Tests.Select(t => t.Id));
        var rows = (MeterDefinition d) => d.Tests.SelectMany(t => t.Points.Select(p => (t.Id, p.Label, p.Expected, p.Min, p.Max, p.Unit, p.Calibrator?.ExternalVolts)));
        Assert.Equal(rows(e4418b), rows(e4419b));
    }

    [Fact]
    public void The_438A_runs_its_tests_on_both_channels()
    {
        var d = MeterDefinition.LoadModel("438A");
        Assert.Equal(["A", "B"], d.Channels);
        Assert.All(d.Tests, t => Assert.True(t.PerChannel));
    }

    [Fact]
    public void dBm_and_dB_rows_are_read_with_the_meter_showing_dBm_or_dB_REL()
    {
        foreach (var (t, p) in All.SelectMany(d => d.Tests.SelectMany(t => t.Points.Select(p => (t, p)))).Where(x => x.p.Unit is "dB" or "dBm"))
        {
            Assert.Equal(ReadingSource.Meter, t.SourceOf(p));
            Assert.Equal(p.Unit == "dB" ? MeterDisplay.DbRelative : MeterDisplay.Dbm, t.Meter!.With(p.Meter).Display);
        }
    }

    [Fact]
    public void Every_bus_meter_test_is_automated_with_a_zero_in_STANDBY()
    {
        foreach (var t in All.Where(d => d.BusDriver is not null).SelectMany(d => d.Tests))
        {
            Assert.True(t.Automated, t.Id);
            Assert.Equal(FunctionSwitch.Standby, t.Zero!.Calibrator.Function);
            if (t.Cal is { } cal)
                Assert.Equal(FunctionSwitch.Calibrate, cal.Calibrator.Function);
        }
        Assert.All(All.Where(d => d.BusDriver is null).SelectMany(d => d.Tests), t => Assert.False(t.Automated));
    }

    [Fact]
    public void The_dB_REL_rows_store_their_reference_first()
    {
        foreach (var model in new[] { "436A", "438A" })
        {
            var test = MeterDefinition.LoadModel(model).Tests.Single(t => t.Id == "instrument-accuracy");
            var rel = test.Points.Where(p => p.Unit == "dB").ToList();
            Assert.True(rel[0].Meter!.StoreReference, model);
            Assert.All(rel.Skip(1), p => Assert.False(p.Meter!.StoreReference));
        }
    }

    [Fact]
    public void The_436A_CAL_ADJ_is_by_hand_and_the_437B_438A_and_E441x_CAL_is_over_the_bus()
    {
        Assert.All(MeterDefinition.LoadModel("436A").Tests.Where(t => t.Cal is not null), t => Assert.Equal("1.000 ±0.002 mW", t.Cal!.AdjustTo));
        Assert.All(new[] { "437B", "438A", "E4418B" }.SelectMany(m => MeterDefinition.LoadModel(m).Tests).Where(t => t.Cal is not null),
            t => Assert.Null(t.Cal!.AdjustTo));
    }

    [Fact]
    public void Every_cal_factor_expectation_is_inside_its_limits()
    {
        foreach (var p in All.SelectMany(d => d.Tests).SelectMany(t => t.Points).Where(p => p.CalFactorPercent is not null))
            Assert.InRange(p.Expected, p.Min, p.Max);
    }

    [Fact]
    public void A_bad_definition_is_refused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "11683A-Verify-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bad = new
            {
                model = "X", tests = new[] { new { id = "t", title = "T", source = "p. 1", reading = "Recorder",
                    points = new[] { new { label = "r", meterSetting = "", expected = 1.0, unit = "mW", min = 0.9, max = 1.1 } } } },
            };
            var path = Path.Combine(dir, "X.json");
            File.WriteAllText(path, JsonSerializer.Serialize(bad, Json.Options));
            var ex = Assert.Throws<InvalidDataException>(() => MeterDefinition.Load(path));
            Assert.Contains("recorder-output reading must be in mV or V", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("mW", 1e-3, 1.0)]
    [InlineData("uW", 3.16e-6, 3.16)]
    [InlineData("nW", 50e-9, 50)]
    [InlineData("dBm", 0.1, 20)]
    public void Watts_in_the_rows_units(string unit, double watts, double expected) =>
        Assert.Equal(expected, MeterUnits.FromWatts(unit, watts)!.Value, 9);

    [Fact]
    public void A_relative_dB_reading_cannot_come_from_watts() => Assert.Null(MeterUnits.FromWatts("dB", 1e-3));
}

public class Hp436ADriverTests
{
    [Theory]
    [InlineData("PKA 1000E-06", 1e-3)]
    [InlineData("PIA 3162E-09", 3.162e-6)]
    [InlineData("QIA-0012E-09", -12e-9)]
    [InlineData("PMA 1003E-04\r\n", 0.1003)]
    public void Decodes_the_14_character_reply(string reply, double watts) =>
        Assert.Equal(watts, Hp436APowerMeter.Parse(reply), 15);

    [Theory]
    [InlineData("RMA 9999E-04")]   // over range
    [InlineData("PKD 0000E-02")]   // dBm mode
    [InlineData("garbage")]
    public void Refuses_over_range_wrong_mode_and_garbage(string reply) =>
        Assert.Throws<PowerMeterException>(() => Hp436APowerMeter.Parse(reply));

    [Fact]
    public void Sends_9A_plus_T_or_9A_minus_T_for_the_cal_factor_test()
    {
        var session = new ScriptedVisaSession().Answer("9A+T", "PKA 1000E-06").Answer("9A-T", "PKA 1053E-06");
        var meter = new Hp436APowerMeter(session);
        Assert.Equal(1e-3, meter.ReadWatts(), 12);
        meter.UseCalFactorSwitch = true;
        Assert.Equal(1.053e-3, meter.ReadWatts(), 12);
        Assert.Equal(["9A+T", "9A-T"], session.Sent);
    }

    [Fact]
    public void The_438A_reads_sensor_b_on_channel_2()
    {
        var session = new ScriptedVisaSession().Answer("TR2", "1.0E-03");
        PowerMeters.Open("438A", session, 2).ReadWatts();
        Assert.Equal(["BP", "LN", "TR2", "TR3"], session.Sent);
    }
}

public class MeterRunTests
{
    internal static MeterRun Sample(double? reading = 10.01)
    {
        var d = MeterDefinition.LoadModel("437B");
        var test = d.Tests.Single(t => t.Id == "instrument-accuracy");
        var points = test.Points.Select(p => new MeterPointResult(p, ReadingSource.Meter, p.Label == "10 mW" ? reading : p.Expected, false)).ToList();
        return new MeterRun
        {
            Started = new DateTimeOffset(2026, 10, 17, 11, 0, 0, TimeSpan.FromHours(-7)),
            Company = "The Schnauzer Group LLC",
            Meter = new MeterUnderTest("437B", d.Title, "3125U12345", "HP 437B, fw 2.1", "437B driver at GPIB0::13::INSTR",
                new DataFileVersion("437B.json", d.Manual, new string('c', 64))),
            Calibrator = new UnitUnderTest("11683A", "SG52060005", "H01"),
            Tests = [new MeterTestResult(test.Id, test.Title, test.Source, "A", points)],
            NotRun = d.NotCovered,
        };
    }

    [Fact]
    public void Verdicts_follow_the_manuals_limits()
    {
        Assert.Equal(Uncertainty.Verdict.Pass, Sample().Verdict);
        Assert.Equal(Uncertainty.Verdict.Fail, Sample(10.06).Verdict);
        var skipped = Sample(null);
        Assert.False(skipped.Complete);
        Assert.Equal(Uncertainty.Verdict.Pass, skipped.Verdict);
    }

    [Fact]
    public void A_reading_exactly_on_a_limit_passes() =>
        Assert.Equal(Uncertainty.Verdict.Pass, Sample(10.05).Verdict);

    [Fact]
    public void Round_trips_and_is_named_by_model_serial_and_time()
    {
        var dir = Path.Combine(Path.GetTempPath(), "11683A-Verify-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Sample().Save(dir);
            Assert.EndsWith("437B_3125U12345_20261017-1100.json", path);
            var back = MeterRun.Load(path);
            Assert.Equal(Uncertainty.Verdict.Pass, back.Verdict);
            Assert.Equal(10, back.Tests[0].Points.Count);
            Assert.Equal(10.01, back.Tests[0].Points.Single(p => p.Point.Label == "10 mW").Reading);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_pdf_record_renders()
    {
        Assert.True(MeterReport.Generate(Sample(10.06) with { SimulationScenario = "nominal" }).Length > 5_000);
    }
}
