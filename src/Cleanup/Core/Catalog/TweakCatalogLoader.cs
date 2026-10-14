using System.Text.Json;
using TrashSweepr.Cleanup.Core.Models;

namespace TrashSweepr.Cleanup.Core.Catalog;

public sealed record CatalogLoadResult(TweakCatalog Catalog, IReadOnlyList<ValidationIssue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
}

/// <summary>
/// Carrega os arquivos JSON do catálogo. Um tweak ou arquivo inválido gera um <see cref="ValidationIssue"/>
/// e é descartado: o restante do catálogo continua utilizável e nada inválido chega ao executor.
/// </summary>
public static class TweakCatalogLoader
{
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static CatalogLoadResult LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new CatalogLoadResult(
                TweakCatalog.Empty,
                [new ValidationIssue(IssueSeverity.Error, directory, null, CoreText.Get("Catalog.DirectoryNotFound"))]);
        }

        var issues = new List<ValidationIssue>();
        var sources = new List<(string Source, string Json)>();

        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(f => !f.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            try
            {
                sources.Add((Path.GetFileName(file), File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, Path.GetFileName(file), null, CoreText.Format("Catalog.CannotReadFile", ex.Message)));
            }
        }

        var result = LoadFromSources(sources);
        return new CatalogLoadResult(result.Catalog, [.. issues, .. result.Issues]);
    }

    /// <summary>Núcleo puro (sem acesso a disco), usado também pelos testes.</summary>
    public static CatalogLoadResult LoadFromSources(IEnumerable<(string Source, string Json)> sources)
    {
        var issues = new List<ValidationIssue>();
        var tweaks = new List<Tweak>();
        var profiles = new List<Profile>();
        var tweakOrigin = new Dictionary<string, string>(StringComparer.Ordinal);
        var profileIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (source, json) in sources)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json, DocumentOptions);
            }
            catch (JsonException ex)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, source, null, CoreText.Format("Catalog.InvalidJson", ex.Message)));
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Error, source, null, CoreText.Get("Catalog.RootNotObject")));
                    continue;
                }

                if (!root.TryGetProperty("schemaVersion", out var version)
                    || version.ValueKind != JsonValueKind.Number
                    || !version.TryGetInt32(out var versionNumber)
                    || versionNumber != SupportedSchemaVersion)
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Error, source, null, CoreText.Format("Catalog.SchemaVersion", SupportedSchemaVersion)));
                    continue;
                }

                var hasContent = false;

                if (root.TryGetProperty("tweaks", out var tweakArray) && tweakArray.ValueKind == JsonValueKind.Array)
                {
                    hasContent = true;
                    foreach (var element in tweakArray.EnumerateArray())
                    {
                        AddTweak(element, source, tweaks, tweakOrigin, issues);
                    }
                }

                if (root.TryGetProperty("profiles", out var profileArray) && profileArray.ValueKind == JsonValueKind.Array)
                {
                    hasContent = true;
                    foreach (var element in profileArray.EnumerateArray())
                    {
                        AddProfile(element, source, profiles, profileIds, issues);
                    }
                }

                if (!hasContent)
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Error, source, null, CoreText.Get("Catalog.NoContent")));
                }
            }
        }

        return new CatalogLoadResult(new TweakCatalog(tweaks, profiles), issues);
    }

    private static void AddTweak(
        JsonElement element,
        string source,
        List<Tweak> tweaks,
        Dictionary<string, string> origin,
        List<ValidationIssue> issues)
    {
        Tweak? tweak;
        try
        {
            tweak = element.Deserialize<Tweak>(TweakJson.Options);
        }
        catch (JsonException ex)
        {
            var id = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            issues.Add(new ValidationIssue(IssueSeverity.Error, source, id, ex.Message));
            return;
        }

        if (tweak is null)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, source, null, CoreText.Get("Catalog.EmptyTweak")));
            return;
        }

        IReadOnlyList<string> errors;
        try
        {
            errors = TweakValidator.ValidateTweak(tweak);
        }
        catch (Exception ex) when (ex is ArgumentException or NullReferenceException)
        {
            // Rede de segurança: um campo inesperado (ex.: null) descarta o tweak em vez de derrubar o carregamento.
            issues.Add(new ValidationIssue(IssueSeverity.Error, source, tweak.Id, CoreText.Format("Catalog.InvalidTweak", ex.Message)));
            return;
        }

        if (errors.Count > 0)
        {
            issues.AddRange(errors.Select(e => new ValidationIssue(IssueSeverity.Error, source, tweak.Id, e)));
            return;
        }

        if (origin.TryGetValue(tweak.Id, out var firstSource))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, source, tweak.Id, CoreText.Format("Catalog.DuplicateId", firstSource)));
            return;
        }

        origin[tweak.Id] = source;
        tweaks.Add(tweak);
    }

    private static void AddProfile(
        JsonElement element,
        string source,
        List<Profile> profiles,
        HashSet<string> ids,
        List<ValidationIssue> issues)
    {
        Profile? profile;
        try
        {
            profile = element.Deserialize<Profile>(TweakJson.Options);
        }
        catch (JsonException ex)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, source, null, CoreText.Format("Catalog.InvalidProfile", ex.Message)));
            return;
        }

        if (profile is null)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, source, null, CoreText.Get("Catalog.EmptyProfile")));
            return;
        }

        IReadOnlyList<string> errors;
        try
        {
            errors = TweakValidator.ValidateProfile(profile);
        }
        catch (Exception ex) when (ex is ArgumentException or NullReferenceException)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, source, profile.Id, CoreText.Format("Catalog.InvalidProfile", ex.Message)));
            return;
        }

        if (errors.Count > 0)
        {
            issues.AddRange(errors.Select(e => new ValidationIssue(IssueSeverity.Error, source, profile.Id, CoreText.Format("Catalog.ProfileProblem", e))));
            return;
        }

        if (!ids.Add(profile.Id))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, source, profile.Id, CoreText.Get("Catalog.DuplicateProfileId")));
            return;
        }

        profiles.Add(profile);
    }
}
