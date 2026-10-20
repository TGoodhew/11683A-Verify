using System.Globalization;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using Verify11683A.Calibrator;
using Verify11683A.Formatting;
using Verify11683A.Measurement;

namespace Verify11683A.App.Procedures;

/// <summary>
/// Part 3: the Power Supply Adjustment assist (11683A manual para 43–47, p. 8, PDF p. 19). RANGE 1 mW, FUNCTION
/// CALIBRATE, top cover off; A2R1 is adjusted for 145.00 ± 2.00 mV at DC REFERENCE OUTPUT while the app shows the
/// reading in large digits against the band.
/// </summary>
public static class SupplyAdjustmentProcedure
{
    /// <summary>The manual's warning at the head of its Adjustments section (p. 8), word for word.</summary>
    public const string CoversOffWarning =
        "The operations in this section require the instrument is operated with its covers removed and should only be carried out by qualified service personnel.";

    /// <summary>NPLC 10: about 0.4 s per reading at 6½ digits, quick enough to follow a trimmer.</summary>
    public const double Nplc = 10;

    /// <summary>Simulated A2R1 steps, as fractions of the supply: coarse (↑/↓) and fine (←/→).</summary>
    public const double CoarseStep = 1e-3, FineStep = 1e-4;

    /// <summary>Returns the final reading, or null if the operator declined the warning.</summary>
    public static AdjustmentReading? Run(VerificationSession session)
    {
        var limits = session.Limits.SupplyAdjustment;
        var sim = session.Bench.Simulator;
        var dvm = session.Dvm;

        session.Screens.Begin("Part 3", $"Part 3: {limits.Title} ({limits.Adjustment})");
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(limits.Source)}[/]");
        AnsiConsole.Write(new Panel(new Markup($"[bold red]WARNING[/]\n{Markup.Escape(CoversOffWarning)}"))
            .Border(BoxBorder.Heavy).BorderColor(Color.Red));
        if (!ConsoleUi.Confirm("Continue with the adjustment?", defaultValue: true))
            return null;

        AnsiConsole.Write(ConsoleUi.Settings("Set the 11683A",
            ("RANGE", "1 mW"), ("FUNCTION", "CALIBRATE"), ("POLARITY", "NORMAL"), ("REF", "INT"),
            ("DVM", "rear DC REFERENCE OUTPUT"), ("COVER", "top cover removed")));
        AnsiConsole.MarkupLine($"Adjust [bold]{Markup.Escape(limits.Adjustment)}[/] for {Si.Format(limits.TargetVolts, "V", 5)} "
            + $"± {Si.Format(limits.ToleranceVolts, "V", 3)}; aim for the centre.");
        session.Bench.Step("Press Enter when set", s =>
        {
            s.SetReference(ReferenceSwitch.Int);
            s.SetPolarity(PolaritySwitch.Normal);
            s.SetRange(limits.Range);
            s.SetFunction(FunctionSwitch.Calibrate);
        });

        dvm.Configure(DvmFunction.DcVoltage, 1, Nplc);
        var detector = new RangeDetector(DvmFunction.DcVoltage,
            [new ExpectedValue(limits.Range, limits.TargetVolts)], tolerance: 0.25);
        var recent = new Queue<double>();
        AdjustmentReading? last = null;
        var hint = sim is null
            ? "Press Enter when done."
            : "Simulated A2R1: ↑/↓ coarse, ←/→ fine. Press Enter when done.";

        if (!ConsoleUi.Interactive)
        {
            // Redirected: a fixed number of readings, printed.
            for (var i = 0; i < 10; i++)
            {
                last = Read(dvm, limits, recent);
                AnsiConsole.MarkupLine($"{Si.Format(last.Volts, "V", 7)}  {last.State}  ({Signed(last.Error * 1e3, "0.000")} mV)");
            }
        }
        else
        {
            AnsiConsole.Live(new Text("")).Start(ctx =>
            {
                while (true)
                {
                    last = Read(dvm, limits, recent);
                    var detection = detector.Detect(last.Volts, false);
                    ctx.UpdateTarget(View(last, recent, detection.IsMatch ? null : detection.Message, hint));
                    ctx.Refresh();

                    if (!Console.KeyAvailable)
                        continue;
                    var key = Console.ReadKey(intercept: true).Key;
                    if (key is ConsoleKey.Enter or ConsoleKey.Escape)
                        break;
                    if (sim is not null)
                    {
                        sim.SupplyScale *= 1 + key switch
                        {
                            ConsoleKey.UpArrow => CoarseStep,
                            ConsoleKey.DownArrow => -CoarseStep,
                            ConsoleKey.RightArrow => FineStep,
                            ConsoleKey.LeftArrow => -FineStep,
                            _ => 0,
                        };
                        recent.Clear();
                    }
                }
            });
        }

