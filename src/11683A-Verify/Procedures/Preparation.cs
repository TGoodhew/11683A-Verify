using System.Globalization;
using Spectre.Console;
using Verify11683A.App.Reports;
using Verify11683A.Measurement;

namespace Verify11683A.App.Procedures;

/// <summary>
/// Before the first part: the ambient temperature (on the Setup screen), the wiring diagram (its own screen), and the
/// warm-up countdown (its own screen, when it is timed).
/// </summary>
public static class Preparation
{
    public static void Run(VerificationSession session)
    {
        AskAmbient(session);

        session.Screens.Begin("Wiring", "Wiring: DVM to the 11683A DC REFERENCE OUTPUT");
        Wiring.Write(heading: false);
        var timed = !(session.Bench.IsSimulation || session.Options.SkipWarmup || Console.IsInputRedirected);
        if (!timed)
        {
            session.Screens.Skip("Warm-up");
            if (session.Bench.Walkthrough)
                AnsiConsole.MarkupLine("[grey]Simulator: the warm-up countdown is skipped (on the bench it asks how long each instrument has been on and counts down).[/]");
        }
        session.Screens.Pause("Press Enter when the DVM is connected as shown");

        if (timed)
            session.Screens.Begin("Warm-up");
        WarmUp(session);
    }

    private static void AskAmbient(VerificationSession session)
    {
        if (session.AmbientC is not null || (session.Bench.IsSimulation && !session.Bench.Walkthrough) || Console.IsInputRedirected)
            return;
        var band = session.Specs.CalibrationBandC;
        var text = AnsiConsole.Prompt(new TextPrompt<string>(
                $"Ambient temperature in °C [grey](Enter to skip; outside {band[0]:0}–{band[1]:0} °C adds the temperature coefficient)[/]:")
            .AllowEmpty()
            .Validate(t => t.Trim() == "" || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is > -20 and < 60
                ? ValidationResult.Success()
                : ValidationResult.Error("Enter a temperature such as 23.5, or nothing.")));
        if (text.Trim() != "")
            session.AmbientC = double.Parse(text, CultureInfo.InvariantCulture);
        session.Log.Write($"Ambient: {(session.AmbientC is { } a ? a.ToString(CultureInfo.InvariantCulture) + " °C" : "not entered")}");
    }

    private static void WarmUp(VerificationSession session)
    {
        var required = session.Config.Warmup;
        var dvmModel = session.Specs.Model;
        if (session.Bench.IsSimulation || session.Options.SkipWarmup || Console.IsInputRedirected)
        {
            session.Log.Write(session.Bench.IsSimulation ? "Warm-up: simulated, not timed." : "Warm-up: skipped.");
            return;
        }

        var plan = new WarmupPlan(required.CalibratorMinutes, session.Specs.WarmupMinutes,
            AskMinutes("How many minutes has the 11683A been switched on?", required.CalibratorMinutes),
            AskMinutes($"How many minutes has the {dvmModel} been switched on?", session.Specs.WarmupMinutes));
        var start = DateTimeOffset.Now;
        var cutShort = false;

        if (plan.Remaining > TimeSpan.Zero)
        {
            AnsiConsole.MarkupLine($"Waiting {plan.Remaining.TotalMinutes:0} min for {Markup.Escape(plan.Limiting(dvmModel))} to warm up "
                + $"[grey](11683A {required.CalibratorMinutes:0} min; {Markup.Escape(dvmModel)} {session.Specs.WarmupMinutes} min per its specifications). "
                + "Press S to skip.[/]");
            var total = plan.Remaining.TotalSeconds;
            AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new RemainingTimeColumn())
                .Start(ctx =>
                {
                    var task = ctx.AddTask("Warming up", maxValue: total);
                    while (!task.IsFinished)
                    {
                        if (Console.KeyAvailable && char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar) == 'S')
                        {
                            cutShort = true;
                            break;
                        }
                        Thread.Sleep(250);
                        task.Value = Math.Min(total, (DateTimeOffset.Now - start).TotalSeconds);
                    }
                });
        }

        var done = plan.After(DateTimeOffset.Now - start);
        session.WarmupMinutes = done.CalibratorOnMinutes;
        session.DvmWarmupMinutes = done.DvmOnMinutes;
        session.WarmupCutShort = cutShort;
        session.Log.Write($"Warm-up: 11683A on {done.CalibratorOnMinutes:0.0} min, {dvmModel} on {done.DvmOnMinutes:0.0} min{(cutShort ? ", cut short by the operator" : "")}");
        if (cutShort)
            AnsiConsole.MarkupLine($"[yellow]Warm-up cut short: {done.Remaining.TotalMinutes:0} min short. The report will say so.[/]");
        AnsiConsole.WriteLine();
    }

    private static double AskMinutes(string question, double warmedUp) =>
        AnsiConsole.Prompt(new TextPrompt<double>($"{Markup.Escape(question)} [grey](Enter = {warmedUp:0}, already warm)[/]")
            .DefaultValue(warmedUp).HideDefaultValue()
            .Validate(v => v >= 0 ? ValidationResult.Success() : ValidationResult.Error("Minutes cannot be negative.")));
}
