using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TrashSweepr.Cleanup.Core;
using TrashSweepr.Cleanup.Core.Execution;
using TrashSweepr.Tests.Helpers;

namespace TrashSweepr.Tests;

/// <summary>Mensagens do motor (src/Cleanup/Core/Resources/Messages*.resx).</summary>
public partial class CoreTextTests
{
    private static readonly string ResourceDirectory = Path.Combine(RepoPaths.Root, "src", "Cleanup", "Core", "Resources");

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"CoreText\.(?:Get|Format)\(""([^""]+)""")]
    private static partial Regex UsageRegex();

    private static Dictionary<string, string> ReadResx(string fileName) =>
        XDocument.Load(Path.Combine(ResourceDirectory, fileName))
            .Root!
            .Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!, StringComparer.Ordinal);

    [Fact]
    public void English_and_portuguese_files_have_the_same_keys()
    {
        var english = ReadResx("Messages.resx");
        var portuguese = ReadResx("Messages.pt.resx");

        Assert.NotEmpty(english);
        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), portuguese.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Placeholders_match_and_every_text_is_a_valid_format_string()
    {
        var english = ReadResx("Messages.resx");
        var portuguese = ReadResx("Messages.pt.resx");

        foreach (var (key, en) in english)
        {
            var pt = portuguese[key];
            var enSlots = Placeholders(en);
            Assert.True(enSlots.SetEquals(Placeholders(pt)), $"{key}: marcadores diferentes entre en e pt");
            Assert.True(enSlots.Count == 0 || enSlots.Max() == enSlots.Count - 1, $"{key}: marcadores fora de sequência");

            // Todo texto passa por string.Format sem erro (chaves literais precisam ser {{ }}).
            var args = Enumerable.Range(0, enSlots.Count).Select(i => (object)i).ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, en, args);
            _ = string.Format(CultureInfo.InvariantCulture, pt, args);
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            Assert.False(string.IsNullOrWhiteSpace(pt), key);
        }
    }

    [Fact]
    public void Every_key_used_in_the_code_exists()
    {
        var keys = ReadResx("Messages.resx").Keys.ToHashSet(StringComparer.Ordinal);
        var used = Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src", "Cleanup"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => UsageRegex().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(used);
        Assert.Empty(used.Except(keys));
        Assert.Empty(keys.Except(used));
    }

    [Theory]
    [InlineData("en-US", "Cancelled by the user.")]
    [InlineData("en-GB", "Cancelled by the user.")]
    [InlineData("pt-BR", "Cancelado pelo usuário.")]
    [InlineData("pt-PT", "Cancelado pelo usuário.")]
    [InlineData("ja-JP", "Cancelled by the user.")]
    public void Messages_follow_the_interface_language_with_english_as_fallback(string culture, string expected)
    {
        using (TestCulture.Use(culture))
        {
            Assert.Equal(expected, CoreText.Get("Engine.Cancelled"));
        }
    }

    [Fact]
    public void Restore_point_description_follows_the_interface_language()
    {
        using (TestCulture.Use("en-US"))
        {
            Assert.Equal("trashsweepr: before applying tweaks", new ExecutionOptions().RestorePointDescription);
        }

        using (TestCulture.Use("pt-BR"))
        {
            Assert.Equal("trashsweepr: antes de aplicar ajustes", new ExecutionOptions().RestorePointDescription);
        }
    }

    [Fact]
    public void Unknown_key_is_visible_instead_of_empty()
    {
        Assert.Equal("!Nope.Missing!", CoreText.Get("Nope.Missing"));
    }

    private static HashSet<int> Placeholders(string text) =>
        [.. PlaceholderRegex().Matches(text.Replace("{{", string.Empty, StringComparison.Ordinal).Replace("}}", string.Empty, StringComparison.Ordinal))
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];
}
