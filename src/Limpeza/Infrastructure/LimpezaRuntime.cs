using System.Runtime.Versioning;
using TrashSweepr.Limpeza.Core.Abstractions;
using TrashSweepr.Limpeza.Core.Backup;
using TrashSweepr.Limpeza.Core.Execution;
using TrashSweepr.Limpeza.Core.Logging;
using TrashSweepr.Limpeza.Core.Operations;

namespace TrashSweepr.Limpeza.Infrastructure;

/// <summary>Onde o trashsweepr guarda backups e logs (%ProgramData%\trashsweepr\limpeza por padrão).</summary>
public sealed record AppPaths(string DataDirectory)
{
    public string BackupDirectory => Path.Combine(DataDirectory, "backups");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "trashsweepr",
        "limpeza"));
}

/// <summary>Tudo o que a interface precisa, já montado com as implementações reais.</summary>
[SupportedOSPlatform("windows")]
public sealed class LimpezaRuntime : IDisposable
{
    private LimpezaRuntime(
        AppPaths paths,
        IWindowsInfo windows,
        TweakEngine engine,
        TweakStateDetector detector,
        BackupStore backups,
        IRestorePointService restorePoints,
        IOperationLog log)
    {
        Log = log;
        Paths = paths;
        Windows = windows;
        Engine = engine;
        Detector = detector;
        Backups = backups;
        RestorePoints = restorePoints;
    }

    public IOperationLog Log { get; }

    public IRestorePointService RestorePoints { get; }

    public AppPaths Paths { get; }

    public IWindowsInfo Windows { get; }

    public TweakEngine Engine { get; }

    public TweakStateDetector Detector { get; }

    public BackupStore Backups { get; }

    public static LimpezaRuntime Create(AppPaths? paths = null)
    {
        paths ??= AppPaths.Default;

        var runner = new PowerShellRunner();
        var system = new SystemServices(new RegistryService(), new ServiceManager(runner), new TaskSchedulerService(runner), new AppxService(runner));
        var windows = new WindowsInfo();
        var backups = new BackupStore(paths.BackupDirectory);
        var log = new FileOperationLog(paths.LogDirectory);

        var restorePoints = new RestorePointService(runner);
        var engine = new TweakEngine(system, restorePoints, windows, backups, log);
        var detector = new TweakStateDetector(system, windows);

        return new LimpezaRuntime(paths, windows, engine, detector, backups, restorePoints, log);
    }

    public void Dispose() => Backups.Dispose();
}
