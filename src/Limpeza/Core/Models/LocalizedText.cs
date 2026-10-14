using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrashSweepr.Limpeza.Core.Models;

/// <summary>
/// Texto com tradução por cultura. No JSON pode ser uma string simples (assume pt-BR)
/// ou um objeto { "pt-BR": "...", "en-US": "..." }.
/// </summary>
[JsonConverter(typeof(LocalizedTextConverter))]
public sealed class LocalizedText
{
    public const string DefaultCulture = "pt-BR";

    public LocalizedText(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, string> Values { get; }

    public bool HasDefault =>
        Values.TryGetValue(DefaultCulture, out var text) && !string.IsNullOrWhiteSpace(text);

    public static LocalizedText FromDefault(string text) =>
        new(new Dictionary<string, string> { [DefaultCulture] = text });

    /// <summary>Resolve o texto: cultura exata, idioma neutro, pt-BR, e por fim qualquer valor disponível.</summary>
    public string Resolve(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;

        if (Values.TryGetValue(culture.Name, out var exact))
        {
            return exact;
        }

        var neutral = culture.TwoLetterISOLanguageName;
        foreach (var (key, value) in Values)
        {
            if (key.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase)
                || key.Equals(neutral, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        if (Values.TryGetValue(DefaultCulture, out var fallback))
        {
            return fallback;
        }

        return Values.Values.FirstOrDefault() ?? string.Empty;
    }

    public override string ToString() => Resolve(CultureInfo.GetCultureInfo(DefaultCulture));
}

internal sealed class LocalizedTextConverter : JsonConverter<LocalizedText>
{
    public override LocalizedText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return LocalizedText.FromDefault(reader.GetString() ?? string.Empty);

            case JsonTokenType.StartObject:
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var culture = reader.GetString() ?? string.Empty;
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new JsonException($"O texto da cultura '{culture}' deve ser uma string.");
                    }

                    values[culture] = reader.GetString() ?? string.Empty;
                }

                return new LocalizedText(values);

            default:
                throw new JsonException("Texto localizado deve ser uma string ou um objeto { cultura: texto }.");
        }
    }

    public override void Write(Utf8JsonWriter writer, LocalizedText value, JsonSerializerOptions options)
    {
        if (value.Values.Count == 1 && value.HasDefault)
        {
            writer.WriteStringValue(value.Values[LocalizedText.DefaultCulture]);
            return;
        }

        writer.WriteStartObject();
        foreach (var (key, text) in value.Values)
        {
            writer.WriteString(key, text);
        }

        writer.WriteEndObject();
    }
}
