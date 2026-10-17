using Verify11683A.Measurement;

namespace Verify11683A.Tests;

public class SettlingDetectorTests
{
    /// <summary>One NPLC 100 reading with autozero at 60 Hz: 3.383 s.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3.383);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly SettlingCriteria Volts = SettlingCriteria.For(DvmFunction.DcVoltage);

    /// <summary>Feeds readings one interval apart; returns the 1-based index of the first Settled, or 0.</summary>
    private static int SettlesAt(SettlingDetector detector, IEnumerable<double> values)
    {
        var i = 0;
        foreach (var v in values)
        {
            i++;
            if (detector.Add(v, T0 + Interval * (i - 1)) == SettlingStatus.Settled)
                return i;
        }
        return 0;
    }

    [Fact]
    public void Band_is_20_ppm_with_floors_of_2_uV_and_2_mOhm()
    {
        Assert.Equal(2.9e-6, Volts.Band(0.145), 12);          // 20 ppm of 145 mV
        Assert.Equal(2e-6, Volts.Band(0.04585), 12);          // 20 ppm would be 0.92 µV: the floor wins
        Assert.Equal(316e-6, Volts.Band(-15.8), 12);          // magnitude
        var ohms = SettlingCriteria.For(DvmFunction.FourWireResistance);
        Assert.Equal(63.004e-3, ohms.Band(3150.2), 9);
        Assert.Equal(2e-3, ohms.Band(31.649), 12);           // 20 ppm would be 0.63 mΩ
    }

    [Fact]
    public void A_steady_output_settles_once_the_readings_span_the_hold_time()
    {
        // Readings at 0, 3.4, 6.8 and 10.1 s: the fourth is the first whose window spans 10 s.
        var detector = new SettlingDetector(Volts);
        Assert.Equal(4, SettlesAt(detector, Enumerable.Repeat(0.145, 10)));
        Assert.Equal(4, detector.Window.Count);
        Assert.Equal(SettlingStatus.Settled, detector.Status);
    }

    [Fact]
    public void Min_readings_applies_when_readings_are_slow()
    {
        // 30 s apart: the second reading already spans the hold time, but three are needed.
        var detector = new SettlingDetector(Volts);
        Assert.Equal(SettlingStatus.Settling, detector.Add(0.145, T0));
        Assert.Equal(SettlingStatus.Settling, detector.Add(0.145, T0.AddSeconds(30)));
        Assert.Equal(SettlingStatus.Settled, detector.Add(0.145, T0.AddSeconds(60)));
    }

    [Fact]
    public void An_exponential_tail_settles_only_once_it_is_inside_the_band()
    {
        // 300 ppm tail, τ = 5 s, on 145 mV (the simulator's default). Band ±2.9 µV around the window mean.
        var values = Enumerable.Range(0, 30).Select(i => 0.145 * (1 + 300e-6 * Math.Exp(-(i * Interval.TotalSeconds) / 5))).ToList();
        var detector = new SettlingDetector(Volts);
        var at = SettlesAt(detector, values);

        // Worked: the window is four readings over 10.15 s. At reading 7 (t = 20.3 s) it holds tails of
        // 39.4, 20.0, 10.2 and 5.2 ppm: mean 18.7, largest departure 20.7 ppm > 20 ppm, so not yet. At reading 8 it
        // holds 20.0, 10.2, 5.2 and 2.6 ppm: mean 9.5, largest departure 10.5 ppm. Settled.
        Assert.Equal(8, at);
        Assert.True(detector.Spread <= detector.Band);
        var tailNow = values[at - 1] / 0.145 - 1;
        Assert.True(tailNow < 20e-6);
    }

    [Fact]
    public void A_step_inside_the_window_holds_off_settling_until_it_leaves()
    {
        var values = Enumerable.Repeat(0.145, 5).Concat(Enumerable.Repeat(0.1451, 10)).ToList();
        var detector = new SettlingDetector(Volts);
        // Settles on reading 4, before the step.
        Assert.Equal(4, SettlesAt(detector, values.Take(4)));
        // After the step at reading 6, the window spans the step until reading 6 is the window's first reading.
        Assert.Equal(SettlingStatus.Settled, detector.Add(values[4], T0 + Interval * 4));
        var later = 0;
        for (var i = 5; i < values.Count && later == 0; i++)
            if (detector.Add(values[i], T0 + Interval * i) == SettlingStatus.Settled)
                later = i + 1;
        Assert.Equal(9, later);
    }

