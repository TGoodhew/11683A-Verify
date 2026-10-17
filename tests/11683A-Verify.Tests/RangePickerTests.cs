using Verify11683A.Formatting;
using Verify11683A.Measurement;

namespace Verify11683A.Tests;

public class RangePickerTests
{
    private static readonly double[] Dcv = [0.1, 1, 10, 100, 1000];
    private static readonly double[] Ohms = [100, 1e3, 1e4, 1e5, 1e6, 1e7];

    [Theory]
    // The ranges the prompt specifies for each 11683A point.
    [InlineData(15.8, 100)]      // 100 mW
    [InlineData(4.71, 10)]       // 30 mW
    [InlineData(1.465, 10)]      // 10 mW
    [InlineData(0.4585, 1)]      // 3 mW
    [InlineData(0.145, 1)]       // 1 mW
    [InlineData(0.04585, 0.1)]   // 300 µW
    [InlineData(-15.8, 100)]     // magnitude, not sign
    public void Dc_volts(double expected, double range) => Assert.Equal(range, RangePicker.Pick(Dcv, expected));

    [Theory]
    [InlineData(3150.2, 1e4)]
    [InlineData(998.05, 1e3)]
    [InlineData(315.83, 1e3)]
    [InlineData(99.965, 100)]
    [InlineData(31.649, 100)]
    public void Four_wire_ohms(double expected, double range) => Assert.Equal(range, RangePicker.Pick(Ohms, expected));

    [Theory]
    [InlineData(1.0, 1.17, true)]     // near the top of the 1 V range's 20% overrange
    [InlineData(1.0, 1.10, false)]
    [InlineData(1.0, 0.05, true)]     // belongs on the 100 mV range
    [InlineData(0.1, 0.00001, false)] // already the lowest range
    [InlineData(1000, 1100, false)]   // already the highest range
    public void Rerange_when_a_reading_is_near_either_end(double range, double reading, bool expected) =>
        Assert.Equal(expected, RangePicker.ShouldRerange(Dcv, range, reading));
}

public class SiFormatTests
{
    [Theory]
    [InlineData(0.145, "V", 6, "145.000 mV")]
    [InlineData(15.804, "V", 5, "15.804 V")]
    [InlineData(3150.2, "Ω", 6, "3.15020 kΩ")]
    [InlineData(12.8e-6, "V", 3, "12.8 µV")]
    [InlineData(-0.04585, "V", 4, "-45.85 mV")]
    [InlineData(0.0, "V", 4, "0 V")]
    public void Format(double value, string unit, int digits, string expected) =>
        Assert.Equal(expected, Si.Format(value, unit, digits));

    [Theory]
    [InlineData(0.1, "V", "100 mV")]
    [InlineData(1e4, "Ω", "10 kΩ")]
    [InlineData(100, "V", "100 V")]
    public void Range(double range, string unit, string expected) => Assert.Equal(expected, Si.Range(range, unit));
}
