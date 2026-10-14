using TrashSweepr.Cleanup.Core.Catalog;
using TrashSweepr.Cleanup.Core.Models;

namespace TrashSweepr.Cleanup.Core.Execution;

/// <summary>
/// Decide quais tweaks entram em um perfil pré-definido. Regras fixas, independentes do JSON:
/// risco até o teto do perfil, nunca Avançado, nunca opt-in e só o que é compatível com o Windows atual.
/// </summary>
public static class ProfileResolver
{
    public static IReadOnlyList<Tweak> Resolve(TweakCatalog catalog, Profile profile, int windowsBuild) =>
        [.. catalog.Tweaks.Where(t =>
            t.Risk <= profile.MaxRisk
            && t.Risk < RiskLevel.Advanced
            && !t.OptIn
            && CompatibilityChecker.Evaluate(t, windowsBuild).IsCompatible)];
}
