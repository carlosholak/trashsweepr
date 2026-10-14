namespace TrashSweepr.Limpeza.Core.Models;

/// <summary>
/// Perfil pré-definido. Contém todos os tweaks de risco até <see cref="MaxRisk"/> que não sejam opt-in.
/// <see cref="RiskLevel.Advanced"/> nunca é aceito como teto de um perfil.
/// </summary>
public sealed record Profile
{
    public required string Id { get; init; }

    public required LocalizedText Name { get; init; }

    public required LocalizedText Description { get; init; }

    public required RiskLevel MaxRisk { get; init; }
}

public sealed class TweakCatalog
{
    private readonly Dictionary<string, Tweak> _byId;

    public TweakCatalog(IReadOnlyList<Tweak> tweaks, IReadOnlyList<Profile> profiles)
    {
        Tweaks = tweaks;
        Profiles = profiles;
        _byId = tweaks.ToDictionary(t => t.Id, StringComparer.Ordinal);
    }

    public static TweakCatalog Empty { get; } = new([], []);

    public IReadOnlyList<Tweak> Tweaks { get; }

    public IReadOnlyList<Profile> Profiles { get; }

    public Tweak? Find(string id) => _byId.GetValueOrDefault(id);
}
