using System.Text.Encodings.Web;
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
        // Results files are read by people, not embedded in HTML: keep "Ω", "µ" and "+" as they are.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // A TUR is NaN with no limits and ∞ with U = 0; results files must hold both.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <exception cref="InvalidDataException">The file is not valid JSON for <typeparamref name="T"/>; the message names it.</exception>
    public static T Load<T>(string path)
    {
        using var stream = File.OpenRead(path);
        try
        {
            return JsonSerializer.Deserialize<T>(stream, Options)
                ?? throw new InvalidDataException($"{path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
    }
}
