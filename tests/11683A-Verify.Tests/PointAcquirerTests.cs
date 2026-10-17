using Verify11683A.Calibrator;
using Verify11683A.Instruments.Measurement;
using Verify11683A.Instruments.Simulation;
using Verify11683A.Measurement;

namespace Verify11683A.Tests;

/// <summary>Detection, settling and interruption against the simulated 11683A and DVM.</summary>
public class PointAcquirerTests
{
    private static readonly CalibratorLimits Limits = CalibratorLimits.LoadDefault();

    private static readonly IReadOnlyList<ExpectedValue> Part1 =
        Limits.Part1.Points.Select(p => new ExpectedValue(p.Range, Limits.NominalPart1Volts(p.Range))).ToList();

    private static readonly IReadOnlyList<ExpectedValue> Part2 =
        Limits.Part2.Points.Select(p => new ExpectedValue(p.Range, p.OhmsMid)).ToList();

    private static (Simulated11683A Calibrator, SimulatedDvm Dvm) Bench(SimulationScenario scenario)
    {
        var clock = new SimulatedClock();
        var calibrator = new Simulated11683A(Limits, scenario, clock);
        return (calibrator, new SimulatedDvm(calibrator, clock));
    }

    private static (Simulated11683A Calibrator, SimulatedDvm Dvm) Bench(string scenario = "nominal") =>
        Bench(SimulationScenario.Get(scenario));

    private static PointAcquirer Volts(SimulatedDvm dvm, Action<AcquisitionProgress>? progress = null, List<string>? log = null) =>
        new(dvm, new RangeDetector(DvmFunction.DcVoltage, Part1), SettlingCriteria.For(DvmFunction.DcVoltage), nplc: 100, readings: 10)
        {
            Progress = progress,
            Log = log is null ? null : log.Add,
        };

    [Theory]
    [InlineData("100mW", 100)]
    [InlineData("30mW", 10)]
    [InlineData("10mW", 10)]
    [InlineData("3mW", 1)]
    [InlineData("1mW", 1)]
    [InlineData("300uW", 0.1)]
    public void Detects_settles_and_measures_every_part_1_range(string range, double dvmRange)
    {
        var (calibrator, dvm) = Bench();
        calibrator.SetRange(range);
        calibrator.SetFunction(FunctionSwitch.Calibrate);

        var result = Volts(dvm).Acquire();

        Assert.Equal(AcquisitionOutcome.Measured, result.Outcome);
        Assert.Equal(range, result.Point!.RangeId);
        Assert.Equal(dvmRange, result.Configuration!.Range);
        Assert.Equal(100, result.Configuration.Nplc);
        Assert.True(result.Settled);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(10, result.Readings.Count);
        // Settled to well inside 20 ppm of the noise-free value.
        Assert.Equal(calibrator.SettledVolts(RangeSwitch.Get(range)), result.Statistics!.Mean, Math.Abs(result.Statistics.Mean) * 20e-6);
    }

