using System.Windows;
using TrashSweepr.Cleanup.Views;
using TrashSweepr.Localization;

namespace TrashSweepr.Cleanup.Services;

/// <summary>Implementação real dos diálogos: janelas modais sobre a janela principal.</summary>
public sealed class WpfDialogService : IDialogService
{
    public Task<bool> ConfirmRisksAsync(IReadOnlyList<RiskConfirmationItem> items) =>
        Task.FromResult(Show(new ConfirmRisksDialog(items)));

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDanger = false) =>
        Task.FromResult(Show(new MessageDialog(title, message, confirmText, Loc.Get("Dialog.Cancel"), isDanger)));

    public Task ShowMessageAsync(string title, string message)
    {
        Show(MessageDialog.Info(title, message));
        return Task.CompletedTask;
    }

    private static bool Show(Window dialog)
    {
        dialog.Owner = Application.Current?.MainWindow;
        return dialog.ShowDialog() == true;
    }
}