using System.Text.Json;
using Verify11683A.Calibrator;
using Verify11683A.Measurement;
using Verify11683A.Results;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

namespace Verify11683A.Tests;

public class ResultsFileTests
{
    private static readonly CalibratorLimits Limits = CalibratorLimits.LoadDefault();
    private static readonly DvmSpecTable Specs = DvmSpecTable.LoadForModel("34401A");
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 14, 30, 5, TimeSpan.FromHours(-7));

    internal static VerificationRun SampleRun(Func<string, double, double>? volts = null)
    {
        var convention = UncertaintyConvention.Default;
        var p1 = Limits.Part1.Points
            .Select(p => Part1EvaluatorTests.Point(p.Range, (volts ?? ((_, v) => v))(p.Range, Limits.NominalPart1Volts(p.Range))))
            .ToList();
        var part1 = Part1Evaluator.Evaluate(Limits, p1, Specs, SpecPeriod.Year1, 23.5, convention);

        var p2 = Limits.Part2.Points.Select(p =>
        {
            var range = RangePicker.Pick([100, 1e3, 1e4], p.OhmsMid);
            var readings = Enumerable.Range(0, 10).Select(i => new TimedValue(p.OhmsMid + (i % 2 == 0 ? 1e-3 : -1e-3), T0.AddSeconds(i * 3.383))).ToList();
            return new MeasuredPoint(p.Range, DvmFunction.FourWireResistance, range, 100, "n/a (4-wire ohms)", readings, true, 21.4, 1);
        }).ToList();
        var part2 = Part2Evaluator.Evaluate(Limits, p2, [new OhmsSafetyRecord(T0, 0.4e-6, false, 50e-6, true)], Specs, SpecPeriod.Year1, 23.5, convention);

        return new VerificationRun
        {
            AppVersion = "0.1.0",
            Started = T0,
            Finished = T0.AddMinutes(25),
            Company = "The Schnauzer Group LLC",
            Operator = "Tony Goodhew",
            Unit = new UnitUnderTest("11683A", "SG52060005", "H01"),
            Dvm = new DvmRecord(new DvmDetails("HEWLETT-PACKARD", "34401A", "3146A12345", "11-5-2", new DateOnly(2026, 3, 14), 12),
                "GPIB0::22::INSTR", "HEWLETT-PACKARD,34401A,0,11-5-2", CalibrationState.InDate),
            Conditions = new TestConditions(23.5, 45, null),
            Settings = new RunSettings(100, 10, 60, 20, 10, 120, SpecPeriod.Year1, convention),
            Limits = new DataFileVersion("limits-11683A.json", Limits.Source, new string('a', 64)),
            SpecTable = new DataFileVersion("dvm-specs-34401A.json", Specs.Source, new string('b', 64)),
            Part1 = part1,
            Part2 = part2,
            Notes = ["sample"],
        };
    }

    internal static Part4Result SamplePart4()
    {
        var table = Verify11683A.H01.H01Table.LoadDefault();
        return new Part4Result(table.Source, "Agilent Technologies E4418B s/n GB12345678, fw A1.06.00, channel 1", true,
            new TransferCheck(0.99999e-3, 0.1450004, 0.1450008, 1.00050e-3, table.TransferTolerance),
            table.Rows.Select(r => new H01PointResult(r, r.SettingVolts * (1 + 3e-6), r.EffectiveWatts * 1.00001)).ToList(),
            ["Indication only."]);
    }

    [Fact]
    public void A_run_with_part_4_round_trips_and_part_4_does_not_change_the_verdict()
    {
        var run = SampleRun() with { Part4 = SamplePart4() with { Transfer = SamplePart4().Transfer! with { ExtWatts = 1.01e-3 } } };
        var json = ResultsFile.Serialize(run);
        var back = ResultsFile.Deserialize(json);
        Assert.Equal(json, ResultsFile.Serialize(back));
        Assert.False(back.Part4!.AllWithin);
        Assert.Equal(Verdict.Pass, back.Verdict);
        Assert.Equal(10, back.Part4.TablePoints.Count);
        Assert.Equal(15.588, back.Part4.TablePoints[^1].Row.SettingVolts);
    }

    [Fact]
    public void A_run_survives_the_round_trip_exactly()
    {
        var run = SampleRun();
        var json = ResultsFile.Serialize(run);
        var back = ResultsFile.Deserialize(json);

        Assert.Equal(json, ResultsFile.Serialize(back));
        Assert.Equal(run.Verdict, back.Verdict);
        Assert.Equal(Verdict.Pass, back.Verdict);
        Assert.True(back.Complete);
        Assert.Equal(new DateOnly(2026, 3, 14), back.Dvm.Details.CalibrationDate);
        Assert.Equal(T0, back.Started);
        Assert.Equal(TimeSpan.FromHours(-7), back.Started.Offset);
    }

    [Fact]
    public void Raw_readings_and_derived_values_come_back_intact()
    {
        var run = SampleRun();
        var back = ResultsFile.Deserialize(ResultsFile.Serialize(run));

        var original = run.Part1!.Point("10mW")!;
        var restored = back.Part1!.Point("10mW")!;
        Assert.Equal(original.Measurement.Readings, restored.Measurement.Readings);
        Assert.Equal(original.Ratio!.Value, restored.Ratio!.Value);
        Assert.Equal(original.RatioAssessment!.Tur, restored.RatioAssessment!.Tur);
        Assert.Equal(original.Uncertainty.Spec.AppliedAdders, restored.Uncertainty.Spec.AppliedAdders);
        Assert.Equal(DvmFunction.DcVoltage, restored.Measurement.Function);
        Assert.Equal(run.Part2!.SafetyChecks, back.Part2!.SafetyChecks);
        Assert.Equal(run.Settings, back.Settings);
    }

    [Fact]
    public void A_failing_run_keeps_its_verdict_and_notes()
    {
        var run = SampleRun((_, v) => v * 0.98);
        var back = ResultsFile.Deserialize(ResultsFile.Serialize(run));
        Assert.Equal(Verdict.Fail, back.Verdict);
        Assert.True(back.Part1!.SupplyAdjustmentRecommended);
        Assert.Equal(run.Part1!.Notes, back.Part1.Notes);
    }

    [Fact]
    public void Enums_are_written_as_names_and_nan_and_infinity_are_allowed()
    {
        var json = ResultsFile.Serialize(SampleRun());
        Assert.Contains("\"function\": \"FourWireResistance\"", json);
        Assert.Contains("\"specPeriod\": \"Year1\"", json);

        var info = Assessment.Of(15.8, 0, null, UncertaintyConvention.Default);
        var exact = Assessment.Of(15, 0, new Tolerance(10, 20), UncertaintyConvention.Default);
        var text = JsonSerializer.Serialize(new[] { info, exact }, Json.Options);
        var back = JsonSerializer.Deserialize<Assessment[]>(text, Json.Options)!;
        Assert.True(double.IsNaN(back[0].Tur));
        Assert.True(double.IsPositiveInfinity(back[1].Tur));
    }

    [Fact]
    public void An_unknown_schema_is_refused()
    {
        var json = ResultsFile.Serialize(SampleRun() with { Schema = "11683A-Verify/results/99" });
        Assert.Throws<InvalidDataException>(() => ResultsFile.Deserialize(json));
    }

    [Fact]
    public void File_name_is_model_serial_and_start_minute()
    {
        Assert.Equal("11683A_SG52060005_20261017-1430", ResultsFile.BaseName(SampleRun()));
        var odd = SampleRun() with { Unit = new UnitUnderTest("11683A", "SG/52 06", "") };
        Assert.Equal("11683A_SG-52-06_20261017-1430", ResultsFile.BaseName(odd));
    }

    [Fact]
    public void Saving_twice_in_the_same_minute_does_not_overwrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), "11683A-Verify-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = ResultsFile.Save(SampleRun(), directory);
            var second = ResultsFile.Save(SampleRun(), directory);
            Assert.EndsWith("11683A_SG52060005_20261017-1430.json", first);
            Assert.EndsWith("11683A_SG52060005_20261017-1430-2.json", second);
            Assert.Equal(Verdict.Pass, ResultsFile.Load(second).Verdict);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Data_file_version_hashes_what_was_loaded()
    {
        var version = DataFileVersion.Of(DataFiles.Resolve(DataFiles.LimitsFileName), Limits.Source);
        Assert.Equal("limits-11683A.json", version.File);
        Assert.Matches("^[0-9a-f]{64}$", version.Sha256);
    }
}
