namespace Verify11683A.Instruments.Simulation;

/// <summary>How the simulated 11683A departs from nominal: supply setting, per-range errors, noise and drift.</summary>
public sealed record SimulationScenario
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Scales every CALIBRATE output voltage, as A2R1 does. 0.98 puts 1 mW at about 142.1 mV.</summary>
    public double SupplyScale { get; init; } = 1.0;

    /// <summary>Fractional error on the CALIBRATE voltage of individual ranges, keyed by range id.</summary>
    public IReadOnlyDictionary<string, double> VoltageErrors { get; init; } = new Dictionary<string, double>();

    /// <summary>Fractional error on the STANDBY four-wire resistance of individual ranges, keyed by range id.</summary>
    public IReadOnlyDictionary<string, double> OhmsErrors { get; init; } = new Dictionary<string, double>();

    /// <summary>Random noise as parts per million of the value (1 sigma).</summary>
    public double NoisePpm { get; init; } = 1.0;

    public double NoiseFloorVolts { get; init; } = 0.3e-6;
    public double NoiseFloorOhms { get; init; } = 0.3e-3;

    /// <summary>Linear drift of every output, ppm per minute since the simulation started.</summary>
    public double DriftPpmPerMinute { get; init; }

    /// <summary>Size of the thermal tail after a knob is turned, ppm of the new value.</summary>
    public double SettlingTailPpm { get; init; } = 300;

    /// <summary>Fractional error of the Option H01 REF EXT path against Table 2-4 (Part 4).</summary>
    public double ExtTransferError { get; init; }

    /// <summary>Time constant of that tail. At NPLC 100 it falls below 20 ppm after about five readings.</summary>
    public double SettlingTauSeconds { get; init; } = 5.0;

    public static readonly IReadOnlyList<SimulationScenario> All =
    [
        new()
        {
            Name = "nominal",
            Description = "Every range at the centre of its limits; 1 ppm noise.",
        },
        new()
        {
            Name = "supply-low",
            Description = "Supply set 2% low: 1 mW reads about 142.1 mV (limit 143.00 mV); ratios still pass.",
            SupplyScale = 0.98,
        },
        new()
        {
            Name = "ratio-fail",
            Description = "10 mW range 0.6% high, so its ratio (about 10.727) falls below the 10.768 limit.",
            VoltageErrors = new Dictionary<string, double> { ["10mW"] = 0.006 },
        },
        new()
        {
            Name = "ohms-fail",
            Description = "3 µW range resistance 0.4% high (about 31.78 Ω; limit 31.718 Ω).",
            OhmsErrors = new Dictionary<string, double> { ["3uW"] = 0.004 },
        },
        new()
        {
            Name = "noisy",
            Description = "30 ppm noise with a 5 µV / 5 mΩ floor: slow to settle, wider σ.",
            NoisePpm = 30,
            NoiseFloorVolts = 5e-6,
            NoiseFloorOhms = 5e-3,
        },
        new()
        {
            Name = "drifting",
            Description = "Outputs drift 15 ppm per minute, as if the 11683A had not warmed up.",
            DriftPpmPerMinute = 15,
        },
    ];

    public static SimulationScenario Get(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException(
            $"Unknown simulation scenario '{name}'. Choose from: {string.Join(", ", All.Select(s => s.Name))}.");
}