        var final = AdjustmentReading.For(recent.Count > 0 ? recent.Average() : last!.Volts, limits);
        session.Log.Write($"Power Supply Adjustment finished at {final.Volts} V ({final.State}, error {final.Error} V)");
        session.Screens.Report(() => AnsiConsole.MarkupLine($"Power Supply Adjustment finished at [bold]{Si.Format(final.Volts, "V", 6)}[/] "
            + $"({Signed(final.Error * 1e3, "0.000")} mV): {StateMarkup(final.State)}."));
        return final;
    }

    private static AdjustmentReading Read(Instruments.Dvm.IDvm dvm, SupplyAdjustmentLimits limits, Queue<double> recent)
    {
        var r = dvm.Read();
        var volts = r.Overload ? double.PositiveInfinity : r.Value;
        if (!r.Overload)
        {
            recent.Enqueue(r.Value);
            while (recent.Count > 5)
                recent.Dequeue();
        }
        return AdjustmentReading.For(volts, limits);
    }

    /// <summary>The live view: the reading in large digits, the gauge, the error and which way to turn.</summary>
    public static IRenderable View(AdjustmentReading reading, IEnumerable<double> recent, string? warning, string hint)
    {
        var colour = reading.State switch
        {
            AdjustmentState.Centred => Color.Green,
            AdjustmentState.InTolerance => Color.Yellow,
            _ => Color.Red,
        };
        var digits = double.IsInfinity(reading.Volts)
            ? "OVERLOAD"
            : (reading.Volts * 1e3).ToString("0.000", CultureInfo.InvariantCulture) + " mV";
        var history = recent.ToList();
        var spread = history.Count > 1 ? history.Max() - history.Min() : 0;
        var rows = new List<IRenderable>
        {
            new FigletText(digits).Color(colour),
            new Markup(Markup.Escape(Gauge(reading))),
            new Markup($"Target {Si.Format(reading.Target, "V", 5)} ± {Si.Format(reading.Tolerance, "V", 3)}   "
                + $"error [bold]{Signed(reading.Error * 1e3, "0.000")} mV[/] ({Signed(reading.ErrorPpm, "0")} ppm)   "
                + $"{StateMarkup(reading.State)}: {Markup.Escape(reading.Advice)}"),
            new Markup($"[grey]last {history.Count} readings span {Si.Format(spread, "V", 2)}[/]"),
        };
        if (warning is not null)
            rows.Add(new Markup($"[yellow]⚠ {Markup.Escape(warning)}[/]"));
        rows.Add(new Markup($"[blue]{Markup.Escape(hint)}[/]"));
        return new Rows(rows);
    }

    /// <summary>A 61-character bar from −1.5 to +1.5 tolerances: limits at ±1, the target in the middle.</summary>
    public static string Gauge(AdjustmentReading reading)
    {
        const int width = 61;
        const double span = 1.5;
        int Column(double position) => (int)Math.Round((Math.Clamp(position, -span, span) + span) / (2 * span) * (width - 1));
        var bar = new StringBuilder(new string('─', width));
        bar[Column(-1)] = '|';
        bar[Column(1)] = '|';
        bar[Column(0)] = '┼';
        var pointer = new StringBuilder(new string(' ', width));
        pointer[Column(reading.Position)] = Math.Abs(reading.Position) > span ? (reading.Position < 0 ? '◄' : '►') : '▲';
        var lo = Si.Format(reading.Target - reading.Tolerance, "V", 5);
        var hi = Si.Format(reading.Target + reading.Tolerance, "V", 5);
        var labels = new StringBuilder(new string(' ', width + 12));
        labels.Insert(Math.Max(0, Column(-1) - lo.Length / 2), lo);
        var hiAt = Math.Max(0, Column(1) - hi.Length / 2);
        var line = labels.ToString().TrimEnd();
        line = line.Length < hiAt ? line.PadRight(hiAt) + hi : line + "  " + hi;
        return bar + "\n" + pointer.ToString().TrimEnd() + "\n" + line;
    }

    private static string StateMarkup(AdjustmentState state) => state switch
    {
        AdjustmentState.Centred => "[green bold]CENTRED[/]",
        AdjustmentState.InTolerance => "[yellow bold]IN TOLERANCE[/]",
        AdjustmentState.Low => "[red bold]LOW[/]",
        _ => "[red bold]HIGH[/]",
    };

    private static string Signed(double value, string format) =>
        (value >= 0 ? "+" : "") + value.ToString(format, CultureInfo.InvariantCulture);
}
