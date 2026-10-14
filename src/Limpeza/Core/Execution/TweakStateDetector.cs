using TrashSweepr.Limpeza.Core.Abstractions;
using TrashSweepr.Limpeza.Core.Catalog;
using TrashSweepr.Limpeza.Core.Models;
using TrashSweepr.Limpeza.Core.Operations;

namespace TrashSweepr.Limpeza.Core.Execution;

/// <summary>Descobre, só lendo, se cada tweak já está aplicado neste computador.</summary>
public sealed class TweakStateDetector(SystemServices system, IWindowsInfo windows)
{
    public async Task<IReadOnlyDictionary<string, TweakState>> DetectAsync(IEnumerable<Tweak> tweaks, CancellationToken ct)
    {
        var reader = new SystemStateReader(system);
        var result = new Dictionary<string, TweakState>(StringComparer.Ordinal);

        foreach (var tweak in tweaks)
        {
            ct.ThrowIfCancellationRequested();

            var compatibility = CompatibilityChecker.Evaluate(tweak, windows.Build);
            if (!compatibility.IsCompatible)
            {
                result[tweak.Id] = new TweakState(TweakAppliedState.NotApplicable, compatibility);
                continue;
            }

            result[tweak.Id] = new TweakState(await DetectOneAsync(tweak, reader, ct).ConfigureAwait(false), compatibility);
        }

        return result;
    }

    private static async Task<TweakAppliedState> DetectOneAsync(Tweak tweak, SystemStateReader reader, CancellationToken ct)
    {
        var plan = OperationPlanner.Plan(tweak.Apply, allowDeleteKey: false);
        if (!plan.IsValid)
        {
            return TweakAppliedState.Unknown;
        }

        var satisfied = 0;
        var notSatisfied = 0;

        foreach (var operation in plan.Operations)
        {
            switch (await reader.CheckAsync(operation, ct).ConfigureAwait(false))
            {
                case OperationState.Satisfied:
                    satisfied++;
                    break;
                case OperationState.NotSatisfied:
                    notSatisfied++;
                    break;
                case OperationState.Unknown:
                    return TweakAppliedState.Unknown;
            }
        }

        return (satisfied, notSatisfied) switch
        {
            (0, 0) => TweakAppliedState.NotApplicable,
            (_, 0) => TweakAppliedState.Applied,
            (0, _) => TweakAppliedState.NotApplied,
            _ => TweakAppliedState.Partial,
        };
    }
}
