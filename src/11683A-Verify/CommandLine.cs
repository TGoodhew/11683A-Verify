namespace Verify11683A.App;

/// <summary>Parsed command line. Options override the matching appsettings values.</summary>
public sealed class CommandLine
{
    public string? Command { get; private set; }
    public bool Help { get; private set; }
    public string? ConfigPath { get; private set; }

    /// <summary>Non-null when --simulate was given: the scenario name ("nominal" if none was named).</summary>
    public string? SimulateScenario { get; private set; }

    public string? DvmModel { get; private set; }
    public string? Resource { get; private set; }
    public string? SpecPeriod { get; private set; }
    public double? AmbientC { get; private set; }
    public bool SkipWarmup { get; private set; }

    /// <summary>meter-test: the meter model (a data/meters/&lt;model&gt;.json definition).</summary>
    public string? Meter { get; private set; }

    /// <summary>meter-test: the power meter's VISA resource (overrides powerMeter.resource).</summary>
    public string? MeterResource { get; private set; }

    /// <summary>verify: run Part 4 without asking.</summary>
    public bool Part4 { get; private set; }

    /// <summary>A results file to rebuild the PDF report from (--regen-pdf).</summary>
    public string? RegeneratePdf { get; private set; }

    /// <summary>A results file to write 11683A-actuals.json from (--export-actuals).</summary>
    public string? ExportActuals { get; private set; }

    public bool IsSimulation => SimulateScenario is not null;

    public const string Usage = """
        Usage: 11683A-Verify [command] [options]

        Commands:
          dvm-check            Step 1 bench check: identify the DVM, configure every range the test uses,
                               read each setting back, check the error queue, then optionally take
                               readings of the 11683A at 1 mW.
          verify               The Range Switch Performance Test: Part 1, then Part 2.
          part1                Part 1: DC voltage at every range from 100 mW to 300 uW and the ratios to
                               100 mW, with uncertainty, TUR and verdicts.
          part2                Part 2: four-wire ohms from 300 uW to 3 uW with FUNCTION in STANDBY. No ohms
                               current unless a DC reading first shows the output is grounded.
          part4                Part 4: Option H01 REF EXT transfer check against a power meter
                               (informational; E4418B Service Guide procedure 2).
          meter-test           Power meter performance tests with the 11683A: 435A, 435B, 436A, 437B, 438A,
                               E4418B (definitions in data/meters). Menu choice, or --meter <model>.
          adjust               Part 3: Power Supply Adjustment assist. Live reading of the 1 mW output while
                               A2R1 is set to 145.00 +/- 2.00 mV, then an offer to rerun Part 1.
                               (In simulation, the arrow keys turn the simulated A2R1.)
          wiring               Show the wiring diagram and the 4-wire cable rules.
          spec-table           Show the DVM's spec table, and the spec limit and TUR at every 11683A test point.
          (none)               Menu.

        Options:
          --simulate[=name]    Use the simulated 11683A, DVM and (Part 4) power meter. Scenarios: nominal
                               (default), supply-low, ratio-fail, ohms-fail, noisy, h01-ext-error,
                               drifting, rear-terminals. See the README's Simulation section.
          --dvm <model>        34401A or 3458A (overrides dvm.model).
          --resource <name>    VISA resource, e.g. GPIB0::22::INSTR (overrides dvm.resource).
          --period <p>         Spec column: 24h, 90d, 1y or 2y (overrides dvm.specPeriod).
          --ambient <degC>     Ambient temperature, for the temperature coefficient outside 18-28 C.
                               Asked for at the start of a test if not given.
          --part4              verify: also run Part 4 without asking.
          --meter <model>      meter-test: the power meter, e.g. 437B.
          --meter-resource <r> meter-test: its VISA resource (overrides powerMeter.resource).
          --skip-warmup        Don't ask about or time the warm-up (it is then not recorded).
          --regen-pdf <json>   Rebuild the PDF report from a saved results file, beside it, and exit.
          --export-actuals <json>
                               Write 11683A-actuals.json (for the power meter app) beside a saved, passing
                               results file, and exit.
          --config <path>      appsettings.json to use.
          -h, --help           This help.
        """;

    public static CommandLine Parse(string[] args)
    {
        var result = new CommandLine();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Value() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"{arg} needs a value.");

            switch (arg)
            {
                case "-h" or "--help" or "/?":
                    result.Help = true;
                    break;
                case "--simulate":
                    result.SimulateScenario = "nominal";
                    break;
                case var s when s.StartsWith("--simulate=", StringComparison.Ordinal):
                    result.SimulateScenario = s["--simulate=".Length..];
                    break;
                case "--dvm":
                    result.DvmModel = Value();
                    break;
                case "--resource":
                    result.Resource = Value();
                    break;
                case "--period":
                    result.SpecPeriod = Value();
                    break;
                case "--ambient":
                    result.AmbientC = double.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--export-actuals":
                    result.ExportActuals = Value();
                    break;
                case "--regen-pdf":
                    result.RegeneratePdf = Value();
                    break;
                case "--meter":
                    result.Meter = Value();
                    break;
                case "--meter-resource":
                    result.MeterResource = Value();
                    break;
                case "--part4":
                    result.Part4 = true;
                    break;
                case "--skip-warmup":
                    result.SkipWarmup = true;
                    break;
                case "--config":
                    result.ConfigPath = Value();
                    break;
                case var c when !c.StartsWith('-') && result.Command is null:
                    result.Command = c.ToLowerInvariant();
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.");
            }
        }
        return result;
    }

    public void ApplyTo(AppConfig config)
    {
        if (DvmModel is not null) config.Dvm.Model = DvmModel;
        if (Resource is not null) config.Dvm.Resource = Resource;
        if (SpecPeriod is not null) config.Dvm.SpecPeriod = SpecPeriod;
        _ = config.Dvm.Period;
    }
}
