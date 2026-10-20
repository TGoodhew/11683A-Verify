using Spectre.Console;

namespace Verify11683A.App;

/// <summary>
/// Puts each step of a run on its own cleared screen, under a fixed header: what is being tested, and a tracker of the steps.
/// At a real console the run uses the terminal's alternate screen, as a full-screen program does. <see cref="Finish"/>
/// brings the normal screen back and writes there the results kept with <see cref="Report"/>, so they stay in the
/// scrollback. Redirected (scripts and tests), each step is a heading in the scrolling output instead.
/// </summary>
public sealed class Screens : IDisposable
{
    private const string EnterAlternate = "\u001b[?1049h\u001b[H", LeaveAlternate = "\u001b[?1049l";

    private readonly string _title;
    private readonly List<string> _steps;
    private readonly Dictionary<string, StepState> _states = [];
    private readonly List<Action> _kept = [];
    private string? _current;
    private string _heading = "";
    private bool _alternate;
    private bool _titleWritten;

    /// <param name="title">The run, e.g. "11683A verification — verify".</param>
    /// <param name="steps">The steps in order, as the tracker shows them.</param>
    /// <param name="clearing">Clear between steps; by default only at a real console.</param>
    public Screens(string title, IEnumerable<string> steps, bool? clearing = null)
    {
        _title = title;
        _steps = steps.ToList();
        Clearing = clearing ?? ConsoleUi.Interactive;
    }

    /// <summary>Each step on a cleared screen (a real console); otherwise steps are headings in scrolling output.</summary>
    public bool Clearing { get; }

    /// <summary>The instruments in use, under the title: set once they are identified.</summary>
    public string Identity { get; set; } = "";

    public enum StepState { Pending, Current, Done, Skipped }

    public StepState State(string step) => _states.GetValueOrDefault(step);

    /// <summary>Replaces the steps not yet started, e.g. once the operator has chosen which tests to run.</summary>
    public void SetSteps(IEnumerable<string> steps)
    {
        var started = _steps.Where(s => State(s) != StepState.Pending);
        _steps.Clear();
        _steps.AddRange(started.Concat(steps.Where(s => State(s) == StepState.Pending)).Distinct());
    }

    /// <summary>
    /// Starts a screen for <paramref name="step"/>: the screen is cleared and the header redrawn, with
    /// <paramref name="heading"/> (plain text; the step's name by default) under it. The step before it is marked done.
    /// </summary>
    public void Begin(string step, string? heading = null)
    {
        if (!_steps.Contains(step))
            _steps.Add(step);
        if (_current is { } previous && previous != step && State(previous) == StepState.Current)
            _states[previous] = StepState.Done;
        _current = step;
        _states[step] = StepState.Current;
        _heading = heading ?? step;

        if (!Clearing)
        {
            if (!_titleWritten)
            {
                AnsiConsole.Write(new Rule(TitleMarkup()).LeftJustified());
                _titleWritten = true;
            }
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(_heading)}[/]").LeftJustified());
            return;
        }

        if (!_alternate)
        {
            AnsiConsole.Write(new ControlCode(EnterAlternate));
            _alternate = true;
        }
        Redraw();
    }

    /// <summary>Clears the screen and redraws the header and the current step's heading. Does nothing redirected.</summary>
    public void Redraw()
    {
        if (!Clearing)
            return;
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine(TitleMarkup());
        AnsiConsole.MarkupLine(TrackerMarkup());
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(_heading)}[/]").LeftJustified());
    }

    /// <summary>Marks a step as not run (the tracker strikes it through).</summary>
    public void Skip(string step)
    {
        if (!_steps.Contains(step))
            _steps.Add(step);
        _states[step] = StepState.Skipped;
    }

    /// <summary>Writes results now and, at a real console, again on the normal screen when the run ends.</summary>
    public void Report(Action write)
    {
        write();
        if (_alternate)
            _kept.Add(write);
    }

    /// <summary>Lets the operator read the screen before the next one clears it. Only at a real console.</summary>
    public void Pause(string prompt = "Press Enter to continue")
    {
        if (!Clearing)
            return;
        AnsiConsole.WriteLine();
        ConsoleUi.Prompt(prompt);
    }

    /// <summary>
    /// Ends the full-screen part of the run: back to the normal screen, where the kept results are written again.
    /// Anything written after this (the summary, where the results went) stays in the scrollback.
    /// </summary>
    public void Finish()
    {
        if (_current is { } current && State(current) == StepState.Current)
            _states[current] = StepState.Done;
        if (!_alternate)
            return;
        AnsiConsole.Write(new ControlCode(LeaveAlternate));
        _alternate = false;
        AnsiConsole.Write(new Rule(TitleMarkup()).LeftJustified());
        foreach (var write in _kept)
        {
            write();
            AnsiConsole.WriteLine();
        }
        _kept.Clear();
    }

    /// <summary>The tracker line: done steps ticked, the current one highlighted, skipped ones struck through.</summary>
    public string TrackerMarkup() => string.Join("[grey] › [/]", _steps.Select(s => State(s) switch
    {
        StepState.Done => $"[green]✓[/] [grey]{Markup.Escape(s)}[/]",
        StepState.Current => $"[black on yellow] {Markup.Escape(s)} [/]",
        StepState.Skipped => $"[grey strikethrough]{Markup.Escape(s)}[/]",
        _ => $"[grey]{Markup.Escape(s)}[/]",
    }));

    private string TitleMarkup() =>
        $"[bold]{Markup.Escape(_title)}[/]" + (Identity == "" ? "" : $"  [grey]{Markup.Escape(Identity)}[/]");

    /// <summary>Leaves the alternate screen if the run ended early, so an error is written where it can be read.</summary>
    public void Dispose() => Finish();
}
