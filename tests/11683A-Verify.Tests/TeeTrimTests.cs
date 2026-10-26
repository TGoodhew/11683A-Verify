using Verify11683A.Instruments.DcSource;

namespace Verify11683A.Tests;

public class TeeTrimTests
{
    /// <summary>A lead with a resistive drop (as a fraction) and an offset: what reaches the tee for a setting.</summary>
    private sealed class Lead(double drop, double offset)
    {
        public double Setting { get; private set; }

        public void Output(double volts) => Setting = volts;

        public double Tee() => Setting * (1 - drop) + offset;
    }

    [Fact]
    public void Takes_out_the_lead_offset_at_the_lowest_REF_EXT_row()
    {
        // The 458.31 µV row on 2026-10-21 read as if REF EXT were about 20 µV low.
        var lead = new Lead(drop: 0, offset: -20e-6);
        var trim = TeeTrim.Run(458.31e-6, lead.Output, lead.Tee);

        Assert.True(trim.Within);
        Assert.Equal(2, trim.Steps.Count);
        Assert.Equal(478.31e-6, trim.Setting, 12);
        Assert.Equal(458.31e-6, trim.Tee, 12);
    }

    [Fact]
    public void Takes_out_a_drop_and_an_offset_together_within_20_ppm()
    {
        var lead = new Lead(drop: 200e-6, offset: 6.4e-6);
        var trim = TeeTrim.Run(15.588, lead.Output, lead.Tee);

        Assert.True(trim.Within);
        Assert.InRange(Math.Abs(trim.Error), 0, TeeTrim.Band(15.588));
        Assert.True(trim.Steps.Count <= 3);
    }

    [Fact]
    public void Already_within_the_band_sets_once()
    {
        var lead = new Lead(drop: 0, offset: 0.5e-6);
        var trim = TeeTrim.Run(0.14493, lead.Output, lead.Tee);

        Assert.True(trim.Within);
        Assert.Equal(0.14493, Assert.Single(trim.Steps).Setting);
    }

    [Fact]
    public void More_than_2_percent_off_stops_at_once_as_gross()
    {
        var lead = new Lead(drop: 0, offset: 0);
        var trim = TeeTrim.Run(1.4493, lead.Output, () => 0);   // the DVM not on the tee

        Assert.True(trim.Gross);
        Assert.False(trim.Within);
        Assert.Single(trim.Steps);
    }

    [Fact]
    public void A_noisy_tee_gives_up_after_the_last_try()
    {
        var flip = 1;
        var lead = new Lead(drop: 0, offset: 0);
        var trim = TeeTrim.Run(1.0, lead.Output, () => lead.Tee() + (flip = -flip) * 50e-6);

        Assert.False(trim.Within);
        Assert.False(trim.Gross);
        Assert.Equal(TeeTrim.MaxTrims, trim.Steps.Count);
    }

    [Theory]
    [InlineData(458.31e-6, 1e-6)]   // the 1 µV floor
    [InlineData(0.14493, 2.8986e-6)]
    [InlineData(15.588, 311.76e-6)]
    public void The_band_is_20_ppm_or_1_uV(double target, double band) => Assert.Equal(band, TeeTrim.Band(target), 12);
}
