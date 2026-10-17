using Verify11683A.Calibrator;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Instruments.Simulation;

namespace Verify11683A.Tests;

public class SimulationTests
{
    private static readonly CalibratorLimits Limits = CalibratorLimits.LoadDefault();

    private static (Simulated11683A Calibrator, SimulatedDvm Dvm, SimulatedClock Clock) Bench(string scenario = "nominal")
    {
        var clock = new SimulatedClock();
        var calibrator = new Simulated11683A(Limits, SimulationScenario.Get(scenario), clock);
        return (calibrator, new SimulatedDvm(calibrator, clock), clock);
    }

    /// <summary>Reads until the output has settled (several time constants) and returns the next reading.</summary>
    private static double SettledReading(SimulatedDvm dvm)
    {
        for (var i = 0; i < 8; i++)
            dvm.Read();
        return dvm.Read().Value;
    }

    [Fact]
    public void Standby_grounds_the_output()
    {
        var (_, dvm, _) = Bench();
        dvm.Configure(DvmFunction.DcVoltage, 0.1, 100);
        Assert.InRange(SettledReading(dvm), -5e-6, 5e-6);
    }

    [Theory]
    [InlineData("100mW", 100)]
    [InlineData("30mW", 10)]
    [InlineData("10mW", 10)]
    [InlineData("3mW", 1)]
    [InlineData("1mW", 1)]
    [InlineData("300uW", 0.1)]
    public void Calibrate_settles_inside_every_part_1_limit_in_the_nominal_scenario(string range, double dvmRange)
    {
        var (calibrator, dvm, _) = Bench();
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        calibrator.SetRange("100mW");
        dvm.Configure(DvmFunction.DcVoltage, 100, 100);
        var reference = SettledReading(dvm);

        calibrator.SetRange(range);
        dvm.Configure(DvmFunction.DcVoltage, dvmRange, 100);
        var volts = SettledReading(dvm);

        var point = Limits.Part1.Point(range);
        Assert.InRange(reference / volts, point.RatioMin, point.RatioMax);
        if (point.VoltsMin is { } lo && point.VoltsMax is { } hi)
            Assert.InRange(volts, lo, hi);
    }

