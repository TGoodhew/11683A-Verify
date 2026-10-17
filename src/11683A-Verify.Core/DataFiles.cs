namespace Verify11683A;

/// <summary>Locates the JSON data files (limits, DVM spec tables) deployed under data/ next to the app.</summary>
public static class DataFiles
{
    public const string LimitsFileName = "limits-11683A.json";

    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "data");

    public static string SpecFileName(string dvmModel) => $"dvm-specs-{dvmModel}.json";

    public static string Resolve(string fileName, string? directory = null)
    {
        var path = Path.Combine(directory ?? DefaultDirectory, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Data file not found: {path}", path);
        return path;
    }
}
