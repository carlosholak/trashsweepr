using CommunityToolkit.Mvvm.ComponentModel;
using TrashSweepr.Limpeza.Core.Models;

namespace TrashSweepr.Limpeza.ViewModels;

/// <summary>Cor semântica de um selo ou aviso; o XAML escolhe as cores do tema a partir dela.</summary>
public enum Tone
{
    Neutral,
    Positive,
    Warning,
    Danger,
    Muted,
}

public static class ToneExtensions
{
    /// <summary>Cor do selo de risco: Seguro em verde, Moderado em amarelo, Avançado em vermelho.</summary>
    public static Tone ToTone(this RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => Tone.Positive,
        RiskLevel.Moderate => Tone.Warning,
        _ => Tone.Danger,
    };
}

public enum StateFilterKind
{
    All,
    Pending,
    Applied,
}

/// <summary>Uma opção de um grupo de filtros (categoria, risco, estado ou perfil).</summary>
public sealed partial class FilterOption<T> : ObservableObject
{
    private FilterGroup<T>? _group;

    public FilterOption(string label, T value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }

    public T Value { get; }

    [ObservableProperty]
    private int _count;

    public bool IsSelected
    {
        get => _group?.Selected == this;
        set
        {
            if (value && _group is not null)
            {
                _group.Selected = this;
            }
        }
    }

    internal void Attach(FilterGroup<T> group) => _group = group;

    internal void NotifySelectionChanged() => OnPropertyChanged(nameof(IsSelected));
}

/// <summary>Grupo de opções mutuamente exclusivas, ligado a RadioButtons.</summary>
public sealed class FilterGroup<T> : ObservableObject
{
    private FilterOption<T> _selected;

    public FilterGroup(IEnumerable<FilterOption<T>> options)
    {
        Options = [.. options];
        if (Options.Count == 0)
        {
            throw new ArgumentException("Um grupo de filtros precisa de ao menos uma opção.", nameof(options));
        }

        foreach (var option in Options)
        {
            option.Attach(this);
        }

        _selected = Options[0];
    }

    public IReadOnlyList<FilterOption<T>> Options { get; }

    public event EventHandler? Changed;

    public FilterOption<T> Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value) || !Options.Contains(value))
            {
                return;
            }

            _selected = value;
            foreach (var option in Options)
            {
                option.NotifySelectionChanged();
            }

            OnPropertyChanged();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Seleciona sem disparar <see cref="Changed"/> (usado quando o próprio código muda o filtro).</summary>
    public void SelectSilently(FilterOption<T> option)
    {
        if (ReferenceEquals(_selected, option))
        {
            return;
        }

        _selected = option;
        foreach (var o in Options)
        {
            o.NotifySelectionChanged();
        }

        OnPropertyChanged(nameof(Selected));
    }
}

public sealed record BannerItem(string Text, Tone Tone, bool OffersElevation = false);
