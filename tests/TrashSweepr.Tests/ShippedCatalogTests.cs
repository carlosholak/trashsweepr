using System.Globalization;
using System.Text.Json;
using TrashSweepr.Cleanup.Core.Catalog;
using TrashSweepr.Cleanup.Core.Execution;
using TrashSweepr.Cleanup.Core.Models;
using TrashSweepr.Tests.Helpers;

namespace TrashSweepr.Tests;

/// <summary>Invariantes do catálogo real em /tweaks: protegem contra erros ao editar os JSON.</summary>
public class ShippedCatalogTests
{
    private static readonly Lazy<CatalogLoadResult> Loaded = new(() => TweakCatalogLoader.LoadFromDirectory(RepoPaths.TweaksDirectory));

    private static TweakCatalog Catalog => Loaded.Value.Catalog;

    [Fact]
    public void Shipped_catalog_loads_without_any_issue()
    {
        Assert.Empty(Loaded.Value.Issues);
        Assert.NotEmpty(Catalog.Tweaks);
    }

    [Fact]
    public void Every_requested_initial_tweak_is_present()
    {
        string[] expected =
        [
            "apps.clipchamp", "apps.xbox", "apps.solitaire", "apps.teams-personal", "apps.news", "apps.weather",
            "apps.tips", "apps.feedback-hub", "apps.maps", "apps.office-hub", "apps.skype",
            "privacy.telemetry-basic", "privacy.advertising-id", "privacy.tips-ads",
            "interface.start-suggestions", "interface.start-bing-search", "interface.widgets", "interface.copilot",
            "interface.recall", "tasks.compat-appraiser", "tasks.ceip-consolidator",
            "interface.classic-context-menu", "interface.show-file-extensions",
        ];

        foreach (var id in expected)
        {
            Assert.NotNull(Catalog.Find(id));
        }
    }

