using TrashSweepr.Cleanup.Core.Catalog;
using TrashSweepr.Cleanup.Core.Logging;
using TrashSweepr.Cleanup.Infrastructure;
using TrashSweepr.Cleanup.Services;
using TrashSweepr.Cleanup.ViewModels;

namespace TrashSweepr.Cleanup;

/// <summary>Monta a seção "Limpeza do Windows" com as implementações reais (acesso ao registro, serviços, PowerShell).</summary>
internal static class CleanupComposition
{
    public static (CleanupViewModel ViewModel, IDisposable Runtime) Create()
    {
        var runtime = CleanupRuntime.Create();

        var catalog = TweakCatalogLoader.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "tweaks"));
        foreach (var issue in catalog.Issues)
        {
            runtime.Log.Write(new LogEntry(DateTimeOffset.Now, LogSeverity.Error, issue.TweakId, "catalog", issue.Source, "invalid", issue.Message));
        }

        var viewModel = new CleanupViewModel(
            catalog.Catalog,
            catalog.Issues.Count,
            runtime.Engine,
            runtime.Detector,
            runtime.Windows,
            runtime.RestorePoints,
            runtime.Backups,
            new WpfDialogService(),
            new ElevatedRelauncher(),
            new SystemRunGate(),
            runtime.Paths.LogDirectory);

        return (viewModel, runtime);
    }
}