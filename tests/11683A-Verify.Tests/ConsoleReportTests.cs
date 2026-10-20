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

    [Fact]
    public void Wiring_rules_start_below_the_diagram()
    {
        var console = new TestConsole().Width(200);
        Spectre.Console.AnsiConsole.Console = console;
        Wiring.Write();
        var lines = console.Lines;
        var bottom = lines.Select((l, i) => (l, i)).Last(x => x.l.Contains('┘')).i;
        Assert.DoesNotContain("•", lines[bottom]);
        Assert.StartsWith("•", lines[bottom + 1].TrimStart());
    }

    [Fact]
    public void Wiring_diagram_box_borders_line_up()
    {
        // A stray space once pushed the DC REFERENCE OUTPUT line's right border out by one.
        var lines = Wiring.Diagram.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.EndsWith('│') || l.EndsWith('┐') || l.EndsWith('┘')).ToList();
        Assert.Single(lines.Select(l => l.Length).Distinct());
    }
}

/// <summary>The walkthrough's knob picker, driven with keys through Spectre's interactive TestConsole.</summary>
public class SimulatorKnobsTests
{
    private static Verify11683A.Instruments.Simulation.Simulated11683A Sim() =>
        new(CalibratorLimits.LoadDefault(), Verify11683A.Instruments.Simulation.SimulationScenario.Get("nominal"),
            new Verify11683A.SimulatedClock());

    [Fact]
    public void Enter_picks_the_first_range_still_to_measure()
    {
        var console = new TestConsole().Interactive();
        var sim = Sim();
        console.Input.PushKey(ConsoleKey.Enter);
        Verify11683A.App.SimulatorKnobs.ChooseRange(sim, ["30mW", "10mW"], console);
        Assert.Equal("30mW", sim.Range.Id);
    }

    [Fact]
    public void A_range_not_on_the_list_can_be_chosen_to_see_the_warning()
    {
        var console = new TestConsole().Interactive();
        var sim = Sim();
        // Remaining first (30 mW, 10 mW), then every other position from the top: 100 mW is third.
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        Verify11683A.App.SimulatorKnobs.ChooseRange(sim, ["30mW", "10mW"], console);
        Assert.Equal("100mW", sim.Range.Id);
    }

    [Fact]
    public void Other_switches_can_be_changed_before_choosing_the_range()
    {
        var console = new TestConsole().Interactive();
        var sim = Sim();
        // The last entry opens the other switches: choose POLARITY (4th), then REVERSE (2nd), then Done, then 30 mW.
        for (var i = 0; i < 10; i++)
            console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.Enter);
        Verify11683A.App.SimulatorKnobs.ChooseRange(sim, ["30mW", "10mW"], console);
        Assert.Equal(PolaritySwitch.Reverse, sim.Polarity);
        Assert.Equal("30mW", sim.Range.Id);
    }
}
