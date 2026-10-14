using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrashSweepr.Limpeza.Core.Catalog;

public static class TweakJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create() => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Um campo digitado errado (ex.: "requiresReboto") vira erro em vez de ser ignorado em silêncio.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
}
