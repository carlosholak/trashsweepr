using TrashSweepr.Cleanup.ViewModels;
using TrashSweepr.Cleanup.Core.Models;

namespace TrashSweepr.Cleanup.Services;

public sealed record RiskConfirmationItem(string Name, RiskLevel Risk, string Note);

/// <summary>Diálogos que o CleanupViewModel precisa. Em testes é substituído por um fake roteirizado.</summary>
public interface IDialogService
{
    /// <summary>Mostra os avisos de risco e retorna true somente se o usuário confirmar explicitamente.</summary>
    Task<bool> ConfirmRisksAsync(IReadOnlyList<RiskConfirmationItem> items);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDanger = false);

    Task ShowMessageAsync(string title, string message);
}
