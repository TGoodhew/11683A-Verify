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
}
