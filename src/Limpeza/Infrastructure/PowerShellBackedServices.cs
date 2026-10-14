using System.Text.Json;
using TrashSweepr.Limpeza.Core.Abstractions;
using TrashSweepr.Limpeza.Core.Models;

namespace TrashSweepr.Limpeza.Infrastructure;

internal static class PowerShellExtensions
{
    /// <summary>Executa e lança <see cref="InvalidOperationException"/> com a mensagem do PowerShell se falhar.</summary>
    public static async Task<string> RunOrThrowAsync(this IPowerShellRunner runner, PowerShellCall call, CancellationToken ct)
    {
        var result = await runner.RunAsync(call, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"O PowerShell terminou com código {result.ExitCode}."
                : result.StandardError;
            throw new InvalidOperationException(message);
        }

        return result.StandardOutput;
    }

    public static JsonElement ParseObject(string output, string what)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Resposta inesperada do PowerShell ao consultar {what}: {ex.Message}", ex);
        }
    }

    public static bool GetBool(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static string? GetString(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed class ServiceManager(IPowerShellRunner runner) : IServiceManager
{
    public async Task<ServiceState> GetAsync(string name, CancellationToken cancellationToken)
    {
        var json = PowerShellExtensions.ParseObject(
            await runner.RunOrThrowAsync(PowerShellCalls.GetService(name), cancellationToken).ConfigureAwait(false),
            $"o serviço {name}");

        if (!json.GetBool("exists"))
        {
            return ServiceState.Missing;
        }

        var startType = json.GetString("startType") switch
        {
            "Automatic" => json.GetBool("delayed") ? ServiceStartType.AutomaticDelayed : ServiceStartType.Automatic,
            "Manual" => ServiceStartType.Manual,
            "Disabled" => ServiceStartType.Disabled,
            var other => throw new InvalidOperationException(
                $"O serviço {name} tem tipo de inicialização '{other}', que o trashsweepr não sabe salvar nem restaurar."),
        };

        return new ServiceState(true, startType, json.GetBool("running"));
    }

    public Task SetStartTypeAsync(string name, ServiceStartType startType, CancellationToken cancellationToken) =>
        runner.RunOrThrowAsync(PowerShellCalls.SetServiceStartType(name, startType), cancellationToken);

    public Task StopAsync(string name, CancellationToken cancellationToken) =>
        runner.RunOrThrowAsync(PowerShellCalls.StopService(name), cancellationToken);

    public Task StartAsync(string name, CancellationToken cancellationToken) =>
        runner.RunOrThrowAsync(PowerShellCalls.StartService(name), cancellationToken);
}

public sealed class TaskSchedulerService(IPowerShellRunner runner) : ITaskScheduler
{
    public async Task<TaskState> GetAsync(string taskPath, string taskName, CancellationToken cancellationToken)
    {
        var json = PowerShellExtensions.ParseObject(
            await runner.RunOrThrowAsync(PowerShellCalls.GetTask(taskPath, taskName), cancellationToken).ConfigureAwait(false),
            $"a tarefa {taskPath}{taskName}");

        return json.GetBool("exists") ? new TaskState(true, json.GetBool("enabled")) : TaskState.Missing;
    }

    public Task SetEnabledAsync(string taskPath, string taskName, bool enabled, CancellationToken cancellationToken) =>
        runner.RunOrThrowAsync(PowerShellCalls.SetTaskEnabled(taskPath, taskName, enabled), cancellationToken);
}

public sealed class AppxService(IPowerShellRunner runner) : IAppxService
{
    public async Task<IReadOnlySet<string>> ListInstalledNamesAsync(CancellationToken cancellationToken)
    {
        var json = PowerShellExtensions.ParseObject(
            await runner.RunOrThrowAsync(PowerShellCalls.ListAppx(), cancellationToken).ConfigureAwait(false),
            "os apps instalados");

        // O PowerShell 5.1 pode serializar uma lista de um único item como string solta.
        var names = json.ValueKind switch
        {
            JsonValueKind.Array => json.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!),
            JsonValueKind.String => [json.GetString()!],
            _ => [],
        };

        return names.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<AppxPackageState> GetAsync(string packageName, CancellationToken cancellationToken)
    {
        var json = PowerShellExtensions.ParseObject(
            await runner.RunOrThrowAsync(PowerShellCalls.GetAppx(packageName), cancellationToken).ConfigureAwait(false),
            $"o app {packageName}");

        var packages = new List<AppxPackageFile>();
        if (json.TryGetProperty("packages", out var list))
        {
            var items = list.ValueKind switch
            {
                JsonValueKind.Array => list.EnumerateArray().ToList(),
                JsonValueKind.Object => [list],
                _ => [],
            };

            packages.AddRange(items
                .Select(item => new AppxPackageFile(item.GetString("fullName") ?? string.Empty, item.GetString("installLocation")))
                .Where(p => p.FullName.Length > 0));
        }

        return new AppxPackageState(json.GetBool("installed"), packages);
    }

    public Task RemoveAsync(string packageName, CancellationToken cancellationToken) =>
        runner.RunOrThrowAsync(PowerShellCalls.RemoveAppx(packageName), cancellationToken);

    public Task ReinstallAsync(string packageName, CancellationToken cancellationToken) =>
        runner.RunOrThrowAsync(PowerShellCalls.ReinstallAppx(packageName), cancellationToken);
}

public sealed class RestorePointService(IPowerShellRunner runner) : IRestorePointService
{
    public async Task<bool> IsProtectionEnabledAsync(CancellationToken cancellationToken)
    {
        var json = PowerShellExtensions.ParseObject(
            await runner.RunOrThrowAsync(PowerShellCalls.GetRestoreProtection(), cancellationToken).ConfigureAwait(false),
            "a Proteção do Sistema");

        return json.GetBool("enabled");
    }

    public async Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(PowerShellCalls.CreateRestorePoint(description), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new RestorePointResult(RestorePointOutcome.Failed, string.IsNullOrWhiteSpace(result.StandardError) ? $"código {result.ExitCode}" : result.StandardError);
        }

        var json = PowerShellExtensions.ParseObject(result.StandardOutput, "o ponto de restauração");
        return json.GetBool("created")
            ? new RestorePointResult(RestorePointOutcome.Created)
            : new RestorePointResult(
                RestorePointOutcome.Throttled,
                "Nenhum ponto novo foi criado: o Windows só permite um a cada 24 horas por padrão, então já deve existir um recente.");
    }
}
