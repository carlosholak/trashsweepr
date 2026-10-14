using System.Text.Json;
using TrashSweepr.Cleanup.Core.Catalog;
using TrashSweepr.Cleanup.Core.Models;
using TrashSweepr.Cleanup.Core.Operations;

namespace TrashSweepr.Cleanup.Core.Backup;

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

            case ProgramUninstallOperation uninstall:
                return new ProgramBackup(uninstall.Program, await system.Programs.IsInstalledAsync(uninstall.Program, ct).ConfigureAwait(false));

            case ProgramReinstallOperation reinstall:
                return new ProgramBackup(reinstall.Program, await system.Programs.IsInstalledAsync(reinstall.Program, ct).ConfigureAwait(false));

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
                CoreText.Format("Backup.UnsupportedValueType", Describe(hive, path, name), current.UnsupportedKind));
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
                    CoreText.Format("Backup.ValueNotRestorable", Describe(hive, path, name), string.Join(" ", problems)));
            }

            return backup;
        }

        var createdRoot = createsKey ? FirstMissingKey(system, hive, path) : null;
        return new RegistryValueBackup(hive, path, name, false, null, null, createdRoot);
    }

    private static string Describe(Models.RegistryHive hive, string path, string name) =>
        $"{hive}\\{path}\\{(name.Length == 0 ? "(Default)" : name)}";

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
