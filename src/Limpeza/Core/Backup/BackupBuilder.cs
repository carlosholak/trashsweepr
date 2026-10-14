using System.Text.Json;
using TrashSweepr.Limpeza.Core.Catalog;
using TrashSweepr.Limpeza.Core.Models;
using TrashSweepr.Limpeza.Core.Operations;

namespace TrashSweepr.Limpeza.Core.Backup;

/// <summary>Não foi possível salvar o estado anterior com segurança; nada deve ser alterado.</summary>
public sealed class BackupNotPossibleException(string message) : Exception(message);

/// <summary>Lê o estado atual dos alvos das operações e o transforma em itens de backup.</summary>
public static class BackupBuilder
{
    public static async Task<IReadOnlyList<BackupItem>> CaptureAsync(
        IEnumerable<PlannedOperation> operations,
        SystemServices system,
        CancellationToken ct)
    {
        var items = new Dictionary<string, BackupItem>(StringComparer.Ordinal);

        foreach (var operation in operations)
        {
            var item = await CaptureOneAsync(operation, system, ct).ConfigureAwait(false);
            if (item is not null)
            {
                // O primeiro estado lido de cada alvo é o original; leituras repetidas são ignoradas.
                items.TryAdd(item.Key, item);
            }
        }

        return [.. items.Values];
    }

    private static async Task<BackupItem?> CaptureOneAsync(PlannedOperation operation, SystemServices system, CancellationToken ct)
    {
        switch (operation)
        {
            case RegistrySetOperation set:
                return CaptureRegistry(system, set.Hive, set.Path, set.Name, createsKey: true);

            case RegistryDeleteValueOperation del:
                return CaptureRegistry(system, del.Hive, del.Path, del.Name, createsKey: false);

            case ServiceSetStartTypeOperation svc:
                var service = await system.Services.GetAsync(svc.Name, ct).ConfigureAwait(false);
                return new ServiceBackup(svc.Name, service.Exists, service.StartType, service.IsRunning);

            case TaskSetEnabledOperation task:
                var taskState = await system.Tasks.GetAsync(task.TaskPath, task.TaskName, ct).ConfigureAwait(false);
                return new TaskBackup(task.TaskPath, task.TaskName, taskState.Exists, taskState.Enabled);

            case AppxRemoveOperation remove:
                return await CaptureAppxAsync(system, remove.PackageName, ct).ConfigureAwait(false);

            case AppxReinstallOperation add:
                return await CaptureAppxAsync(system, add.PackageName, ct).ConfigureAwait(false);

            default:
                // Parar/iniciar serviço e apagar chaves não têm estado próprio a salvar:
                // o estado do serviço é capturado pela operação de tipo de inicialização.
                return null;
        }
    }

    private static RegistryValueBackup CaptureRegistry(SystemServices system, Models.RegistryHive hive, string path, string name, bool createsKey)
    {
        var current = system.Registry.ReadValue(hive, path, name);

        if (current.UnsupportedKind is not null)
        {
            throw new BackupNotPossibleException(
                $"O valor {hive}\\{path}\\{(name.Length == 0 ? "(Default)" : name)} é do tipo {current.UnsupportedKind}, que o trashsweepr não consegue salvar. Nada foi alterado.");
        }

        if (current.Exists)
        {
            var element = JsonSerializer.SerializeToElement(current.Value);
            var backup = new RegistryValueBackup(hive, path, name, true, current.Type, element, null);

            // O RestorePlanner valida o valor anterior como um 'set'; se ele não passaria, o backup não poderia
            // ser restaurado, então é melhor não alterar nada agora.
            var problems = TweakValidator.ValidateAction(RestorePlanner.ToAction(backup), allowDeleteKey: false);
            if (problems.Count > 0)
            {
                throw new BackupNotPossibleException(
                    $"O valor atual de {hive}\\{path}\\{(name.Length == 0 ? "(Default)" : name)} não poderia ser restaurado ({string.Join(" ", problems)}). Nada foi alterado.");
            }

            return backup;
        }

        var createdRoot = createsKey ? FirstMissingKey(system, hive, path) : null;
        return new RegistryValueBackup(hive, path, name, false, null, null, createdRoot);
    }

    private static string? FirstMissingKey(SystemServices system, Models.RegistryHive hive, string path)
    {
        var segments = path.Split('\\');
        var current = string.Empty;

        foreach (var segment in segments)
        {
            current = current.Length == 0 ? segment : current + "\\" + segment;
            if (!system.Registry.KeyExists(hive, current))
            {
                return current;
            }
        }

        return null;
    }

    private static async Task<AppxBackup> CaptureAppxAsync(SystemServices system, string packageName, CancellationToken ct)
    {
        var state = await system.Appx.GetAsync(packageName, ct).ConfigureAwait(false);
        return new AppxBackup(packageName, state.InstalledForCurrentUser, state.Packages);
    }
}
