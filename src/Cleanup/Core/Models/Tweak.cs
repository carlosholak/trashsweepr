namespace TrashSweepr.Cleanup.Core.Models;

public sealed record Tweak
{
    /// <summary>Identificador estável, em minúsculas, ex.: privacy.advertising-id.</summary>
    public required string Id { get; init; }

    public required LocalizedText Name { get; init; }

    public required LocalizedText Description { get; init; }

    public required TweakCategory Category { get; init; }

    public required RiskLevel Risk { get; init; }

    public bool RequiresReboot { get; init; }

    /// <summary>
    /// Tweaks "opt-in" nunca entram em perfis pré-definidos, mesmo que o risco caiba no perfil.
    /// Só são aplicados quando o usuário os marca explicitamente (ex.: remover apps do Xbox).
    /// </summary>
    public bool OptIn { get; init; }

    /// <summary>Explicação do risco mostrada na confirmação. Obrigatória para Moderado/Avançado.</summary>
    public LocalizedText? RiskNote { get; init; }

    /// <summary>Texto extra sobre como/até onde o tweak é reversível.</summary>
    public LocalizedText? RevertNote { get; init; }

    /// <summary>Menor build do Windows suportada. Padrão: 22000 (Windows 11).</summary>
    public int? MinBuild { get; init; }

    public int? MaxBuild { get; init; }

    public required IReadOnlyList<TweakAction> Apply { get; init; }

    /// <summary>
    /// Ações que levam ao padrão do Windows. Usadas quando não há backup do usuário
    /// (ex.: o tweak já estava aplicado antes de o trashsweepr abrir).
    /// </summary>
    public required IReadOnlyList<TweakAction> Revert { get; init; }
}