    [Fact]
    public void Ids_are_unique()
    {
        Assert.Equal(Catalog.Tweaks.Count, Catalog.Tweaks.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void Ships_a_light_and_a_moderate_profile_and_none_reaches_advanced()
    {
        Assert.Contains(Catalog.Profiles, p => p.Id == "light" && p.MaxRisk == RiskLevel.Safe);
        Assert.Contains(Catalog.Profiles, p => p.Id == "moderate" && p.MaxRisk == RiskLevel.Moderate);
        Assert.All(Catalog.Profiles, p => Assert.True(p.MaxRisk < RiskLevel.Advanced));
    }

    [Fact]
    public void Advanced_tweaks_are_always_opt_in()
    {
        Assert.All(Catalog.Tweaks.Where(t => t.Risk == RiskLevel.Advanced), t => Assert.True(t.OptIn, t.Id));
    }

    [Fact]
    public void Every_tweak_describes_how_to_undo_and_what_it_does()
    {
        foreach (var tweak in Catalog.Tweaks)
        {
            Assert.True(tweak.Description.HasDefault, tweak.Id);
            Assert.True(tweak.RevertNote?.HasDefault == true, $"{tweak.Id} sem revertNote");
        }
    }

    [Fact]
    public void Every_text_in_the_shipped_files_has_english_and_brazilian_portuguese()
    {
        string[] textFields = ["name", "description", "riskNote", "revertNote"];
        var checkedTexts = 0;

        foreach (var file in Directory.EnumerateFiles(RepoPaths.TweaksDirectory, "*.json").Where(f => !f.EndsWith(".schema.json", StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var section in new[] { "tweaks", "profiles" })
            {
                if (!document.RootElement.TryGetProperty(section, out var items))
                {
                    continue;
                }

                foreach (var item in items.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString();
                    foreach (var field in textFields)
                    {
                        if (!item.TryGetProperty(field, out var text))
                        {
                            continue;
                        }

                        var where = $"{Path.GetFileName(file)} {id} {field}";
                        Assert.True(text.ValueKind == JsonValueKind.Object, $"{where}: precisa ser {{ \"en\", \"pt-BR\" }}");
                        Assert.False(string.IsNullOrWhiteSpace(text.TryGetProperty("en", out var en) ? en.GetString() : null), $"{where}: sem \"en\"");
                        Assert.False(string.IsNullOrWhiteSpace(text.TryGetProperty("pt-BR", out var pt) ? pt.GetString() : null), $"{where}: sem \"pt-BR\"");
                        checkedTexts++;
                    }
                }
            }
        }

        Assert.True(checkedTexts > 50, $"só {checkedTexts} textos conferidos");
    }

    [Fact]
    public void Every_tweak_and_profile_reads_differently_in_english_and_portuguese()
    {
        var en = CultureInfo.GetCultureInfo("en-US");
        var pt = CultureInfo.GetCultureInfo("pt-BR");

        var texts = Catalog.Tweaks.SelectMany(t => new (string, LocalizedText?)[]
            {
                ($"{t.Id} name", t.Name), ($"{t.Id} description", t.Description),
                ($"{t.Id} riskNote", t.RiskNote), ($"{t.Id} revertNote", t.RevertNote),
            })
            .Concat(Catalog.Profiles.SelectMany(p => new (string, LocalizedText?)[] { ($"{p.Id} name", p.Name), ($"{p.Id} description", p.Description) }));

        foreach (var (where, text) in texts)
        {
            if (text is null)
            {
                continue;
            }

            var english = text.Resolve(en);
            var portuguese = text.Resolve(pt);
            Assert.False(string.IsNullOrWhiteSpace(english), where);
            Assert.False(string.IsNullOrWhiteSpace(portuguese), where);
            Assert.True(english != portuguese, $"{where}: mesmo texto nas duas línguas");
            Assert.Equal(english, text.Resolve(CultureInfo.GetCultureInfo("de-DE")));
            Assert.Equal(portuguese, text.Resolve(CultureInfo.GetCultureInfo("pt-PT")));
        }
    }

    [Fact]
    public void Every_apply_action_has_a_matching_revert_action()
    {
        foreach (var tweak in Catalog.Tweaks)
        {
            foreach (var action in tweak.Apply)
            {
                Assert.True(tweak.Revert.Any(r => Reverts(action, r)), $"{tweak.Id}: nenhuma ação de revert cobre {action}");
            }
        }
    }

    [Fact]
    public void Apps_are_removed_and_reinstalled_symmetrically()
    {
        foreach (var tweak in Catalog.Tweaks.Where(t => t.Category == TweakCategory.Apps))
        {
            var removed = tweak.Apply.OfType<AppxAction>().Select(a => a.PackageName).Order().ToList();
            var restored = tweak.Revert.OfType<AppxAction>().Select(a => a.PackageName).Order().ToList();

            Assert.NotEmpty(removed);
            Assert.Equal(removed, restored);
            Assert.All(tweak.Apply.OfType<AppxAction>(), a => Assert.Equal(AppxOperation.Remove, a.Operation));
        }
    }

    [Fact]
    public void Recall_is_unavailable_before_windows_11_24h2()
    {
        var recall = Catalog.Find("interface.recall")!;

        Assert.False(CompatibilityChecker.Evaluate(recall, 22631).IsCompatible);
        Assert.True(CompatibilityChecker.Evaluate(recall, 26100).IsCompatible);
    }

    [Fact]
    public void Profiles_resolve_to_the_expected_tweaks()
    {
        var light = Catalog.Profiles.Single(p => p.Id == "light");
        var moderate = Catalog.Profiles.Single(p => p.Id == "moderate");

        var lightIds = ProfileResolver.Resolve(Catalog, light, 26100).Select(t => t.Id).ToHashSet();
        var moderateIds = ProfileResolver.Resolve(Catalog, moderate, 26100).Select(t => t.Id).ToHashSet();

        Assert.Contains("privacy.advertising-id", lightIds);
        Assert.Contains("interface.recall", lightIds);
        Assert.DoesNotContain("services.diagtrack", lightIds);
        Assert.DoesNotContain("apps.xbox", lightIds);

        Assert.True(lightIds.IsSubsetOf(moderateIds));
        Assert.Contains("services.diagtrack", moderateIds);
        Assert.Contains("tasks.compat-appraiser", moderateIds);
        Assert.DoesNotContain("apps.xbox", moderateIds);
        Assert.DoesNotContain("apps.teams-personal", moderateIds);
        Assert.All(moderateIds, id => Assert.NotEqual(RiskLevel.Advanced, Catalog.Find(id)!.Risk));
    }

    private static bool Reverts(TweakAction apply, TweakAction revert) => (apply, revert) switch
    {
        (RegistryAction a, RegistryAction r) => a.Hive == r.Hive && RegistryTargetsMatch(a, r),
        (ServiceAction a, ServiceAction r) => string.Equals(a.Name, r.Name, StringComparison.OrdinalIgnoreCase),
        (ScheduledTaskAction a, ScheduledTaskAction r) => a.TaskPath == r.TaskPath && a.TaskName == r.TaskName && a.Enabled != r.Enabled,
        (AppxAction a, AppxAction r) => a.PackageName == r.PackageName && a.Operation != r.Operation,
        _ => false,
    };

    private static bool RegistryTargetsMatch(RegistryAction apply, RegistryAction revert) =>
        revert.Operation == RegistryOperation.DeleteKey
            ? apply.Path.StartsWith(revert.Path, StringComparison.OrdinalIgnoreCase)
            : string.Equals(apply.Path, revert.Path, StringComparison.OrdinalIgnoreCase)
              && string.Equals(apply.Name, revert.Name, StringComparison.Ordinal);
}