    [Fact]
    public void The_scout_steps_down_from_100_V_to_the_100_mV_range_for_300_uW()
    {
        var (calibrator, dvm) = Bench();
        calibrator.SetRange("300uW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        var (detection, _, range) = Volts(dvm).Scout();
        Assert.True(detection.IsMatch);
        Assert.Equal(0.1, range);
    }

    [Fact]
    public void Settling_waits_out_the_thermal_tail()
    {
        var (calibrator, dvm) = Bench();
        calibrator.SetRange("1mW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        var result = Volts(dvm).Acquire();
        // 300 ppm, τ = 5 s: inside 20 ppm after ~14 s, then the 10 s hold.
        Assert.InRange(result.SettlingTime.TotalSeconds, 10, 40);
    }

    [Theory]
    [InlineData(FunctionSwitch.Standby, PolaritySwitch.Normal, ReferenceSwitch.Int, DetectionKind.NearZero)]
    [InlineData(FunctionSwitch.Calibrate, PolaritySwitch.Normal, ReferenceSwitch.Ext, DetectionKind.NearZero)]
    [InlineData(FunctionSwitch.Calibrate, PolaritySwitch.Reverse, ReferenceSwitch.Int, DetectionKind.Reversed)]
    public void Wrong_switch_settings_are_warnings_not_results(FunctionSwitch function, PolaritySwitch polarity, ReferenceSwitch reference, DetectionKind kind)
    {
        var (calibrator, dvm) = Bench();
        calibrator.SetRange("10mW");
        calibrator.SetFunction(function);
        calibrator.SetPolarity(polarity);
        calibrator.SetReference(reference);

        var result = Volts(dvm).Acquire();

        Assert.Equal(AcquisitionOutcome.NotDetected, result.Outcome);
        Assert.Equal(kind, result.Detection.Kind);
        Assert.Empty(result.Readings);
        Assert.Null(result.Statistics);
    }

    [Fact]
    public void A_range_outside_part_1_is_out_of_family()
    {
        var (calibrator, dvm) = Bench();
        calibrator.SetRange("100uW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        var result = Volts(dvm).Acquire();
        Assert.Equal(DetectionKind.OutOfFamily, result.Detection.Kind);
    }

    [Fact]
    public void A_knob_turned_partway_through_abandons_the_point_and_detects_again()
    {
        var (calibrator, dvm) = Bench();
        calibrator.SetRange("1mW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        var turned = false;
        var log = new List<string>();
        var acquirer = Volts(dvm, p =>
        {
            if (!turned && p.Phase == AcquisitionPhase.Measuring && p.Done == 4)
            {
                calibrator.SetRange("3mW");
                turned = true;
            }
        }, log);

        var result = acquirer.Acquire();

        Assert.Equal(AcquisitionOutcome.Measured, result.Outcome);
        Assert.Equal("3mW", result.Point!.RangeId);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(10, result.Readings.Count);
        Assert.Contains(log, l => l.Contains("abandoning the point"));
    }

    [Fact]
    public void A_small_disturbance_during_the_readings_also_abandons_the_point()
    {
        // The supply nudged by 100 ppm (A2R1 touched) partway through: still 1 mW, but outside the band.
        var (calibrator, dvm) = Bench();
        calibrator.SetRange("1mW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        var nudged = false;
        var result = Volts(dvm, p =>
        {
            if (!nudged && p.Phase == AcquisitionPhase.Measuring && p.Done == 6)
            {
                calibrator.SupplyScale *= 1.0001;
                nudged = true;
            }
        }).Acquire();

        Assert.Equal(AcquisitionOutcome.Measured, result.Outcome);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(0.145 * 1.0001, result.Statistics!.Mean, 0.145 * 20e-6);
    }

    [Fact]
    public void A_noisy_output_times_out_unless_measuring_anyway_is_allowed()
    {
        var scenario = SimulationScenario.Get("noisy") with { NoisePpm = 200 };
        var (calibrator, dvm) = Bench(scenario);
        calibrator.SetRange("1mW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);

        var refused = Volts(dvm).Acquire();
        Assert.Equal(AcquisitionOutcome.NotSettled, refused.Outcome);
        Assert.Empty(refused.Readings);
        Assert.Contains("did not settle", refused.Message);

        var forced = Volts(dvm).Acquire(allowUnsettled: true);
        Assert.Equal(AcquisitionOutcome.Measured, forced.Outcome);
        Assert.False(forced.Settled);
        Assert.Equal(10, forced.Readings.Count);
        Assert.Equal(1, forced.Attempts);
    }

    [Fact]
    public void A_drifting_output_still_settles_and_measures()
    {
        var (calibrator, dvm) = Bench("drifting");
        calibrator.SetRange("1mW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        var result = Volts(dvm).Acquire();
        Assert.Equal(AcquisitionOutcome.Measured, result.Outcome);
        Assert.Equal(1, result.Attempts);
    }

    [Theory]
    [InlineData("300uW", 1e4)]
    [InlineData("100uW", 1e3)]
    [InlineData("30uW", 1e3)]
    [InlineData("10uW", 100)]
    [InlineData("3uW", 100)]
    public void Detects_and_measures_every_part_2_range(string range, double dvmRange)
    {
        var (calibrator, dvm) = Bench();
        calibrator.SetRange(range);
        var acquirer = new PointAcquirer(dvm, new RangeDetector(DvmFunction.FourWireResistance, Part2),
            SettlingCriteria.For(DvmFunction.FourWireResistance), nplc: 100, readings: 5);

        var result = acquirer.Acquire();

        Assert.True(result.Outcome == AcquisitionOutcome.Measured, result.Message);
        Assert.Equal(range, result.Point!.RangeId);
        Assert.Equal(dvmRange, result.Configuration!.Range);
        var point = Limits.Part2.Point(range);
        Assert.InRange(result.Statistics!.Mean, point.OhmsMin, point.OhmsMax);
    }
}
