using TrashSweepr.Limpeza.Core.Catalog;
using TrashSweepr.Limpeza.Core.Models;
using TrashSweepr.Tests.Helpers;
using static TrashSweepr.Tests.Helpers.CatalogJson;

namespace TrashSweepr.Tests;

public class CompatibilityTests
{
    private static Tweak Build(string extra = "") => Assert.Single(Load(Tweak(extra: extra)).Catalog.Tweaks);

    [Theory]
    [InlineData(19045)]
    [InlineData(21999)]
    public void Windows_10_builds_are_never_compatible(int build)
    {
        var result = CompatibilityChecker.Evaluate(Build(), build);

        Assert.Equal(CompatibilityStatus.NotWindows11, result.Status);
        Assert.False(result.IsCompatible);
    }

    [Theory]
    [InlineData(22000)]
    [InlineData(22631)]
    [InlineData(26100)]
    public void Tweak_without_limits_is_compatible_with_any_windows_11_build(int build)
    {
        Assert.True(CompatibilityChecker.Evaluate(Build(), build).IsCompatible);
    }

    [Fact]
    public void Build_older_than_min_build_is_reported_with_the_required_build()
    {
        var result = CompatibilityChecker.Evaluate(Build(", \"minBuild\": 26100"), 22631);

        Assert.Equal(CompatibilityStatus.BuildTooOld, result.Status);
        Assert.Equal(26100, result.RequiredBuild);
    }

    [Fact]
    public void Build_newer_than_max_build_is_reported_with_the_limit()
    {
        var result = CompatibilityChecker.Evaluate(Build(", \"maxBuild\": 22631"), 26100);

        Assert.Equal(CompatibilityStatus.BuildTooNew, result.Status);
        Assert.Equal(22631, result.RequiredBuild);
    }

    [Fact]
    public void Limits_are_inclusive()
    {
        var tweak = Build(", \"minBuild\": 22621, \"maxBuild\": 22631");

        Assert.True(CompatibilityChecker.Evaluate(tweak, 22621).IsCompatible);
        Assert.True(CompatibilityChecker.Evaluate(tweak, 22631).IsCompatible);
        Assert.False(CompatibilityChecker.Evaluate(tweak, 22620).IsCompatible);
        Assert.False(CompatibilityChecker.Evaluate(tweak, 22632).IsCompatible);
    }
}
