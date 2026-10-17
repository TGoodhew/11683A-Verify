using System.Text.Json;

namespace Verify11683A.Results;

/// <summary>Saves and loads <see cref="VerificationRun"/> files.</summary>
public static class ResultsFile
{
    /// <summary>11683A_SG52060005_20261017-1430: the model, the serial (made safe for a file name) and the start time.</summary>
    public static string BaseName(VerificationRun run)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var serial = new string(run.Unit.Serial.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray());
        if (serial == "")
            serial = "no-serial";
        return $"{run.Unit.Model}_{serial}_{run.Started:yyyyMMdd-HHmm}";
    }

    /// <summary>Writes the run to <paramref name="directory"/>; a second run in the same minute gets "-2", "-3" ….</summary>
    public static string Save(VerificationRun run, string directory)
    {
        Directory.CreateDirectory(directory);
        var baseName = BaseName(run);
        var path = Path.Combine(directory, baseName + ".json");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(directory, $"{baseName}-{n}.json");
        File.WriteAllText(path, Serialize(run));
        return path;
    }

    public static string Serialize(VerificationRun run) => JsonSerializer.Serialize(run, Json.Options);

    public static VerificationRun Deserialize(string json)
    {
        var run = JsonSerializer.Deserialize<VerificationRun>(json, Json.Options)
            ?? throw new InvalidDataException("The results file is empty.");
        if (run.Schema != VerificationRun.CurrentSchema)
            throw new InvalidDataException($"Unsupported results schema '{run.Schema}'; this version reads '{VerificationRun.CurrentSchema}'.");
        return run;
    }

    public static VerificationRun Load(string path) => Deserialize(File.ReadAllText(path));
}
