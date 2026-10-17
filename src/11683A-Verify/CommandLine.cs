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

    public bool IsSimulation => SimulateScenario is not null;

    public const string Usage = """
        Usage: 11683A-Verify [command] [options]

        Commands:
          dvm-check            Step 1 bench check: identify the DVM, configure every range the test uses,
                               read each setting back, check the error queue, then optionally take
                               readings of the 11683A at 1 mW.
          part1                Part 1: DC voltage at every range from 100 mW to 300 uW and the ratios to
                               100 mW, with uncertainty, TUR and verdicts.
          spec-table           Show the DVM's spec table, and the spec limit and TUR at every 11683A test point.
          (none)               Menu.

        Options:
          --simulate[=name]    Use the simulated 11683A and DVM. Scenarios: nominal (default), supply-low,
                               ratio-fail, ohms-fail, noisy, drifting.
          --dvm <model>        34401A or 3458A (overrides dvm.model).
          --resource <name>    VISA resource, e.g. GPIB0::22::INSTR (overrides dvm.resource).
          --period <p>         Spec column: 24h, 90d, 1y or 2y (overrides dvm.specPeriod).
          --ambient <degC>     Ambient temperature, for the temperature coefficient outside 18-28 C.
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
