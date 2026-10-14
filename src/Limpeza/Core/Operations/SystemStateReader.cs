using TrashSweepr.Limpeza.Core.Abstractions;
using TrashSweepr.Limpeza.Core.Models;

namespace TrashSweepr.Limpeza.Core.Operations;

public enum OperationState
{
    /// <summary>O sistema já está no estado que a operação produziria.</summary>
    Satisfied,
    NotSatisfied,

    /// <summary>O alvo não existe (serviço, tarefa) ou a operação não descreve um estado verificável.</summary>
    NotApplicable,
    Unknown,
}

/// <summary>
/// Lê o estado atual do sistema e o compara com o que uma operação produziria. Guarda em cache as
/// consultas lentas (PowerShell) durante a vida da instância: crie uma nova para cada varredura.
/// </summary>
public sealed class SystemStateReader(SystemServices system)
{
    private readonly Dictionary<string, Task<ServiceState>> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<TaskState>> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private Task<IReadOnlySet<string>>? _installedApps;

    public Task<ServiceState> GetServiceAsync(string name, CancellationToken ct) =>
        GetOrAdd(_services, name, () => system.Services.GetAsync(name, ct));

    public Task<TaskState> GetTaskAsync(string path, string name, CancellationToken ct) =>
        GetOrAdd(_tasks, path + name, () => system.Tasks.GetAsync(path, name, ct));

    public Task<IReadOnlySet<string>> GetInstalledAppsAsync(CancellationToken ct) =>
        _installedApps ??= system.Appx.ListInstalledNamesAsync(ct);

    public async Task<OperationState> CheckAsync(PlannedOperation operation, CancellationToken ct)
    {
        try
        {
            return operation switch
            {
                RegistrySetOperation set => CheckRegistrySet(set),
                RegistryDeleteValueOperation del => system.Registry.ReadValue(del.Hive, del.Path, del.Name).Exists
                    ? OperationState.NotSatisfied
                    : OperationState.Satisfied,
                RegistryDeleteKeyOperation key => system.Registry.KeyExists(key.Hive, key.Path)
                    ? OperationState.NotSatisfied
                    : OperationState.Satisfied,
                ServiceSetStartTypeOperation svc => await CheckServiceAsync(svc, ct).ConfigureAwait(false),
                TaskSetEnabledOperation task => await CheckTaskAsync(task, ct).ConfigureAwait(false),
                AppxRemoveOperation remove => (await GetInstalledAppsAsync(ct).ConfigureAwait(false)).Contains(remove.PackageName)
                    ? OperationState.NotSatisfied
                    : OperationState.Satisfied,
                AppxReinstallOperation add => (await GetInstalledAppsAsync(ct).ConfigureAwait(false)).Contains(add.PackageName)
                    ? OperationState.Satisfied
                    : OperationState.NotSatisfied,
                _ => OperationState.NotApplicable,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationState.Unknown;
        }
    }

    private OperationState CheckRegistrySet(RegistrySetOperation set)
    {
        var current = system.Registry.ReadValue(set.Hive, set.Path, set.Name);
        if (!current.Exists || current.Type != set.Type || current.UnsupportedKind is not null)
        {
            return OperationState.NotSatisfied;
        }

        return ValuesEqual(set.Type, current.Value, set.Value) ? OperationState.Satisfied : OperationState.NotSatisfied;
    }

    private async Task<OperationState> CheckServiceAsync(ServiceSetStartTypeOperation op, CancellationToken ct)
    {
        var state = await GetServiceAsync(op.Name, ct).ConfigureAwait(false);
        if (!state.Exists)
        {
            return OperationState.NotApplicable;
        }

        return state.StartType == op.StartType ? OperationState.Satisfied : OperationState.NotSatisfied;
    }

    private async Task<OperationState> CheckTaskAsync(TaskSetEnabledOperation op, CancellationToken ct)
    {
        var state = await GetTaskAsync(op.TaskPath, op.TaskName, ct).ConfigureAwait(false);
        if (!state.Exists)
        {
            return OperationState.NotApplicable;
        }

        return state.Enabled == op.Enabled ? OperationState.Satisfied : OperationState.NotSatisfied;
    }

    private static bool ValuesEqual(RegistryValueType type, object? current, object expected) => type switch
    {
        RegistryValueType.Dword or RegistryValueType.Qword =>
            current is not null && Convert.ToInt64(current, System.Globalization.CultureInfo.InvariantCulture)
                == Convert.ToInt64(expected, System.Globalization.CultureInfo.InvariantCulture),
        _ => string.Equals(current as string, expected as string, StringComparison.OrdinalIgnoreCase),
    };

    private static Task<T> GetOrAdd<T>(Dictionary<string, Task<T>> cache, string key, Func<Task<T>> factory)
    {
        if (!cache.TryGetValue(key, out var task))
        {
            task = factory();
            cache[key] = task;
        }

        return task;
    }
}

/// <summary>Agrupa os serviços de sistema usados pelo engine (todos substituíveis por fakes nos testes).</summary>
public sealed record SystemServices(
    IRegistryService Registry,
    IServiceManager Services,
    ITaskScheduler Tasks,
    IAppxService Appx);
