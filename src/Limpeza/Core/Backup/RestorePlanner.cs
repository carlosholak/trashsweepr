using TrashSweepr.Limpeza.Core.Catalog;
using TrashSweepr.Limpeza.Core.Models;
using TrashSweepr.Limpeza.Core.Operations;

namespace TrashSweepr.Limpeza.Core.Backup;

/// <summary>
/// Transforma um backup em operações para devolver o sistema ao estado anterior. Os arquivos de backup
/// também passam pelo validador: um backup adulterado não consegue mexer em componentes protegidos.
/// </summary>
public static class RestorePlanner
{
    public static OperationPlan Plan(BackupRecord record)
    {
        var groups = new List<OperationGroup>();
        var errors = new List<string>();

        if (record.Items is null)
        {
            return new OperationPlan(groups, [$"Backup {record.Id}: lista de itens ausente."]);
        }

        // Do último alvo alterado para o primeiro.
        foreach (var item in record.Items.Reverse())
        {
            if (item is null)
            {
                errors.Add($"Backup {record.Id}: item vazio (null).");
                continue;
            }

            var (action, operations) = PlanItem(item);
            if (action is null)
            {
                continue;
            }

            var problems = TweakValidator.ValidateAction(action, allowDeleteKey: true);
            if (problems.Count > 0)
            {
                errors.AddRange(problems.Select(p => $"Backup {record.Id}: {p}"));
                continue;
            }

            var built = operations();
            if (built.Error is not null)
            {
                errors.Add($"Backup {record.Id}: {built.Error}");
                continue;
            }

            groups.Add(new OperationGroup(action, built.Operations));
        }

        return new OperationPlan(groups, errors);
    }

    private sealed record Built(IReadOnlyList<PlannedOperation> Operations, string? Error = null);

    private static (TweakAction? Action, Func<Built> Operations) PlanItem(BackupItem item)
    {
        switch (item)
        {
            case RegistryValueBackup reg:
                return (ToAction(reg), () => PlanRegistry(reg));

            case ServiceBackup svc:
                return (
                    new ServiceAction { Name = svc.Name, StartType = svc.StartType },
                    () => PlanService(svc));

            case TaskBackup task:
                return (
                    new ScheduledTaskAction { TaskPath = task.TaskPath, TaskName = task.TaskName, Enabled = task.Enabled },
                    () => task.Existed
                        ? new Built([new TaskSetEnabledOperation(task.TaskPath, task.TaskName, task.Enabled)])
                        : new Built([]));

            case AppxBackup appx:
                return (
                    new AppxAction { Operation = AppxOperation.Reinstall, PackageName = appx.PackageName },
                    () => appx.InstalledForCurrentUser
                        ? new Built([new AppxReinstallOperation(appx.PackageName, OnlyIfMissing: true)])
                        : new Built([]));

            default:
                return (null, () => new Built([]));
        }
    }

    /// <summary>
    /// Ação equivalente ao que a restauração fará. Um valor anterior é validado como um 'set' (tipo, tamanho,
    /// caracteres de controle, chaves de comando); os demais casos, como um 'deleteValue' do mesmo alvo.
    /// </summary>
    internal static RegistryAction ToAction(RegistryValueBackup reg)
    {
        ArgumentNullException.ThrowIfNull(reg);

        return reg is { Existed: true, Type: { } type, Value: { } value }
            ? new RegistryAction
            {
                Hive = reg.Hive,
                Path = reg.Path,
                Name = reg.Name,
                Operation = RegistryOperation.Set,
                Kind = type,
                Value = value,
            }
            : new RegistryAction
            {
                Hive = reg.Hive,
                Path = reg.Path,
                Name = reg.Name,
                Operation = RegistryOperation.DeleteValue,
            };
    }

    private static Built PlanRegistry(RegistryValueBackup reg)
    {
        if (!reg.Existed)
        {
            var operations = new List<PlannedOperation> { new RegistryDeleteValueOperation(reg.Hive, reg.Path, reg.Name) };

            if (reg.CreatedKeyRoot is { } root)
            {
                if (!TweakValidator.IsSameOrUnderKey(reg.Path, root))
                {
                    return new Built([], "a raiz de chaves criadas não é um prefixo do caminho do valor.");
                }

                operations.Add(new RegistryPruneKeysOperation(reg.Hive, reg.Path, root));
            }

            return new Built(operations);
        }

        if (reg.Type is not { } type || reg.Value is not { } element)
        {
            return new Built([], "valor anterior ilegível (tipo ou valor ausente).");
        }

        if (!RegistryValueConverter.TryConvert(type, element, out var value, out var error))
        {
            return new Built([], $"valor anterior ilegível ({error}).");
        }

        return new Built([new RegistrySetOperation(reg.Hive, reg.Path, reg.Name, type, value!)]);
    }

    private static Built PlanService(ServiceBackup svc)
    {
        if (!svc.Existed)
        {
            return new Built([]);
        }

        var operations = new List<PlannedOperation> { new ServiceSetStartTypeOperation(svc.Name, svc.StartType) };

        // Se o serviço rodava antes e o tweak o parou, volta a iniciá-lo (a menos que fique desativado).
        if (svc.WasRunning && svc.StartType != ServiceStartType.Disabled)
        {
            operations.Add(new ServiceStartOperation(svc.Name));
        }

        return new Built(operations);
    }
}
