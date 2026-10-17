using Spectre.Console.Testing;
using Verify11683A.App.Reports;
using Verify11683A.Calibrator;
using Verify11683A.Results;
using Verify11683A.Uncertainty;

namespace Verify11683A.Tests;

/// <summary>
/// The console tables render (markup errors only show at render time) and show what the operator needs, including
/// the live view's highlighted range and the unmeasured rows.
/// </summary>
public class ConsoleReportTests
{
    private static readonly CalibratorLimits Limits = CalibratorLimits.LoadDefault();
    private static readonly UncertaintyConvention Convention = UncertaintyConvention.Default;

    private static string Render(Spectre.Console.Rendering.IRenderable renderable)
    {
        var console = new TestConsole().Width(200);
        console.Write(renderable);
        return console.Output;
    }

    [Fact]
    public void Part1_table_shows_ratios_limits_tur_and_verdicts()
    {
        var output = Render(ConsoleReport.Part1Table(ResultsFileTests.SampleRun().Part1!, Limits, Convention));
        Assert.Contains("100 mW", output);
        Assert.Contains("reference", output);
        Assert.Contains("10.768–10.815", output);
        Assert.Contains("143.00 mV–147.00 mV", output);
        Assert.Contains("PASS", output);
        Assert.Contains(":1", output);
    }

    [Fact]
    public void Live_view_highlights_the_range_being_measured_and_shows_the_status()
    {
        // 10 mW not measured yet and being measured now.
        var run = ResultsFileTests.SampleRun();
        var partial = run.Part1! with { Points = run.Part1.Points.Where(p => p.RangeId != "10mW").ToList(), Missing = ["10mW"] };
        var output = Render(ConsoleReport.Live(ConsoleReport.Part1Table(partial, Limits, Convention, "10mW"), "10 mW: reading 3 of 10"));
        Assert.Contains("▶ 10 mW", output);
        Assert.Contains("measuring…", output);
        Assert.Contains("10 mW: reading 3 of 10", output);
    }

    [Fact]
    public void Part2_table_renders_every_range()
    {
        var output = Render(ConsoleReport.Part2Table(ResultsFileTests.SampleRun().Part2!, Limits, Convention, "3uW"));
        foreach (var label in new[] { "300 µW", "100 µW", "30 µW", "10 µW", "3 µW" })
            Assert.Contains(label, output);
        Assert.Contains("3143.3–3157.1 Ω", output);
    }

    [Fact]
    public void Summary_states_the_overall_verdict_conditions_and_recommendation()
    {
        var console = new TestConsole().Width(200);
        Spectre.Console.AnsiConsole.Console = console;
        ConsoleReport.Summary(ResultsFileTests.SampleRun((_, v) => v * 0.98), "results/x.json");
        var output = console.Output;
        Assert.Contains("Range Switch Performance Test: FAIL", output);
        Assert.Contains("ambient 23.5 °C", output);
        Assert.Contains("11683A on 45 min", output);
        Assert.Contains("A2R1", output);
    }

    [Fact]
    public void Incomplete_run_is_not_reported_as_a_pass()
    {
        var console = new TestConsole().Width(200);
        Spectre.Console.AnsiConsole.Console = console;
        ConsoleReport.Summary(ResultsFileTests.SampleRun() with { Part2 = null }, null);
        Assert.Contains("INCOMPLETE", console.Output);
        Assert.Contains("not run", console.Output);
    }

    [Fact]
    public void Part4_table_shows_the_transfer_and_the_table_rows()
    {
        var console = new TestConsole().Width(200);
        Spectre.Console.AnsiConsole.Console = console;
        ConsoleReport.Part4(ResultsFileTests.SamplePart4());
        Assert.Contains("EXT, 1 mW voltage", console.Output);
        Assert.Contains("±0.2%", console.Output);
        Assert.Contains("99.540 mW–100.46 mW", console.Output);
        Assert.Contains("within", console.Output);
    }

    [Fact]
    public void Wiring_diagram_names_both_bncs()
    {
        Assert.Contains("DC REFERENCE OUTPUT", Wiring.Diagram);
        Assert.Contains("REF EXT", Wiring.Diagram);
        Assert.Contains(Wiring.Rules, r => r.Contains("5 ft"));
    }
}
