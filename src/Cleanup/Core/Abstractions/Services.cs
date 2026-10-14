using TrashSweepr.Cleanup.Core.Models;

namespace TrashSweepr.Cleanup.Core.Abstractions;

/// <summary>Valor de registro lido. <see cref="UnsupportedKind"/> indica tipos que o trashsweepr não sabe salvar (ex.: binário).</summary>
public sealed record RegistryValueSnapshot(bool Exists, RegistryValueType? Type, object? Value, string? UnsupportedKind = null)
{
    public static RegistryValueSnapshot Missing { get; } = new(false, null, null);
}

public sealed record ServiceState(bool Exists, ServiceStartType StartType, bool IsRunning)
{
    public static ServiceState Missing { get; } = new(false, ServiceStartType.Manual, false);
}

public sealed record TaskState(bool Exists, bool Enabled)
{
    public static TaskState Missing { get; } = new(false, false);
}

public sealed record AppxPackageFile(string FullName, string? InstallLocation);

/// <param name="InstalledForCurrentUser">O pacote está instalado para o usuário atual.</param>
/// <param name="Packages">Pacotes presentes no sistema (qualquer usuário), usados para reinstalar.</param>
public sealed record AppxPackageState(bool InstalledForCurrentUser, IReadOnlyList<AppxPackageFile> Packages)
{
    public static AppxPackageState Missing { get; } = new(false, []);
}

public enum RestorePointOutcome
{
    Created,
    ProtectionDisabled,
    Throttled,
    Failed,
}

public sealed record RestorePointResult(RestorePointOutcome Outcome, string? Detail = null);

/// <summary>Acesso ao registro. Caminhos e nomes já foram validados pelo engine.</summary>
public interface IRegistryService
{
    RegistryValueSnapshot ReadValue(RegistryHive hive, string path, string name);

    bool KeyExists(RegistryHive hive, string path);

    /// <summary>Define o valor, criando a chave (e as intermediárias) se necessário.</summary>
    void SetValue(RegistryHive hive, string path, string name, RegistryValueType type, object value);

    /// <summary>Remove o valor; não faz nada se ele não existir.</summary>
    void DeleteValue(RegistryHive hive, string path, string name);

    /// <summary>Remove a chave e tudo abaixo dela; não faz nada se ela não existir.</summary>
    void DeleteKeyTree(RegistryHive hive, string path);

    /// <summary>Remove a chave apenas se não tiver valores nem subchaves. Retorna true se removeu.</summary>
    bool DeleteKeyIfEmpty(RegistryHive hive, string path);
}

public interface IServiceManager
{
    Task<ServiceState> GetAsync(string name, CancellationToken cancellationToken);

    Task SetStartTypeAsync(string name, ServiceStartType startType, CancellationToken cancellationToken);

    Task StopAsync(string name, CancellationToken cancellationToken);

    Task StartAsync(string name, CancellationToken cancellationToken);
}

public interface ITaskScheduler
{
    Task<TaskState> GetAsync(string taskPath, string taskName, CancellationToken cancellationToken);

    Task SetEnabledAsync(string taskPath, string taskName, bool enabled, CancellationToken cancellationToken);
}

public interface IAppxService
{
    /// <summary>Nomes dos pacotes instalados para o usuário atual (uma consulta só, para detectar vários apps).</summary>
    Task<IReadOnlySet<string>> ListInstalledNamesAsync(CancellationToken cancellationToken);

    Task<AppxPackageState> GetAsync(string packageName, CancellationToken cancellationToken);

    Task RemoveAsync(string packageName, CancellationToken cancellationToken);

    /// <summary>Registra novamente o pacote a partir dos arquivos que o Windows ainda mantém. Falha se não houver.</summary>
    Task ReinstallAsync(string packageName, CancellationToken cancellationToken);
}

public interface IRestorePointService
{
    /// <summary>Verifica se a Proteção do Sistema está ativa (sem criar nada).</summary>
    Task<bool> IsProtectionEnabledAsync(CancellationToken cancellationToken);

    Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken);
}

public interface IWindowsInfo
{
    int Build { get; }

    string? DisplayVersion { get; }

    bool IsElevated { get; }
}
