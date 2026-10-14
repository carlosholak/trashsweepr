using System.Text.RegularExpressions;
using TrashSweepr.Cleanup.Services;
using TrashSweepr.Cleanup.Core.Execution;
using TrashSweepr.Cleanup.Core.Models;
using TrashSweepr.Tests.Helpers;
using TrashSweepr.Localization;

namespace TrashSweepr.Tests;

public partial class LocalizationTests
{
    private static readonly string AppDirectory = Path.Combine(RepoPaths.Root, "src", "trashsweepr");

    [GeneratedRegex(@"\{loc:T\s+([A-Za-z0-9_.]+)\}")]
    private static partial Regex XamlKeyRegex();

    [GeneratedRegex(@"Loc\.(?:Get|Format)\(\s*""([A-Za-z0-9_.]+)""")]
    private static partial Regex CodeKeyRegex();

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(AppDirectory, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static bool Exists(string key) => !Loc.Get(key).StartsWith('!');

    [Fact]
    public void Every_text_key_used_in_xaml_exists_in_the_resources()
    {
        var keys = SourceFiles("*.xaml").SelectMany(f => XamlKeyRegex().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).Distinct().ToList();

        Assert.NotEmpty(keys);
        Assert.Empty(keys.Where(k => !Exists(k)));
    }

    [Fact]
    public void Every_text_key_used_in_code_exists_in_the_resources()
    {
        var keys = SourceFiles("*.cs").SelectMany(f => CodeKeyRegex().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).Distinct().ToList();

        Assert.NotEmpty(keys);
        Assert.Empty(keys.Where(k => !Exists(k)));
    }

    [Fact]
    public void Keys_built_from_enums_exist_for_every_value()
    {
        var missing = new List<string>();
        missing.AddRange(Enum.GetNames<TweakCategory>().Select(n => $"Category.{n}"));
        missing.AddRange(Enum.GetNames<RiskLevel>().Select(n => $"Risk.{n}"));
        missing.AddRange(Enum.GetNames<ServiceStartType>().Select(n => $"StartType.{n}"));
        missing.AddRange(Enum.GetNames<RegistryValueType>().Select(n => $"RegType.{n}"));
        missing.AddRange(Enum.GetNames<ManagedProgram>().Select(n => $"Program.{n}"));
        missing.AddRange(Enum.GetNames<SkipReason>().Select(n => $"Skip.{n}"));
        missing.AddRange(Enum.GetNames<TweakAppliedState>().Select(n => $"State.{n}"));

        Assert.Empty(missing.Where(k => !Exists(k)));
    }

    [Fact]
    public void Missing_keys_are_visible_instead_of_empty()
    {
        Assert.Equal("!Nao.Existe!", Loc.Get("Nao.Existe"));
    }

    [Fact]
    public void Format_strings_receive_their_arguments()
    {
        Assert.Equal("3 de 24 tweaks", Loc.Format("List.Count", 3, 24));
        Assert.Equal("Simular 2 selecionado(s)", Loc.Format("Run.Simulate", 2));
    }

    [Fact]
    public void Every_resource_is_translated_text_without_leftover_placeholders_mismatch()
    {
        // Os textos com {0}/{1} precisam funcionar com string.Format: um "{" solto lançaria FormatException em tempo de execução.
        var manager = new System.Resources.ResourceManager("TrashSweepr.Resources.Strings", typeof(Loc).Assembly);
        var set = manager.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)!;

        foreach (System.Collections.DictionaryEntry entry in set)
        {
            var text = (string)entry.Value!;
            var ex = Record.Exception(() => string.Format(System.Globalization.CultureInfo.InvariantCulture, text, "a", "b", "c", "d"));
            Assert.True(ex is null, $"Chave {entry.Key}: formato inválido ({ex?.Message})");
        }
    }
}
