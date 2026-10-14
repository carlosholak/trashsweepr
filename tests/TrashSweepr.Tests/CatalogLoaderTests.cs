using TrashSweepr.Cleanup.Core.Catalog;
using TrashSweepr.Tests.Helpers;
using static TrashSweepr.Tests.Helpers.CatalogJson;

namespace TrashSweepr.Tests;

public sealed class CatalogLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "trashsweepr-tests-" + Guid.NewGuid().ToString("N"));

    public CatalogLoaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void Write(string fileName, string content) =>
        File.WriteAllText(Path.Combine(_directory, fileName), content);

    [Fact]
    public void Missing_directory_returns_an_empty_catalog_with_an_error()
    {
        var result = TweakCatalogLoader.LoadFromDirectory(Path.Combine(_directory, "nao-existe"));

        Assert.Empty(result.Catalog.Tweaks);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Loads_tweaks_and_profiles_from_several_files()
    {
        Write("a.json", TweakFile(Tweak(id: "test.a")));
        Write("b.json", TweakFile(Tweak(id: "test.b")));
        Write("profiles.json", ProfileFile(Profile()));

        var result = TweakCatalogLoader.LoadFromDirectory(_directory);

        Assert.False(result.HasErrors);
        Assert.Equal(["test.a", "test.b"], result.Catalog.Tweaks.Select(t => t.Id));
        Assert.Single(result.Catalog.Profiles);
        Assert.NotNull(result.Catalog.Find("test.a"));
        Assert.Null(result.Catalog.Find("test.zzz"));
    }

    [Fact]
    public void Schema_files_are_not_treated_as_catalog_files()
    {
        Write("a.json", TweakFile(Tweak()));
        Write("tweak.schema.json", """{ "$schema": "x", "type": "object" }""");

        var result = TweakCatalogLoader.LoadFromDirectory(_directory);

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Broken_json_file_is_reported_and_the_rest_still_loads()
    {
        Write("a.json", TweakFile(Tweak(id: "test.ok")));
        Write("broken.json", "{ isto não é json");

        var result = TweakCatalogLoader.LoadFromDirectory(_directory);

        Assert.Equal(["test.ok"], result.Catalog.Tweaks.Select(t => t.Id));
        Assert.Contains(result.Issues, i => i.Source == "broken.json" && i.Severity == IssueSeverity.Error);
    }

    [Theory]
    [InlineData("""{ "tweaks": [] }""")]
    [InlineData("""{ "schemaVersion": 2, "tweaks": [] }""")]
    [InlineData("""{ "schemaVersion": "1", "tweaks": [] }""")]
    [InlineData("""{ "schemaVersion": 1.5, "tweaks": [] }""")]
    [InlineData("""{ "schemaVersion": 1 }""")]
    [InlineData("[]")]
    public void Files_with_an_unsupported_structure_are_rejected(string json)
    {
        var result = TweakCatalogLoader.LoadFromSources([("x.json", json)]);

        Assert.Empty(result.Catalog.Tweaks);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        const string json = """
            {
              // arquivo com comentário
              "schemaVersion": 1,
              "profiles": [ { "id": "light", "name": "L", "description": "D", "maxRisk": "safe", }, ],
            }
            """;

        var result = TweakCatalogLoader.LoadFromSources([("x.json", json)]);

        Assert.False(result.HasErrors);
        Assert.Single(result.Catalog.Profiles);
    }

    [Fact]
    public void Issues_are_traceable_to_file_and_tweak()
    {
        var result = TweakCatalogLoader.LoadFromSources([("arquivo.json", TweakFile(Tweak(id: "test.bad", risk: "moderate")))]);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("arquivo.json", issue.Source);
        Assert.Equal("test.bad", issue.TweakId);
        Assert.Contains("arquivo.json", issue.ToString(), StringComparison.Ordinal);
        Assert.Contains("test.bad", issue.ToString(), StringComparison.Ordinal);
    }
}
