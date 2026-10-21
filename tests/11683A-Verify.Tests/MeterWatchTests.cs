using System.Diagnostics;
using Verify11683A.App.Procedures;
using Verify11683A.Instruments.PowerMeter;

namespace Verify11683A.Tests;

/// <summary>The meter readings shown while the 11683A settles.</summary>
public class MeterWatchTests
{
    [Fact]
    public void Keeps_the_last_two_readings()
    {
        var next = 0.0;
        var left = 10;
        using var watch = new MeterWatch(() => ++next, () => TimeSpan.FromSeconds(Interlocked.Decrement(ref left)));
        Assert.True(SpinWait.SpinUntil(() => watch.Done, 5000));
        Assert.Equal((9.0, 8.0, 9), watch.Readings);
        Assert.Null(watch.Error);
    }

    [Fact]
    public void Starts_no_reading_that_would_outlast_the_countdown()
    {
        // Each reading takes 50 ms; with 120 ms left, two fit and a third would run past the end. The countdown starts
        // when the watch first asks, so a slow thread start cannot use up the time.
        Stopwatch? clock = null;
        var count = 0;
        using var watch = new MeterWatch(() =>
        {
            Thread.Sleep(50);
            return ++count;
        }, () => TimeSpan.FromMilliseconds(120) - (clock ??= Stopwatch.StartNew()).Elapsed);
        Assert.True(SpinWait.SpinUntil(() => watch.Done, 5000));
        Assert.InRange(watch.Readings.Count, 1, 2);
    }

    [Fact]
    public void Dispose_waits_for_the_reading_under_way()
    {
        using var reading = new ManualResetEventSlim();
        var watch = new MeterWatch(() =>
        {
            reading.Set();
            Thread.Sleep(200);
            return 1e-6;
        }, () => TimeSpan.FromMinutes(1));
        reading.Wait();
        Assert.True(watch.Busy);
        watch.Dispose();
        Assert.False(watch.Busy);
        Assert.Equal(1e-6, watch.Readings.Latest);
    }

    [Fact]
    public void A_failed_reading_ends_the_watch_quietly()
    {
        var calls = 0;
        using var watch = new MeterWatch(() =>
        {
            calls++;
            throw new PowerMeterException("GPIB0::13::INSTR: timeout");
        }, () => TimeSpan.FromMinutes(1));
        Assert.True(SpinWait.SpinUntil(() => watch.Done, 5000));
        Assert.Equal(1, calls);
        Assert.IsType<PowerMeterException>(watch.Error);
        Assert.Equal(0, watch.Readings.Count);
    }

    /// <summary>Readings in watts every 6.47 s, as the E4419B gave them after Preset while the 11683A settled.</summary>
    private static List<(TimeSpan, double)> Every6_47s(params double[] nanowatts) =>
        [.. nanowatts.Select((nw, i) => (TimeSpan.FromSeconds(6.47 * (i + 1)), nw * 1e-9))];

    /// <summary>The 1.5%-of-full-scale-per-minute limit at the 3 µW range.</summary>
    private const double ZeroLimit = 0.015 * 3.162e-6;

    [Fact]
    public void Channel_A_on_the_bench_was_steady_enough_to_zero()
    {
        // 2026-10-21, channel A at 3 µW STANDBY: the Zero Test then passed (9.7 nW).
        var drift = MeterWatch.Drift(Every6_47s(-29.68, -29.66, -24.30, -36.00, -18.21, -13.67, 0.97, 9.15, -14.08, 5.05), MeterWatch.DriftWindow);
        Assert.NotNull(drift);
        Assert.InRange(Math.Abs(drift.Value), 0, ZeroLimit);
    }

    [Fact]
    public void Channel_B_on_the_bench_was_still_drifting_when_it_was_zeroed()
    {
        // 2026-10-21, channel B just switched down from 100 mW: zeroed at the end of these, it read -77 to -98 nW after.
        var drift = MeterWatch.Drift(Every6_47s(959.9, 683.3, 514.8, 441.8, 372.2, 350.6, 281.4, 249.6, 233.5, 223.9), MeterWatch.DriftWindow);
        Assert.NotNull(drift);
        Assert.True(drift < -400e-9, $"drift {drift}");
        Assert.True(Math.Abs(drift.Value) > ZeroLimit);
    }

    [Fact]
    public void No_drift_is_judged_until_the_readings_span_most_of_the_window()
    {
        Assert.Null(MeterWatch.Drift([], MeterWatch.DriftWindow));
        Assert.Null(MeterWatch.Drift(Every6_47s(1, 2), MeterWatch.DriftWindow));
        Assert.Null(MeterWatch.Drift(Every6_47s(1, 2, 3, 4, 5, 6), MeterWatch.DriftWindow));   // 32 s of the 60
        Assert.NotNull(MeterWatch.Drift(Every6_47s(1, 2, 3, 4, 5, 6, 7, 8), MeterWatch.DriftWindow));   // 45 s
    }

    [Fact]
    public void Drift_is_the_slope_per_minute_over_the_last_window_only()
    {
        // A steep start, then 1 per minute: only the last minute counts.
        var readings = new List<(TimeSpan, double)>();
        for (var s = 0; s <= 180; s += 10)
            readings.Add((TimeSpan.FromSeconds(s), s < 100 ? 1000 - 10 * s : s / 60.0));
        Assert.Equal(1, MeterWatch.Drift(readings, MeterWatch.DriftWindow)!.Value, 9);
    }
}
