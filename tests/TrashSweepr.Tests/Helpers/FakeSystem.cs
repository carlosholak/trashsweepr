using TrashSweepr.Cleanup.Core;
using TrashSweepr.Cleanup.Core.Abstractions;
using TrashSweepr.Cleanup.Core.Backup;
using TrashSweepr.Cleanup.Core.Execution;
using TrashSweepr.Cleanup.Core.Logging;
using TrashSweepr.Cleanup.Core.Models;
using TrashSweepr.Cleanup.Core.Operations;

namespace TrashSweepr.Tests.Helpers;

/// <summary>Registro em memória, com chaves hierárquicas e injeção de falhas.</summary>
internal sealed class FakeRegistry(List<string> mutations) : IRegistryService
{
    private readonly Dictionary<string, Dictionary<string, RegistryValueSnapshot>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public Func<string, bool>? FailOnSet { get; set; }

    private static string KeyId(RegistryHive hive, string path) => $"{hive}\\{path}";

    public void Seed(RegistryHive hive, string path, string name, RegistryValueType type, object value)
    {
        CreateKey(hive, path);
        _keys[KeyId(hive, path)][name] = new RegistryValueSnapshot(true, type, value);
    }

    public void SeedBinary(RegistryHive hive, string path, string name)
    {
        CreateKey(hive, path);
        _keys[KeyId(hive, path)][name] = new RegistryValueSnapshot(true, null, null, "Binary");
    }

    public void SeedKey(RegistryHive hive, string path) => CreateKey(hive, path);

    public bool HasKey(RegistryHive hive, string path) => _keys.ContainsKey(KeyId(hive, path));

    public object? Get(RegistryHive hive, string path, string name) =>
        _keys.TryGetValue(KeyId(hive, path), out var values) && values.TryGetValue(name, out var v) ? v.Value : null;

    public RegistryValueSnapshot ReadValue(RegistryHive hive, string path, string name) =>
        _keys.TryGetValue(KeyId(hive, path), out var values) && values.TryGetValue(name, out var v)
            ? v
            : RegistryValueSnapshot.Missing;

    public bool KeyExists(RegistryHive hive, string path) => _keys.ContainsKey(KeyId(hive, path));

    public void SetValue(RegistryHive hive, string path, string name, RegistryValueType type, object value)
    {
        var target = $"{hive}\\{path}\\{name}";
        if (FailOnSet?.Invoke(target) == true)
        {
            throw new UnauthorizedAccessException($"Acesso negado a {target}.");
        }

        mutations.Add($"reg set {target}");
        CreateKey(hive, path);
        _keys[KeyId(hive, path)][name] = new RegistryValueSnapshot(true, type, value);
    }

    public void DeleteValue(RegistryHive hive, string path, string name)
    {
        mutations.Add($"reg delete-value {hive}\\{path}\\{name}");
        if (_keys.TryGetValue(KeyId(hive, path), out var values))
        {
            values.Remove(name);
        }
    }

