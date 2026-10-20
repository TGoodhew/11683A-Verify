using Spectre.Console.Testing;
using Verify11683A.App;

namespace Verify11683A.Tests;

/// <summary>The step screens: the tracker, headings when redirected, and results kept for the normal screen.</summary>
public class ScreensTests
{
    private static TestConsole Console()
    {
        var console = new TestConsole().Width(200);
        Spectre.Console.AnsiConsole.Console = console;
        return console;
    }

    [Fact]
    public void Tracker_ticks_done_steps_highlights_the_current_one_and_strikes_skipped_ones()
    {
        Console();
        var screens = new Screens("Run", ["Setup", "Part 1", "Part 2", "Part 4", "Results"], clearing: false);
        screens.Begin("Setup");
        screens.Begin("Part 1");
        screens.Skip("Part 4");

        Assert.Equal(Screens.StepState.Done, screens.State("Setup"));
        Assert.Equal(Screens.StepState.Current, screens.State("Part 1"));
        Assert.Equal(Screens.StepState.Pending, screens.State("Part 2"));
        Assert.Equal(Screens.StepState.Skipped, screens.State("Part 4"));
        var tracker = screens.TrackerMarkup();
        Assert.Contains("[green]✓[/] [grey]Setup[/]", tracker);
        Assert.Contains("[black on yellow] Part 1 [/]", tracker);
        Assert.Contains("[grey strikethrough]Part 4[/]", tracker);
        Assert.True(tracker.IndexOf("Setup", StringComparison.Ordinal) < tracker.IndexOf("Results", StringComparison.Ordinal));
    }

    [Fact]
    public void Redirected_each_step_is_a_heading_and_results_are_written_once()
    {
        var console = Console();
        var screens = new Screens("11683A verification — verify", ["Setup", "Part 1"], clearing: false) { Identity = "34401A at GPIB0::17::INSTR" };
        screens.Begin("Setup", "Setup: instruments");
        screens.Begin("Part 1", "Part 1 results");
        screens.Report(() => Spectre.Console.AnsiConsole.MarkupLine("TABLE"));
        screens.Pause();
        screens.Finish();

        var output = console.Output;
        Assert.Contains("11683A verification — verify", output);
        Assert.Contains("GPIB0::17::INSTR", output);
        Assert.Contains("Setup: instruments", output);
        Assert.Contains("Part 1 results", output);
        Assert.Single(output.Split('\n'), l => l.Contains("TABLE"));
        Assert.DoesNotContain("Press Enter", output);
    }

    [Fact]
    public void At_a_console_kept_results_are_written_again_on_the_normal_screen()
    {
        var console = Console();
        var screens = new Screens("Run", ["Part 1", "Part 2"], clearing: true);
        screens.Begin("Part 1");
        screens.Report(() => Spectre.Console.AnsiConsole.MarkupLine("PART 1 TABLE"));
        Spectre.Console.AnsiConsole.MarkupLine("transient");
        screens.Begin("Part 2");
        screens.Finish();
        Spectre.Console.AnsiConsole.MarkupLine("SUMMARY");

        var output = console.Output;
        Assert.Equal(2, output.Split('\n').Count(l => l.Contains("PART 1 TABLE")));
        Assert.Single(output.Split('\n'), l => l.Contains("transient"));
        Assert.True(output.LastIndexOf("PART 1 TABLE", StringComparison.Ordinal) < output.IndexOf("SUMMARY", StringComparison.Ordinal));
        Assert.Equal(Screens.StepState.Done, screens.State("Part 2"));
    }

    [Fact]
    public void Finish_twice_writes_the_kept_results_once()
    {
        var console = Console();
        var screens = new Screens("Run", ["A"], clearing: true);
        screens.Begin("A");
        screens.Report(() => Spectre.Console.AnsiConsole.MarkupLine("KEPT"));
        screens.Finish();
        screens.Dispose();
        Assert.Equal(2, console.Output.Split('\n').Count(l => l.Contains("KEPT")));
    }

    [Fact]
    public void Steps_chosen_later_go_in_before_results()
    {
        Console();
        var screens = new Screens("Meter", ["Setup", "Results"], clearing: false);
        screens.Begin("Setup");
        screens.SetSteps(["Zero Carryover", "Instrument Accuracy", "Results"]);
        var tracker = screens.TrackerMarkup();
        Assert.True(tracker.IndexOf("Setup", StringComparison.Ordinal) < tracker.IndexOf("Zero Carryover", StringComparison.Ordinal));
        Assert.True(tracker.IndexOf("Instrument Accuracy", StringComparison.Ordinal) < tracker.IndexOf("Results", StringComparison.Ordinal));
    }
}
