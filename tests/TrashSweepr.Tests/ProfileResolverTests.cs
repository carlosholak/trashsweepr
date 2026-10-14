using TrashSweepr.Cleanup.Core.Execution;
using TrashSweepr.Cleanup.Core.Models;
using TrashSweepr.Tests.Helpers;
using static TrashSweepr.Tests.Helpers.CatalogJson;

namespace TrashSweepr.Tests;

public class ProfileResolverTests
{
    private static TweakCatalog Build(params string[] tweaks) => Load(tweaks).Catalog;

    private static Profile ProfileOf(RiskLevel max) => new()
    {
        Id = "p",
        Name = LocalizedText.FromDefault("P"),
        Description = LocalizedText.FromDefault("D"),
        MaxRisk = max,
    };

    private static string[] Ids(IEnumerable<Tweak> tweaks) => [.. tweaks.Select(t => t.Id).Order()];

    [Fact]
    public void Light_profile_has_only_safe_tweaks_and_moderate_adds_moderate_ones()
    {
        var catalog = Build(
            Tweak(id: "test.safe"),
            Tweak(id: "test.moderate", risk: "moderate", riskNote: "Risco."),
            Tweak(id: "test.advanced", risk: "advanced", riskNote: "Risco alto."));

        Assert.Equal(["test.safe"], Ids(ProfileResolver.Resolve(catalog, ProfileOf(RiskLevel.Safe), 22631)));
        Assert.Equal(["test.moderate", "test.safe"], Ids(ProfileResolver.Resolve(catalog, ProfileOf(RiskLevel.Moderate), 22631)));
    }

    [Fact]
    public void Advanced_tweaks_never_enter_a_profile_even_if_the_profile_allows_it()
    {
        var catalog = Build(Tweak(id: "test.advanced", risk: "advanced", riskNote: "Risco alto."));

        Assert.Empty(ProfileResolver.Resolve(catalog, ProfileOf(RiskLevel.Advanced), 22631));
    }

    [Fact]
    public void Opt_in_tweaks_are_never_included()
    {
        var catalog = Build(Tweak(id: "test.optin", extra: ", \"optIn\": true"), Tweak(id: "test.normal"));

        Assert.Equal(["test.normal"], Ids(ProfileResolver.Resolve(catalog, ProfileOf(RiskLevel.Moderate), 22631)));
    }

    [Fact]
    public void Tweaks_incompatible_with_the_current_build_are_left_out()
    {
        var catalog = Build(Tweak(id: "test.new", extra: ", \"minBuild\": 26100"), Tweak(id: "test.any"));

        Assert.Equal(["test.any"], Ids(ProfileResolver.Resolve(catalog, ProfileOf(RiskLevel.Safe), 22631)));
        Assert.Equal(["test.any", "test.new"], Ids(ProfileResolver.Resolve(catalog, ProfileOf(RiskLevel.Safe), 26100)));
        Assert.Empty(ProfileResolver.Resolve(catalog, ProfileOf(RiskLevel.Safe), 19045));
    }
}
