using System.Globalization;
using System.Text.Json;
using TrashSweepr.Cleanup.Core.Catalog;
using TrashSweepr.Cleanup.Core.Models;

namespace TrashSweepr.Tests;

public class LocalizedTextTests
{
    private static LocalizedText Parse(string json) => JsonSerializer.Deserialize<LocalizedText>(json, TweakJson.Options)!;

    [Fact]
    public void Plain_string_is_the_default_culture()
    {
        var text = Parse("\"Olá\"");

        Assert.True(text.HasDefault);
        Assert.Equal("Olá", text.Resolve(CultureInfo.GetCultureInfo("pt-BR")));
    }

    [Fact]
    public void Object_form_resolves_by_exact_culture_then_language_then_english()
    {
        var text = Parse("""{ "pt-BR": "Olá", "en-US": "Hello", "es-ES": "Hola" }""");

        Assert.Equal("Hello", text.Resolve(CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("Hello", text.Resolve(CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("Hola", text.Resolve(CultureInfo.GetCultureInfo("es-MX")));
        Assert.Equal("Olá", text.Resolve(CultureInfo.GetCultureInfo("pt-PT")));
        Assert.Equal("Hello", text.Resolve(CultureInfo.GetCultureInfo("de-DE")));
    }

    [Theory]
    [InlineData("pt-BR", "Olá (BR)")]
    [InlineData("pt-PT", "Olá (BR)")]
    [InlineData("pt", "Olá (BR)")]
    [InlineData("en-US", "Hello")]
    [InlineData("en-GB", "Hello")]
    [InlineData("de-DE", "Hello")]
    [InlineData("ja-JP", "Hello")]
    [InlineData("", "Hello")]
    public void Shipped_shape_falls_back_from_exact_culture_to_language_to_english(string culture, string expected)
    {
        var text = Parse("""{ "en": "Hello", "pt-BR": "Olá (BR)" }""");

        Assert.Equal(expected, text.Resolve(CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void Exact_culture_wins_over_the_neutral_language()
    {
        var text = Parse("""{ "en": "Color", "en-GB": "Colour", "pt": "Cor (PT)", "pt-BR": "Cor" }""");

        Assert.Equal("Colour", text.Resolve(CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("Color", text.Resolve(CultureInfo.GetCultureInfo("en-AU")));
        Assert.Equal("Cor", text.Resolve(CultureInfo.GetCultureInfo("pt-BR")));
        Assert.Equal("Cor (PT)", text.Resolve(CultureInfo.GetCultureInfo("pt-PT")));
    }

    [Fact]
    public void Without_english_portuguese_comes_next_then_any_value()
    {
        Assert.Equal("Olá", Parse("""{ "es": "Hola", "pt-BR": "Olá" }""").Resolve(CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal("Hola", Parse("""{ "es": "Hola" }""").Resolve(CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void Empty_translations_are_skipped()
    {
        var text = Parse("""{ "en": "  ", "pt-BR": "Olá" }""");

        Assert.Equal("Olá", text.Resolve(CultureInfo.GetCultureInfo("en-US")));
        Assert.False(text.HasText("en"));
        Assert.True(text.HasSupportedText);
    }

    [Fact]
    public void Plain_string_is_portuguese_but_shown_in_every_language()
    {
        var text = Parse("\"Olá\"");

        Assert.True(text.HasText("pt-BR"));
        Assert.False(text.HasText("en"));
        Assert.Equal("Olá", text.Resolve(CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Supported_text_needs_en_or_pt_br()
    {
        Assert.True(Parse("""{ "en": "Hello" }""").HasSupportedText);
        Assert.True(Parse("""{ "pt-BR": "Olá" }""").HasSupportedText);
        Assert.False(Parse("""{ "es": "Hola" }""").HasSupportedText);
        Assert.False(Parse("""{ "en": "" }""").HasSupportedText);
    }

    [Fact]
    public void Without_a_default_culture_any_value_is_used_as_last_resort()
    {
        var text = Parse("""{ "es-ES": "Hola" }""");

        Assert.False(text.HasDefault);
        Assert.Equal("Hola", text.Resolve(CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void Non_string_values_are_rejected()
    {
        Assert.Throws<JsonException>(() => Parse("""{ "pt-BR": 1 }"""));
        Assert.Throws<JsonException>(() => Parse("42"));
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var single = LocalizedText.FromDefault("Olá");
        var multi = new LocalizedText(new Dictionary<string, string> { ["pt-BR"] = "Olá", ["en-US"] = "Hello" });

        Assert.Equal("\"Olá\"", JsonSerializer.Serialize(single, TweakJson.Options).Replace("\\u00E1", "á", StringComparison.Ordinal));
        Assert.Equal("Hello", Parse(JsonSerializer.Serialize(multi, TweakJson.Options)).Resolve(CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Tweak_actions_round_trip_through_json()
    {
        const string json = """
            [
              { "type": "registry", "hive": "hkcu", "path": "Software\\X", "name": "A", "kind": "dword", "value": 1 },
              { "type": "service", "name": "DiagTrack", "startType": "disabled", "stop": true },
              { "type": "scheduledTask", "taskPath": "\\A\\", "taskName": "B", "enabled": false },
              { "type": "appx", "operation": "remove", "packageName": "Contoso.App" }
            ]
            """;

        var actions = JsonSerializer.Deserialize<List<TweakAction>>(json, TweakJson.Options)!;
        var again = JsonSerializer.Deserialize<List<TweakAction>>(JsonSerializer.Serialize(actions, TweakJson.Options), TweakJson.Options)!;

        Assert.Equal(4, again.Count);
        Assert.Equal(actions[0], again[0]);
        Assert.Equal(actions[1], again[1]);
        Assert.Equal(actions[2], again[2]);
        Assert.Equal(actions[3], again[3]);
    }
}
