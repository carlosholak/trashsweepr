using TrashSweepr.Cleanup.Core.Models;

namespace TrashSweepr.Cleanup.Core.Operations;

/// <summary>Executa operações já planejadas e validadas. É o único ponto que altera o sistema.</summary>
public sealed class OperationExecutor(SystemServices system)
{
    public async Task ExecuteAsync(PlannedOperation operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        switch (operation)
        {
            case RegistrySetOperation set:
                system.Registry.SetValue(set.Hive, set.Path, set.Name, set.Type, set.Value);
                break;

            case RegistryDeleteValueOperation del:
                system.Registry.DeleteValue(del.Hive, del.Path, del.Name);
                break;

            case RegistryDeleteKeyOperation key:
                system.Registry.DeleteKeyTree(key.Hive, key.Path);
                break;

            case RegistryPruneKeysOperation prune:
                Prune(prune);
                break;

            case ServiceSetStartTypeOperation svc:
                await system.Services.SetStartTypeAsync(svc.Name, svc.StartType, ct).ConfigureAwait(false);
                break;

            case ServiceStopOperation stop:
                await system.Services.StopAsync(stop.Name, ct).ConfigureAwait(false);
                break;

            case ServiceStartOperation start:
                await system.Services.StartAsync(start.Name, ct).ConfigureAwait(false);
                break;

            case TaskSetEnabledOperation task:
                await system.Tasks.SetEnabledAsync(task.TaskPath, task.TaskName, task.Enabled, ct).ConfigureAwait(false);
                break;

            case AppxRemoveOperation remove:
                await system.Appx.RemoveAsync(remove.PackageName, ct).ConfigureAwait(false);
                break;

            case AppxReinstallOperation add:
                if (add.OnlyIfMissing && (await system.Appx.GetAsync(add.PackageName, ct).ConfigureAwait(false)).InstalledForCurrentUser)
                {
                    break;
                }

                await system.Appx.ReinstallAsync(add.PackageName, ct).ConfigureAwait(false);
                break;

            case ProgramUninstallOperation uninstall:
                await system.Programs.UninstallAsync(uninstall.Program, ct).ConfigureAwait(false);
                break;

            case ProgramReinstallOperation reinstall:
                if (reinstall.OnlyIfMissing && await system.Programs.IsInstalledAsync(reinstall.Program, ct).ConfigureAwait(false))
                {
                    break;
                }

                await system.Programs.ReinstallAsync(reinstall.Program, ct).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException(CoreText.Format("Engine.UnsupportedOperation", operation.GetType().Name));
        }
    }

    private void Prune(RegistryPruneKeysOperation prune)
    {
        var current = prune.Path;
        while (system.Registry.DeleteKeyIfEmpty(prune.Hive, current))
        {
            if (string.Equals(current, prune.StopAtPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var separator = current.LastIndexOf('\\');
            if (separator < 0)
            {
                return;
            }

            current = current[..separator];
        }
    }
}
