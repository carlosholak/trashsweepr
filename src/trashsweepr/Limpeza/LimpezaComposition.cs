using TrashSweepr.Limpeza.Core.Catalog;
using TrashSweepr.Limpeza.Core.Logging;
using TrashSweepr.Limpeza.Infrastructure;
using TrashSweepr.Limpeza.Services;
using TrashSweepr.Limpeza.ViewModels;

namespace TrashSweepr.Limpeza;

/// <summary>Monta a seção "Limpeza do Windows" com as implementações reais (acesso ao registro, serviços, PowerShell).</summary>
internal static class LimpezaComposition
{
    public static (LimpezaViewModel ViewModel, IDisposable Runtime) Create()
    {
        var runtime = LimpezaRuntime.Create();

        var catalog = TweakCatalogLoader.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "tweaks"));
        foreach (var issue in catalog.Issues)
        {
            runtime.Log.Write(new LogEntry(DateTimeOffset.Now, LogSeverity.Error, issue.TweakId, "catalog", issue.Source, "invalid", issue.Message));
        }

        var viewModel = new LimpezaViewModel(
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