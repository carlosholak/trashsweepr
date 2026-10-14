using TrashSweepr.Cleanup.Core.Catalog;
using TrashSweepr.Cleanup.Core.Models;

namespace TrashSweepr.Cleanup.Core.Operations;

/// <summary>Operações geradas por uma única ação do catálogo (ex.: um serviço gera "mudar tipo" + "parar").</summary>
public sealed record OperationGroup(TweakAction Action, IReadOnlyList<PlannedOperation> Operations);

public sealed record OperationPlan(IReadOnlyList<OperationGroup> Groups, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public IEnumerable<PlannedOperation> Operations => Groups.SelectMany(g => g.Operations);
}

/// <summary>
/// Converte ações do catálogo em operações concretas. Revalida cada ação (defesa em profundidade):
/// o engine nunca executa algo que não passe pelo <see cref="TweakValidator"/>, venha de onde vier.
/// </summary>
public static class OperationPlanner
{
    public static OperationPlan Plan(IReadOnlyList<TweakAction> actions, bool allowDeleteKey)
    {
        var groups = new List<OperationGroup>();
        var errors = new List<string>();

        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var problems = TweakValidator.ValidateAction(action, allowDeleteKey);
            if (problems.Count > 0)
            {
                errors.AddRange(problems.Select(p => $"[{i}] {p}"));
                continue;
            }

            groups.Add(new OperationGroup(action, Translate(action)));
        }

        return new OperationPlan(groups, errors);
    }

    private static List<PlannedOperation> Translate(TweakAction action)
    {
        switch (action)
        {
            case RegistryAction registry:
                return [TranslateRegistry(registry)];

            case ServiceAction service:
                var operations = new List<PlannedOperation> { new ServiceSetStartTypeOperation(service.Name, service.StartType) };
                if (service.Stop)
                {
                    operations.Add(new ServiceStopOperation(service.Name));
                }

                return operations;

            case ScheduledTaskAction task:
                return [new TaskSetEnabledOperation(task.TaskPath, task.TaskName, task.Enabled)];

            case AppxAction appx:
                return appx.Operation == AppxOperation.Remove
                    ? [new AppxRemoveOperation(appx.PackageName)]
                    : [new AppxReinstallOperation(appx.PackageName)];

            case ProgramAction program:
                return program.Operation == ProgramOperation.Uninstall
                    ? [new ProgramUninstallOperation(program.Program)]
                    : [new ProgramReinstallOperation(program.Program)];

            default:
                throw new InvalidOperationException(CoreText.Format("Engine.UnsupportedAction", action.GetType().Name));
        }
    }

    private static PlannedOperation TranslateRegistry(RegistryAction action)
    {
        switch (action.Operation)
        {
            case RegistryOperation.DeleteKey:
                return new RegistryDeleteKeyOperation(action.Hive, action.Path);

            case RegistryOperation.DeleteValue:
                return new RegistryDeleteValueOperation(action.Hive, action.Path, action.Name ?? string.Empty);

            default:
                // O validador já garantiu que o valor existe e tem o tipo certo.
                action.TryGetTypedValue(out var value, out _);
                return new RegistrySetOperation(action.Hive, action.Path, action.Name ?? string.Empty, action.Kind, value!);
        }
    }
}
