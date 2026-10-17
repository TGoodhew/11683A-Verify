using Verify11683A.Calibrator;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Instruments.Measurement;
using Verify11683A.Instruments.Simulation;
using Verify11683A.Measurement;
using Verify11683A.Results;
using Verify11683A.Specs;
using Verify11683A.Tests.Fakes;
using Verify11683A.Uncertainty;

namespace Verify11683A.Tests;

public class Part2EvaluatorTests
{
    private static readonly CalibratorLimits Limits = CalibratorLimits.LoadDefault();
    private static readonly DvmSpecTable Specs = DvmSpecTable.LoadForModel("34401A");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly OhmsSafetyRecord[] Safe = [new(T0, 0.4e-6, false, 50e-6, true)];

    private static MeasuredPoint Point(string range, double ohms, double sigma = 0)
    {
        var dvmRange = RangePicker.Pick([100, 1e3, 1e4, 1e5], ohms);
        var readings = Enumerable.Range(0, 10)
            .Select(i => new TimedValue(ohms + (i % 2 == 0 ? sigma : -sigma), T0.AddSeconds(3.383 * i)))
            .ToList();
        return new MeasuredPoint(range, DvmFunction.FourWireResistance, dvmRange, 100, "n/a (4-wire ohms)", readings, true, 20, 1);
    }

    private static List<MeasuredPoint> All(Func<string, double, double> ohms) =>
        Limits.Part2.Points.Select(p => Point(p.Range, ohms(p.Range, p.OhmsMid))).ToList();

    private static Part2Result Evaluate(IReadOnlyCollection<MeasuredPoint> points, IReadOnlyList<OhmsSafetyRecord>? checks = null) =>
        Part2Evaluator.Evaluate(Limits, points, checks ?? Safe, Specs, SpecPeriod.Year1, null, UncertaintyConvention.Default);

    [Fact]
    public void Nominal_unit_passes_every_range()
    {
        var result = Evaluate(All((_, r) => r));
        Assert.True(result.Complete);
        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Empty(result.Notes);
        Assert.Equal([1e4, 1e3, 1e3, 100, 100], result.Points.Select(p => p.Measurement.DvmRange));
    }

    [Fact]
    public void Three_uW_worked_by_hand()
    {
        // 34401A 1 y on 100 Ω: 0.010% × 31.649 + 0.004% × 100 = 3.1649 + 4.000 = 7.1649 mΩ; u_B = 4.1367 mΩ.
        // ±1 mΩ alternating over 10 readings: s = 1.0541 mΩ, s/√10 = 0.3333 mΩ. u_c = 4.1501 mΩ, U = 8.3001 mΩ.
        // 31.580–31.718 Ω: half-width 69 mΩ, TUR 8.31.
        var result = Evaluate([Point("3uW", 31.649, 1e-3)]);
        var p = result.Point("3uW")!;
        Assert.Equal(4.1367e-3, p.Uncertainty.TypeB, 7);
        Assert.Equal(0.3333e-3, p.Uncertainty.TypeA, 7);
        Assert.Equal(8.3001e-3, p.Uncertainty.Expanded, 7);
        Assert.Equal(8.31, p.Assessment.Tur, 2);
        Assert.Equal(Verdict.Pass, p.Verdict);
    }

    [Fact]
    public void Three_uW_0_4_percent_high_fails()
    {
        var result = Evaluate(All((range, r) => range == "3uW" ? r * 1.004 : r));
        Assert.Equal(Verdict.Fail, result.Point("3uW")!.Verdict);
        Assert.Equal(Verdict.Fail, result.Verdict);
    }

    [Fact]
    public void Missing_ranges_and_a_missing_safety_check_are_reported()
    {
        var result = Evaluate(All((_, r) => r).Where(p => p.RangeId != "10uW").ToList(), checks: []);
        Assert.Equal(["10uW"], result.Missing);
        Assert.False(result.Complete);
        Assert.Contains(result.Notes, n => n.Contains("safety check"));
    }
}

/// <summary>The STANDBY interlock: ohms current is never applied while the output is live.</summary>
public class OhmsInterlockTests
{
    private static readonly CalibratorLimits Limits = CalibratorLimits.LoadDefault();

    private static (Simulated11683A Calibrator, RecordingDvm Dvm, List<OhmsSafetyCheck> Checks, PointAcquirer Acquirer) Bench()
    {
        var clock = new SimulatedClock();
        var calibrator = new Simulated11683A(Limits, SimulationScenario.Get("nominal"), clock);
        var dvm = new RecordingDvm(new SimulatedDvm(calibrator, clock), () => calibrator.Function == FunctionSwitch.Calibrate);
        var checks = new List<OhmsSafetyCheck>();
        var candidates = Limits.Part2.Points.Select(p => new ExpectedValue(p.Range, p.OhmsMid)).ToList();
        var acquirer = new PointAcquirer(dvm, new RangeDetector(DvmFunction.FourWireResistance, candidates),
            SettlingCriteria.For(DvmFunction.FourWireResistance), nplc: 100, readings: 5)
        {
            Interlock = OhmsSafety.Interlock(dvm, checks.Add),
        };
        return (calibrator, dvm, checks, acquirer);
    }

