using System.Text.Json;
using System.Text.Json.Nodes;
using Verify11683A.Instruments.Dvm;
using Verify11683A.Measurement;
using Verify11683A.Specs;
using Verify11683A.Uncertainty;

namespace Verify11683A.App;

public sealed class CalibratorConfig
{
    public string Model { get; set; } = "11683A";
    public string Serial { get; set; } = "";
    public string Options { get; set; } = "";
}

public sealed class DvmConfig
{
    /// <summary>34401A or 3458A. Also selects data/dvm-specs-&lt;model&gt;.json.</summary>
    public string Model { get; set; } = "34401A";

    public string Resource { get; set; } = "GPIB0::22::INSTR";

    /// <summary>The 34401A reports serial 0 and the 3458A none, so it is entered here.</summary>
    public string Serial { get; set; } = "";

    public DateOnly? CalibrationDate { get; set; }
    public int CalibrationIntervalMonths { get; set; } = 12;

    /// <summary>Generous: the bus runs through an HP 37204A extender and NPLC 100 readings take seconds.</summary>
    public int TimeoutMs { get; set; } = 20000;

    /// <summary>The spec-table column used for uncertainty: 24h, 90d, 1y or 2y.</summary>
    public string SpecPeriod { get; set; } = "1y";

    public SpecPeriod Period => SpecPeriodExtensions.Parse(SpecPeriod);

    /// <summary>The DVM for the report: the bus identity, with the serial and calibration date from config.</summary>
    public DvmDetails Details(DvmIdentity identity) => new(identity.Manufacturer, identity.Model,
        DvmDetails.ChooseSerial(identity.Serial, Serial), identity.Firmware, CalibrationDate, CalibrationIntervalMonths);
}

public sealed class MeasurementConfig
{
    public double LineFrequencyHz { get; set; } = 60;
    public double Nplc { get; set; } = 100;
    public int ReadingsPerPoint { get; set; } = 10;

    /// <summary>Stability band half-width, ppm of reading (floors of 2 µV and 2 mΩ apply below it).</summary>
    public double SettlingBandPpm { get; set; } = SettlingCriteria.DefaultBandPpm;

    /// <summary>How long the readings must stay inside the band.</summary>
    public double SettlingHoldSeconds { get; set; } = 10;

    /// <summary>How long to wait for a point to settle before asking the operator.</summary>
    public double SettlingTimeoutSeconds { get; set; } = 120;

    public SettlingCriteria Settling(DvmFunction function) => SettlingCriteria.For(function) with
    {
        BandPpm = SettlingBandPpm,
        HoldTime = TimeSpan.FromSeconds(SettlingHoldSeconds),
        Timeout = TimeSpan.FromSeconds(SettlingTimeoutSeconds),
    };
}

public sealed class UncertaintyConfig
{
    public double CoverageFactor { get; set; } = 2;

    /// <summary>Rss (default) or Linear (worst case) for the two readings of a ratio.</summary>
    public RatioCombination RatioCombination { get; set; } = RatioCombination.Rss;

    public double MinimumTur { get; set; } = 4;

    public UncertaintyConvention Convention => new()
    {
        CoverageFactor = CoverageFactor,
        RatioCombination = RatioCombination,
        MinimumTur = MinimumTur,
    };
}

public sealed class PowerMeterConfig
{
    /// <summary>"manual" (readings typed in), or a model with a bus driver: E4418B, E4419B, 437B, 438A.</summary>
    public string Model { get; set; } = "manual";

    public string Resource { get; set; } = "GPIB0::13::INSTR";

    /// <summary>Channel (E4419B) to read.</summary>
    public int Channel { get; set; } = 1;

    public int TimeoutMs { get; set; } = 20000;
}

public sealed class DcSourceConfig
{
    /// <summary>"manual" (the operator sets it by hand), or a model with a bus driver: 5440B.</summary>
    public string Model { get; set; } = "manual";

    /// <summary>7 is the 5440B factory default address.</summary>
    public string Resource { get; set; } = "GPIB0::7::INSTR";

    /// <summary>Part 4: use the DIVIDER terminals at 2.2 V and below (finer, quieter; the operator moves the leads once).</summary>
    public bool UseDivider { get; set; } = true;

    public int TimeoutMs { get; set; } = 20000;
}

public sealed class WarmupConfig
{
    /// <summary>Minutes the 11683A must be on before testing. The DVM's figure comes from its spec table.</summary>
    public double CalibratorMinutes { get; set; } = WarmupPlan.DefaultCalibratorMinutes;
}

public sealed class AppConfig
{
    public string Company { get; set; } = "";
    public string Operator { get; set; } = "";
    public CalibratorConfig Calibrator { get; set; } = new();
    public DvmConfig Dvm { get; set; } = new();
    public MeasurementConfig Measurement { get; set; } = new();
    public UncertaintyConfig Uncertainty { get; set; } = new();
    public WarmupConfig Warmup { get; set; } = new();
    public PowerMeterConfig PowerMeter { get; set; } = new();
    public DcSourceConfig DcSource { get; set; } = new();
    public string ResultsDirectory { get; set; } = "results";

    /// <summary>
    /// Loads appsettings.json from the app directory (or <paramref name="path"/>), then overlays
    /// appsettings.local.json from the same directory if it exists. Keep bench-specific values
    /// (GPIB address, DVM serial and cal date) in the local file; git ignores it.
    /// </summary>
    public static AppConfig Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject ?? throw new InvalidDataException($"{path} is not a JSON object.");

        var local = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "appsettings.local.json");
        if (File.Exists(local))
        {
            var overlay = JsonNode.Parse(File.ReadAllText(local), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new InvalidDataException($"{local} is not a JSON object.");
            Merge(root, overlay);
        }

        var config = root.Deserialize<AppConfig>(Json.Options) ?? new AppConfig();
        _ = config.Dvm.Period;
        return config;
    }

    /// <summary>Copies every value in <paramref name="overlay"/> onto <paramref name="target"/>, recursing into objects.</summary>
    internal static void Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay)
        {
            var existingKey = target.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
            if (value is JsonObject child && target[existingKey] is JsonObject existing)
                Merge(existing, child);
            else
                target[existingKey] = value?.DeepClone();
        }
    }
}