    [Fact]
    public void Output_settles_exponentially_after_a_knob_is_turned()
    {
        var (calibrator, dvm, _) = Bench();
        dvm.Configure(DvmFunction.DcVoltage, 1, 100);
        calibrator.SetRange("1mW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);

        // ~3.4 s after the change: the switch step has gone, about 200 ppm of the 300 ppm thermal tail remains.
        var first = dvm.Read().Value;
        var settled = SettledReading(dvm);
        Assert.InRange((first - settled) / settled, 120e-6, 280e-6);
        Assert.Equal(0.145, settled, 5);
    }

    [Fact]
    public void Reverse_polarity_gives_a_negative_output()
    {
        var (calibrator, dvm, _) = Bench();
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        calibrator.SetRange("100mW");
        calibrator.SetPolarity(PolaritySwitch.Reverse);
        dvm.Configure(DvmFunction.DcVoltage, 100, 100);
        Assert.True(SettledReading(dvm) < -15);
    }

    [Fact]
    public void Ref_ext_with_nothing_connected_gives_about_zero()
    {
        var (calibrator, dvm, _) = Bench();
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        calibrator.SetRange("1mW");
        calibrator.SetReference(ReferenceSwitch.Ext);
        dvm.Configure(DvmFunction.DcVoltage, 0.1, 100);
        Assert.InRange(SettledReading(dvm), -5e-6, 5e-6);
    }

    [Theory]
    [InlineData("300uW", 1e4)]
    [InlineData("100uW", 1e3)]
    [InlineData("30uW", 1e3)]
    [InlineData("10uW", 100)]
    [InlineData("3uW", 100)]
    public void Standby_ohms_settle_inside_every_part_2_limit(string range, double dvmRange)
    {
        var (calibrator, dvm, _) = Bench();
        calibrator.SetRange(range);
        dvm.Configure(DvmFunction.FourWireResistance, dvmRange, 100);
        var point = Limits.Part2.Point(range);
        Assert.InRange(SettledReading(dvm), point.OhmsMin, point.OhmsMax);
    }

    [Fact]
    public void Ohms_with_function_in_calibrate_are_far_from_any_part_2_value()
    {
        var (calibrator, dvm, _) = Bench();
        calibrator.SetRange("3uW");
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        dvm.Configure(DvmFunction.FourWireResistance, 100, 100);
        // 31.65 Ω + 0.46 mV / 1 mA ≈ 32.1 Ω: outside the 31.580–31.718 Ω limits.
        Assert.True(SettledReading(dvm) > Limits.Part2.Point("3uW").OhmsMax);
    }

    [Fact]
    public void Ohms_safety_passes_in_standby_and_fails_in_calibrate_even_on_3_uW()
    {
        var (calibrator, dvm, _) = Bench();
        calibrator.SetRange("3uW");
        SettledReading(Configured(dvm));
        Assert.True(OhmsSafety.Check(dvm).Safe);

        calibrator.SetFunction(FunctionSwitch.Calibrate);
        SettledReading(Configured(dvm));
        var check = OhmsSafety.Check(dvm);
        Assert.False(check.Safe);
        Assert.InRange(check.Reading.Value, 0.3e-3, 0.6e-3);
    }

    [Fact]
    public void Ohms_safety_fails_on_overload()
    {
        var (calibrator, dvm, _) = Bench();
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        calibrator.SetRange("100mW");
        SettledReading(Configured(dvm));
        var check = OhmsSafety.Check(dvm);
        Assert.False(check.Safe);
        Assert.True(check.Reading.Overload);
    }

    [Fact]
    public void Supply_low_scenario_puts_1_mW_below_143_mV()
    {
        var (calibrator, dvm, _) = Bench("supply-low");
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        calibrator.SetRange("1mW");
        dvm.Configure(DvmFunction.DcVoltage, 1, 100);
        Assert.InRange(SettledReading(dvm), 0.1415, 0.1425);
    }

    [Fact]
    public void A_reading_beyond_the_overrange_is_an_overload()
    {
        var (calibrator, dvm, _) = Bench();
        calibrator.SetFunction(FunctionSwitch.Calibrate);
        calibrator.SetRange("100mW");
        dvm.Configure(DvmFunction.DcVoltage, 10, 100);
        SettledReading(dvm);
        Assert.True(dvm.Read().Overload);
    }

    [Fact]
    public void Each_nplc_100_reading_advances_the_clock_by_the_real_reading_time()
    {
        var (_, dvm, clock) = Bench();
        dvm.Configure(DvmFunction.DcVoltage, 1, 100);
        var before = clock.Now;
        dvm.Read();
        // 100 PLC at 60 Hz = 1.667 s, doubled by autozero, plus 50 ms.
        Assert.Equal(3.383, (clock.Now - before).TotalSeconds, 2);
    }

    [Theory]
    [InlineData("3uW", 3.1623e-6)]
    [InlineData("10uW", 10e-6)]
    [InlineData("300uW", 316.23e-6)]
    [InlineData("100mW", 100e-3)]
    public void True_full_scale_power_steps_by_root_10(string range, double watts) =>
        Assert.Equal(watts, Simulated11683A.TrueFullScaleWatts(RangeSwitch.Get(range)), watts * 1e-4);

    [Fact]
    public void Every_scenario_has_a_unique_name_and_unknown_ones_are_rejected()
    {
        Assert.Equal(SimulationScenario.All.Count, SimulationScenario.All.Select(s => s.Name).Distinct().Count());
        Assert.Throws<KeyNotFoundException>(() => SimulationScenario.Get("nope"));
    }

    private static SimulatedDvm Configured(SimulatedDvm dvm)
    {
        dvm.Configure(DvmFunction.DcVoltage, 0.1, 10);
        return dvm;
    }
}