    public void DeleteKeyTree(RegistryHive hive, string path)
    {
        mutations.Add($"reg delete-tree {hive}\\{path}");
        var id = KeyId(hive, path);
        foreach (var key in _keys.Keys.Where(k => k.Equals(id, StringComparison.OrdinalIgnoreCase) || k.StartsWith(id + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _keys.Remove(key);
        }
    }

    public bool DeleteKeyIfEmpty(RegistryHive hive, string path)
    {
        var id = KeyId(hive, path);
        if (!_keys.TryGetValue(id, out var values) || values.Count > 0
            || _keys.Keys.Any(k => k.StartsWith(id + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        mutations.Add($"reg delete-empty-key {hive}\\{path}");
        _keys.Remove(id);
        return true;
    }

    private void CreateKey(RegistryHive hive, string path)
    {
        var current = string.Empty;
        foreach (var segment in path.Split('\\'))
        {
            current = current.Length == 0 ? segment : current + "\\" + segment;
            var id = KeyId(hive, current);
            if (!_keys.ContainsKey(id))
            {
                _keys[id] = new Dictionary<string, RegistryValueSnapshot>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}

internal sealed class FakeServiceManager(List<string> mutations) : IServiceManager
{
    public Dictionary<string, ServiceState> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FailSetStartType { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Aceita o comando mas não muda nada (simula um comando que "funciona" sem efeito).</summary>
    public HashSet<string> IgnoreSetStartType { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Action<string>? OnSetStartType { get; set; }

    public int GetCalls { get; private set; }

    public Task<ServiceState> GetAsync(string name, CancellationToken cancellationToken)
    {
        GetCalls++;
        return Task.FromResult(Services.GetValueOrDefault(name, ServiceState.Missing));
    }

    public Task SetStartTypeAsync(string name, ServiceStartType startType, CancellationToken cancellationToken)
    {
        OnSetStartType?.Invoke(name);
        cancellationToken.ThrowIfCancellationRequested();

        // A falha vale uma única vez: o rollback que vem depois precisa conseguir restaurar.
        if (FailSetStartType.Remove(name))
        {
            throw new InvalidOperationException($"sc.exe config falhou para {name}.");
        }

        mutations.Add($"svc set-start {name} {startType}");
        if (!IgnoreSetStartType.Contains(name))
        {
            Services[name] = Services[name] with { StartType = startType };
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(string name, CancellationToken cancellationToken)
    {
        mutations.Add($"svc stop {name}");
        Services[name] = Services[name] with { IsRunning = false };
        return Task.CompletedTask;
    }

    public Task StartAsync(string name, CancellationToken cancellationToken)
    {
        mutations.Add($"svc start {name}");
        Services[name] = Services[name] with { IsRunning = true };
        return Task.CompletedTask;
    }
}

internal sealed class FakeTaskScheduler(List<string> mutations) : ITaskScheduler
{
    public Dictionary<string, TaskState> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int GetCalls { get; private set; }

    public Task<TaskState> GetAsync(string taskPath, string taskName, CancellationToken cancellationToken)
    {
        GetCalls++;
        return Task.FromResult(Tasks.GetValueOrDefault(taskPath + taskName, TaskState.Missing));
    }

    public Task SetEnabledAsync(string taskPath, string taskName, bool enabled, CancellationToken cancellationToken)
    {
        mutations.Add($"task set-enabled {taskPath}{taskName} {enabled}");
        Tasks[taskPath + taskName] = new TaskState(true, enabled);
        return Task.CompletedTask;
    }
}

internal sealed class FakeAppxService(List<string> mutations) : IAppxService
{
    public Dictionary<string, AppxPackageState> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FailRemove { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int ListCalls { get; private set; }

    public void Install(string name, bool staged = true) =>
        Packages[name] = new AppxPackageState(true, staged ? [new AppxPackageFile(name + "_1.0_x64__abc", @"C:\WindowsApps\" + name)] : []);

    public Task<IReadOnlySet<string>> ListInstalledNamesAsync(CancellationToken cancellationToken)
    {
        ListCalls++;
        IReadOnlySet<string> set = Packages.Where(p => p.Value.InstalledForCurrentUser).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(set);
    }

    public Task<AppxPackageState> GetAsync(string packageName, CancellationToken cancellationToken) =>
        Task.FromResult(Packages.GetValueOrDefault(packageName, AppxPackageState.Missing));

    public Task RemoveAsync(string packageName, CancellationToken cancellationToken)
    {
        if (FailRemove.Contains(packageName))
        {
            throw new InvalidOperationException($"Remove-AppxPackage falhou para {packageName}.");
        }

        mutations.Add($"appx remove {packageName}");
        if (Packages.TryGetValue(packageName, out var state))
        {
            Packages[packageName] = state with { InstalledForCurrentUser = false };
        }

        return Task.CompletedTask;
    }

    public Task ReinstallAsync(string packageName, CancellationToken cancellationToken)
    {
        if (!Packages.TryGetValue(packageName, out var state) || state.Packages.Count == 0)
        {
            throw new InvalidOperationException("Pacote não encontrado no sistema. Reinstale pela Microsoft Store.");
        }

        mutations.Add($"appx reinstall {packageName}");
        Packages[packageName] = state with { InstalledForCurrentUser = true };
        return Task.CompletedTask;
    }
}

/// <summary>Programas (Edge, OneDrive) em memória, com injeção de falhas iguais às do serviço real.</summary>
internal sealed class FakeProgramService(List<string> mutations) : IProgramService
{
    public HashSet<ManagedProgram> Installed { get; } = [];

    /// <summary>O desinstalador recusa (para o Edge: a trava regional da Microsoft). Nada muda.</summary>
    public HashSet<ManagedProgram> FailRemove { get; } = [];

    /// <summary>O instalador falha (ex.: sem winget e sem internet). Nada muda.</summary>
    public HashSet<ManagedProgram> FailReinstall { get; } = [];

    public int UninstallCalls { get; private set; }

    public int ReinstallCalls { get; private set; }

    public void Install(params ManagedProgram[] programs) => Installed.UnionWith(programs);

    public Task<bool> IsInstalledAsync(ManagedProgram program, CancellationToken cancellationToken) =>
        Task.FromResult(Installed.Contains(program));

    public Task UninstallAsync(ManagedProgram program, CancellationToken cancellationToken)
    {
        UninstallCalls++;
        if (FailRemove.Contains(program))
        {
            // Mesmas mensagens do ProgramService real.
            throw new InvalidOperationException(program == ManagedProgram.Edge
                ? CoreText.Format("Program.EdgeRemovalRefused", 93)
                : CoreText.Format("Program.UninstallFailed", ManagedPrograms.DisplayName(program), 1));
        }

        mutations.Add($"program uninstall {ManagedPrograms.JsonName(program)}");
        Installed.Remove(program);
        return Task.CompletedTask;
    }

    public Task ReinstallAsync(ManagedProgram program, CancellationToken cancellationToken)
    {
        ReinstallCalls++;
        if (FailReinstall.Contains(program))
        {
            throw new InvalidOperationException(program == ManagedProgram.Edge
                ? CoreText.Get("Program.EdgeInstallerUnavailable")
                : CoreText.Get("Program.OneDriveInstallerUnavailable"));
        }

        mutations.Add($"program reinstall {ManagedPrograms.JsonName(program)}");
        Installed.Add(program);
        return Task.CompletedTask;
    }
}

internal sealed class FakeRestorePoints : IRestorePointService
{
    public bool ProtectionEnabled { get; set; } = true;

    public RestorePointResult CreateResult { get; set; } = new(RestorePointOutcome.Created);

    public int CreateCalls { get; private set; }

    public Task<bool> IsProtectionEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(ProtectionEnabled);

    public Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken)
    {
        CreateCalls++;
        return Task.FromResult(CreateResult);
    }
}

internal sealed class FakeWindows : IWindowsInfo
{
    public int Build { get; set; } = 22631;

    public string? DisplayVersion => "23H2";

    public bool IsElevated { get; set; } = true;
}

internal sealed class MemoryLog : IOperationLog
{
    public List<LogEntry> Entries { get; } = [];

    public void Write(LogEntry entry) => Entries.Add(entry);
}

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        // Cada leitura avança 1s: garante ordem estável entre backups criados em sequência.
        _now = _now.AddSeconds(1);
        return _now;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>Sistema falso completo + engine pronto para uso. Descarta o diretório de backups ao final.</summary>
internal sealed class FakeSystem : IDisposable
{
    public FakeSystem()
    {
        Registry = new FakeRegistry(Mutations);
        Services = new FakeServiceManager(Mutations);
        Tasks = new FakeTaskScheduler(Mutations);
        Appx = new FakeAppxService(Mutations);
        Programs = new FakeProgramService(Mutations);
        BackupDirectory = Path.Combine(Path.GetTempPath(), "trashsweepr-tests-" + Guid.NewGuid().ToString("N"));
        Backups = new BackupStore(BackupDirectory);
        Engine = new TweakEngine(Bundle, RestorePoints, Windows, Backups, Log, Time);
        Detector = new TweakStateDetector(Bundle, Windows);
    }

    public List<string> Mutations { get; } = [];

    public FakeRegistry Registry { get; }

    public FakeServiceManager Services { get; }

    public FakeTaskScheduler Tasks { get; }

    public FakeAppxService Appx { get; }

    public FakeProgramService Programs { get; }

    public FakeRestorePoints RestorePoints { get; } = new();

    public FakeWindows Windows { get; } = new();

    public MemoryLog Log { get; } = new();

    public ManualTime Time { get; } = new();

    public string BackupDirectory { get; }

    public BackupStore Backups { get; }

    public TweakEngine Engine { get; }

    public TweakStateDetector Detector { get; }

    public SystemServices Bundle => new(Registry, Services, Tasks, Appx, Programs);

    public static ExecutionOptions Real { get; } = new();

    public static ExecutionOptions Dry { get; } = new() { DryRun = true };

    public static ExecutionOptions Confirmed(params string[] ids) => new() { ConfirmedTweakIds = ids.ToHashSet() };

    public int BackupFileCount() =>
        Directory.Exists(BackupDirectory) ? Directory.GetFiles(BackupDirectory).Length : 0;

    public void Dispose()
    {
        Backups.Dispose();
        if (Directory.Exists(BackupDirectory))
        {
            Directory.Delete(BackupDirectory, recursive: true);
        }
    }
}
