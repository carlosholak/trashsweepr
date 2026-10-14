using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TrashSweepr.Cleanup.Services;
using TrashSweepr.Cleanup.Core.Catalog;
using TrashSweepr.Cleanup.Core.Execution;
using TrashSweepr.Cleanup.Core.Models;
using TrashSweepr.Localization;

namespace TrashSweepr.Cleanup.ViewModels;

/// <summary>Um tweak na lista: texto já traduzido, estado detectado e seleção.</summary>
public sealed partial class TweakItemViewModel : ObservableObject
{
    private readonly int _windowsBuild;

    public TweakItemViewModel(Tweak tweak, int windowsBuild)
    {
        Tweak = tweak;
        _windowsBuild = windowsBuild;
        var culture = CultureInfo.CurrentUICulture;

        Name = tweak.Name.Resolve(culture);
        Description = tweak.Description.Resolve(culture);
        RiskNote = tweak.RiskNote?.Resolve(culture);
        RevertNote = tweak.RevertNote?.Resolve(culture);
        CategoryText = Loc.Get($"Category.{tweak.Category}");
        RiskText = Loc.Get($"Risk.{tweak.Risk}");
        RiskTone = tweak.Risk.ToTone();

        ApplyDescriptions = [.. tweak.Apply.Select(ActionDescriber.Describe)];
        RevertDescriptions = [.. tweak.Revert.Select(ActionDescriber.Describe)];
        UpdateDerived();
    }

    public Tweak Tweak { get; }

    public string Id => Tweak.Id;

    public string Name { get; }

    public string Description { get; }

    public string? RiskNote { get; }

    public string? RevertNote { get; }

    public string CategoryText { get; }

    public string RiskText { get; }

    public Tone RiskTone { get; }

    public RiskLevel Risk => Tweak.Risk;

    public TweakCategory Category => Tweak.Category;

    public bool RequiresReboot => Tweak.RequiresReboot;

    public bool IsOptIn => Tweak.OptIn;

    public bool HasRiskNote => !string.IsNullOrWhiteSpace(RiskNote);

    public bool HasRevertNote => !string.IsNullOrWhiteSpace(RevertNote);

    public IReadOnlyList<string> ApplyDescriptions { get; }

    public IReadOnlyList<string> RevertDescriptions { get; }

    /// <summary>Notifica o dono da lista quando o usuário marca/desmarca o item.</summary>
    public event EventHandler? SelectionChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    private TweakState? _state;

    [ObservableProperty]
    private bool _isSelected;

    public TweakAppliedState? Applied => State?.Applied;

    public bool IsCompatible => State?.Compatibility.IsCompatible ?? true;

    public bool IsApplied => Applied == TweakAppliedState.Applied;

    /// <summary>Pode ser desfeito: está aplicado (ou parcialmente aplicado).</summary>
    public bool CanRevert => Applied is TweakAppliedState.Applied or TweakAppliedState.Partial;

    /// <summary>Ainda há algo a fazer (ou o estado não é conhecido).</summary>
    public bool IsPending => Applied is null or TweakAppliedState.NotApplied or TweakAppliedState.Partial or TweakAppliedState.Unknown;

    /// <summary>Só dá para marcar o que é compatível e ainda tem algo a fazer.</summary>
    public bool CanSelect => State is null || (IsCompatible && IsPending);

    public string StateText { get; private set; } = string.Empty;

    public Tone StateTone { get; private set; } = Tone.Muted;

    public string? CompatibilityMessage { get; private set; }

    public bool HasCompatibilityMessage => CompatibilityMessage is not null;

    partial void OnStateChanged(TweakState? value)
    {
        UpdateDerived();
        OnPropertyChanged(nameof(Applied));
        OnPropertyChanged(nameof(IsCompatible));
        OnPropertyChanged(nameof(IsApplied));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(CanRevert));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateTone));
        OnPropertyChanged(nameof(CompatibilityMessage));
        OnPropertyChanged(nameof(HasCompatibilityMessage));

        // O que não pode mais ser marcado (já aplicado, incompatível) sai da seleção.
        if (!CanSelect && IsSelected)
        {
            IsSelected = false;
        }
    }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, EventArgs.Empty);

    private void UpdateDerived()
    {
        CompatibilityMessage = null;

        if (State is null)
        {
            StateText = Loc.Get("State.Checking");
            StateTone = Tone.Muted;
            return;
        }

        if (!State.Compatibility.IsCompatible)
        {
            StateText = Loc.Get("State.Incompatible");
            StateTone = Tone.Muted;
            CompatibilityMessage = DescribeCompatibility(State.Compatibility);
            return;
        }

        (StateText, StateTone) = State.Applied switch
        {
            TweakAppliedState.Applied => (Loc.Get("State.Applied"), Tone.Positive),
            TweakAppliedState.Partial => (Loc.Get("State.Partial"), Tone.Warning),
            TweakAppliedState.NotApplied => (Loc.Get("State.NotApplied"), Tone.Neutral),
            TweakAppliedState.NotApplicable => (Loc.Get("State.NotApplicable"), Tone.Muted),
            _ => (Loc.Get("State.Unknown"), Tone.Muted),
        };
    }

    private string DescribeCompatibility(CompatibilityResult result)
    {
        // Números de build não levam separador de milhar: formato invariante.
        var required = (result.RequiredBuild ?? TweakValidator.Windows11MinBuild).ToString(CultureInfo.InvariantCulture);
        var current = _windowsBuild.ToString(CultureInfo.InvariantCulture);

        return result.Status switch
        {
            CompatibilityStatus.NotWindows11 => Loc.Format("Compat.NotWindows11", required),
            CompatibilityStatus.BuildTooOld => Loc.Format("Compat.BuildTooOld", required, current),
            CompatibilityStatus.BuildTooNew => Loc.Format("Compat.BuildTooNew", required, current),
            _ => string.Empty,
        };
    }
}
