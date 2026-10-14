using TrashSweepr.Cleanup.Core.Models;

namespace TrashSweepr.Cleanup.Core.Catalog;

public enum CompatibilityStatus
{
    Compatible,
    NotWindows11,
    BuildTooOld,
    BuildTooNew,
}

/// <param name="RequiredBuild">Build que causou a incompatibilidade (mínima ou máxima), para a UI montar a mensagem.</param>
public sealed record CompatibilityResult(CompatibilityStatus Status, int? RequiredBuild = null)
{
    public bool IsCompatible => Status == CompatibilityStatus.Compatible;
}

public static class CompatibilityChecker
{
    public static CompatibilityResult Evaluate(Tweak tweak, int windowsBuild)
    {
        if (windowsBuild < TweakValidator.Windows11MinBuild)
        {
            return new CompatibilityResult(CompatibilityStatus.NotWindows11, TweakValidator.Windows11MinBuild);
        }

        if (tweak.MinBuild is { } min && windowsBuild < min)
        {
            return new CompatibilityResult(CompatibilityStatus.BuildTooOld, min);
        }

        if (tweak.MaxBuild is { } max && windowsBuild > max)
        {
            return new CompatibilityResult(CompatibilityStatus.BuildTooNew, max);
        }

        return new CompatibilityResult(CompatibilityStatus.Compatible);
    }
}
