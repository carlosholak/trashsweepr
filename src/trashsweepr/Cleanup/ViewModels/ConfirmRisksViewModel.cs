using CommunityToolkit.Mvvm.ComponentModel;
using TrashSweepr.Cleanup.Services;
using TrashSweepr.Cleanup.Core.Models;
using TrashSweepr.Localization;

namespace TrashSweepr.Cleanup.ViewModels;

public sealed record RiskConfirmationRow(string Name, string RiskText, Tone Tone, string Note);

/// <summary>Dados do diálogo de confirmação de risco. "Continuar" só habilita depois de marcar a ciência.</summary>
public sealed partial class ConfirmRisksViewModel : ObservableObject
{
    public ConfirmRisksViewModel(IReadOnlyList<RiskConfirmationItem> items)
    {
        Items =
        [
            .. items
                .OrderByDescending(i => i.Risk)
                .Select(i => new RiskConfirmationRow(
                    i.Name,
                    Loc.Get($"Risk.{i.Risk}"),
                    i.Risk.ToTone(),
                    i.Note)),
        ];
    }

    public IReadOnlyList<RiskConfirmationRow> Items { get; }

    [ObservableProperty]
    private bool _accepted;
}
