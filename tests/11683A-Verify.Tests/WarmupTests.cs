using Verify11683A.Measurement;

namespace Verify11683A.Tests;

public class WarmupTests
{
    [Fact]
    public void The_longer_remaining_wait_wins()
    {
        // 11683A on 20 min of 30 (10 left); 34401A on 45 min of 60 (15 left).
        var plan = new WarmupPlan(30, 60, 20, 45);
        Assert.Equal(TimeSpan.FromMinutes(15), plan.Remaining);
        Assert.Equal("the 34401A", plan.Limiting("34401A"));
    }

    [Fact]
    public void Nothing_to_wait_for_once_both_are_warm()
    {
        var plan = new WarmupPlan(30, 60, 120, 600);
        Assert.Equal(TimeSpan.Zero, plan.Remaining);
        Assert.Equal("none", plan.Limiting("34401A"));
    }

    [Fact]
    public void Waiting_adds_to_both_instruments()
    {
        var plan = new WarmupPlan(30, 240, 0, 0).After(TimeSpan.FromMinutes(30));
        Assert.Equal(30, plan.CalibratorOnMinutes);
        Assert.Equal(TimeSpan.FromMinutes(210), plan.Remaining);
        Assert.Equal("the 3458A", plan.Limiting("3458A"));
    }

    [Fact]
    public void Equal_waits_name_both()
    {
        Assert.Equal("the 11683A and the 34401A", new WarmupPlan(30, 60, 0, 30).Limiting("34401A"));
    }
}