    [Fact]
    public void Noise_beyond_the_band_times_out()
    {
        var criteria = Volts with { Timeout = TimeSpan.FromSeconds(60) };
        var detector = new SettlingDetector(criteria);
        // ±10 µV alternating at 145 mV: 69 ppm, never inside ±20 ppm.
        var status = SettlingStatus.Settling;
        for (var i = 0; i < 40 && status == SettlingStatus.Settling; i++)
            status = detector.Add(0.145 + (i % 2 == 0 ? 10e-6 : -10e-6), T0 + Interval * i);
        Assert.Equal(SettlingStatus.TimedOut, status);
        Assert.True(detector.Elapsed >= criteria.Timeout);
    }

    [Fact]
    public void Reset_starts_again()
    {
        var detector = new SettlingDetector(Volts);
        SettlesAt(detector, Enumerable.Repeat(0.145, 4));
        detector.Reset();
        Assert.Equal(0, detector.Count);
        Assert.Equal(SettlingStatus.Settling, detector.Add(0.145, T0.AddMinutes(5)));
    }
}

public class InterruptionMonitorTests
{
    private static readonly SettlingCriteria Volts = SettlingCriteria.For(DvmFunction.DcVoltage);

    [Fact]
    public void A_knob_turn_is_an_interruption()
    {
        var monitor = new InterruptionMonitor(Volts, [0.145, 0.145001, 0.144999]);
        Assert.False(monitor.IsInterruption(0.1450005));
        Assert.True(monitor.IsInterruption(0.4585));      // RANGE moved to 3 mW
    }

    [Fact]
    public void A_step_just_outside_the_band_is_an_interruption()
    {
        var monitor = new InterruptionMonitor(Volts, [0.145, 0.145, 0.145]);
        Assert.False(monitor.IsInterruption(0.145 + 2.8e-6));
        Assert.True(monitor.IsInterruption(0.145 + 6e-6));
    }

    [Fact]
    public void Slow_drift_is_not_an_interruption()
    {
        // 15 ppm per minute, one reading every 3.4 s: 0.85 ppm per reading, 25 ppm over 30 readings.
        var values = Enumerable.Range(0, 33).Select(i => 0.145 * (1 + 0.85e-6 * i)).ToList();
        var monitor = new InterruptionMonitor(Volts, values.Take(3));
        Assert.All(values.Skip(3), v => Assert.False(monitor.IsInterruption(v)));
    }

    [Fact]
    public void A_noisy_point_measured_unsettled_is_not_abandoned_on_noise()
    {
        // Settled-window σ of 10 µV: the threshold widens to 5σ = 50 µV.
        var window = new[] { 0.145 + 10e-6, 0.145 - 10e-6, 0.145 + 10e-6, 0.145 - 10e-6 };
        var monitor = new InterruptionMonitor(Volts, window);
        Assert.Equal(5 * ReadingStatistics.Of(window).StandardDeviation, monitor.Threshold, 12);
        Assert.False(monitor.IsInterruption(0.145 + 25e-6));
        Assert.True(monitor.IsInterruption(0.146));
    }
}

public class ReadingStatisticsTests
{
    [Fact]
    public void Mean_sigma_and_standard_error_worked_by_hand()
    {
        // Mean 0.145002; deviations −2, 0, +2, −1, +1 µV; Σd² = 10e-12; s = √(10e-12/4) = 1.5811 µV; s/√5 = 0.7071 µV.
        var stats = ReadingStatistics.Of([0.145000, 0.145002, 0.145004, 0.145001, 0.145003]);
        Assert.Equal(5, stats.Count);
        Assert.Equal(0.145002, stats.Mean, 12);
        Assert.Equal(1.58114e-6, stats.StandardDeviation, 10);
        Assert.Equal(0.70711e-6, stats.StandardError, 10);
        Assert.Equal(0.145000, stats.Min);
        Assert.Equal(0.145004, stats.Max);
    }

    [Fact]
    public void One_reading_has_zero_sigma()
    {
        var stats = ReadingStatistics.Of([1.0]);
        Assert.Equal(0, stats.StandardDeviation);
    }
}