    [Fact]
    public void In_standby_the_safety_reading_comes_before_any_ohms_range()
    {
        var (calibrator, dvm, checks, acquirer) = Bench();
        calibrator.SetRange("30uW");

        var result = acquirer.Acquire();

        Assert.Equal(AcquisitionOutcome.Measured, result.Outcome);
        Assert.Equal((DvmFunction.DcVoltage, 0.1), (dvm.Configured[0].Function, dvm.Configured[0].Range));
        Assert.Single(checks);
        Assert.True(checks[0].Safe);
    }

    [Fact]
    public void In_calibrate_no_ohms_range_is_ever_configured()
    {
        var (calibrator, dvm, checks, acquirer) = Bench();
        calibrator.SetRange("30uW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);

        var result = acquirer.Acquire();

        Assert.Equal(AcquisitionOutcome.Blocked, result.Outcome);
        Assert.Contains("STANDBY", result.Message);
        Assert.DoesNotContain(dvm.Configured, c => c.Function == DvmFunction.FourWireResistance);
        Assert.False(Assert.Single(checks).Safe);
    }

    [Theory]
    [InlineData("300uW")]
    [InlineData("3uW")]
    public void Function_switched_to_calibrate_partway_through_stops_the_ohms_at_once(string range)
    {
        var (calibrator, dvm, checks, acquirer) = Bench();
        calibrator.SetRange(range);
        var flipped = false;
        acquirer.Progress = p =>
        {
            if (!flipped && p.Phase == AcquisitionPhase.Measuring && p.Done == 2)
            {
                calibrator.SetFunction(FunctionSwitch.Calibrate);
                flipped = true;
            }
        };

        var result = acquirer.Acquire();

        // The next reading no longer matches (R + V/I), the point is abandoned, and the interlock refuses.
        Assert.Equal(AcquisitionOutcome.Blocked, result.Outcome);
        Assert.Equal(2, checks.Count);
        Assert.DoesNotContain(dvm.Configured, c => c.Function == DvmFunction.FourWireResistance && c.OutputLive);
    }

    [Fact]
    public void Every_part_2_range_is_measured_in_standby()
    {
        var (calibrator, _, checks, acquirer) = Bench();
        foreach (var point in Limits.Part2.Points)
        {
            calibrator.SetRange(point.Range);
            var result = acquirer.Acquire();
            Assert.Equal(AcquisitionOutcome.Measured, result.Outcome);
            Assert.Equal(point.Range, result.Point!.RangeId);
            Assert.InRange(result.Statistics!.Mean, point.OhmsMin, point.OhmsMax);
        }
        Assert.Equal(Limits.Part2.Points.Count, checks.Count);
        Assert.All(checks, c => Assert.True(c.Safe));
    }
}

public class OhmsSafetyCheckTests
{
    private static readonly CalibratorLimits Limits = CalibratorLimits.LoadDefault();

    [Fact]
    public void An_output_still_falling_to_ground_just_after_standby_is_accepted()
    {
        var clock = new SimulatedClock();
        var calibrator = new Simulated11683A(Limits, SimulationScenario.Get("nominal"), clock);
        var dvm = new SimulatedDvm(calibrator, clock);
        calibrator.SetRange("300uW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        clock.Advance(TimeSpan.FromMinutes(1));

        // FUNCTION to STANDBY and check at once: the first reading (0.38 s later) is still ~13 mV.
        calibrator.SetFunction(FunctionSwitch.Standby);
        var check = OhmsSafety.Check(dvm);
        Assert.True(check.Safe);
        Assert.InRange(check.Reading.Value, -50e-6, 50e-6);
    }

    [Fact]
    public void A_single_reading_check_refuses_that_same_output()
    {
        var clock = new SimulatedClock();
        var calibrator = new Simulated11683A(Limits, SimulationScenario.Get("nominal"), clock);
        var dvm = new SimulatedDvm(calibrator, clock);
        calibrator.SetRange("300uW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        clock.Advance(TimeSpan.FromMinutes(1));
        calibrator.SetFunction(FunctionSwitch.Standby);
        Assert.False(OhmsSafety.Check(dvm, maxReadings: 1).Safe);
    }

    [Fact]
    public void A_live_output_is_refused_however_long_the_check_waits()
    {
        var clock = new SimulatedClock();
        var calibrator = new Simulated11683A(Limits, SimulationScenario.Get("nominal"), clock);
        var dvm = new SimulatedDvm(calibrator, clock);
        calibrator.SetRange("3uW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        Assert.False(OhmsSafety.Check(dvm, maxReadings: 50).Safe);
    }
}
