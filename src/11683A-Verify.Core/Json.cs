using System.Text.Json;
using System.Text.Json.Serialization;

namespace Verify11683A;

/// <summary>Shared JSON settings for the data files and the results files.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options)
            ?? throw new InvalidDataException($"{path} is empty.");
    }
}
