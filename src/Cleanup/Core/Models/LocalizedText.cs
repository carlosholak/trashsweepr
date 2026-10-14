using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrashSweepr.Cleanup.Core.Models;

/// <summary>
/// Texto com tradução por cultura. No JSON é um objeto { "en": "...", "pt-BR": "..." } ou, por compatibilidade
/// com catálogos antigos, uma string simples, que vale como pt-BR (<see cref="DefaultCulture"/>).
/// </summary>
[JsonConverter(typeof(LocalizedTextConverter))]
public sealed class LocalizedText
{
    /// <summary>Cultura de uma string simples no JSON (o catálogo nasceu em português).</summary>
    public const string DefaultCulture = "pt-BR";

    /// <summary>Língua de reserva quando a interface está numa língua sem tradução.</summary>
    public const string EnglishCulture = "en";

    public LocalizedText(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>Tem texto não vazio em pt-BR.</summary>
    public bool HasDefault => HasText(DefaultCulture);

    /// <summary>Tem texto não vazio em ao menos uma das culturas que o catálogo usa ("en" ou "pt-BR").</summary>
    public bool HasSupportedText => HasText(EnglishCulture) || HasText(DefaultCulture);

    public static LocalizedText FromDefault(string text) =>
        new(new Dictionary<string, string> { [DefaultCulture] = text });

    /// <summary>Tem texto não vazio exatamente nesta cultura (ex.: "en", "pt-BR").</summary>
    public bool HasText(string culture) => Values.TryGetValue(culture, out var text) && IsUsable(text);

    /// <summary>
    /// Resolve o texto, ignorando valores vazios: cultura exata (pt-BR), mesma língua (pt-PT usa pt-BR; en-GB usa en),
    /// inglês, pt-BR e, por fim, qualquer valor disponível.
    /// </summary>
    public string Resolve(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;

        return Exact(culture.Name)
            ?? SameLanguage(culture.TwoLetterISOLanguageName)
            ?? Exact(EnglishCulture)
            ?? SameLanguage("en")
            ?? Exact(DefaultCulture)
            ?? SameLanguage("pt")
            ?? Values.Values.FirstOrDefault(IsUsable)
            ?? string.Empty;
    }

    public override string ToString() => Resolve();

    private string? Exact(string culture) =>
        culture.Length > 0 && Values.TryGetValue(culture, out var text) && IsUsable(text) ? text : null;

    // A língua neutra ("pt") vem antes das variantes; entre variantes, a ordem alfabética deixa o resultado estável.
    private string? SameLanguage(string language) =>
        Exact(language)
        ?? Values
            .Where(v => IsLanguage(v.Key, language) && IsUsable(v.Value))
            .OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .Select(v => v.Value)
            .FirstOrDefault();

    private static bool IsLanguage(string culture, string language) =>
        culture.Equals(language, StringComparison.OrdinalIgnoreCase)
        || culture.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase);

    private static bool IsUsable(string? text) => !string.IsNullOrWhiteSpace(text);
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
                        throw new JsonException(CoreText.Format("Catalog.TextMustBeString", culture));
                    }

                    values[culture] = reader.GetString() ?? string.Empty;
                }

                return new LocalizedText(values);

            default:
                throw new JsonException(CoreText.Get("Catalog.TextInvalidShape"));
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
