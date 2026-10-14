using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrashSweepr.Cleanup.Services;
using TrashSweepr.Cleanup.Core.Execution;
using TrashSweepr.Localization;

namespace TrashSweepr.Cleanup.ViewModels;

/// <summary>Resultado de um tweak na lista de resultados (sucesso, falha, ignorado ou simulado).</summary>
public sealed partial class ResultItemViewModel : ObservableObject
{
    public ResultItemViewModel(string tweakName, TweakResult result)
    {
        TweakName = tweakName;
        Result = result;

        (StatusText, Tone) = result.Status switch
        {
            ResultStatus.Success => (Loc.Get("Result.Success"), Tone.Positive),
            ResultStatus.Failed => (Loc.Get("Result.Failed"), Tone.Danger),
            ResultStatus.DryRun => (Loc.Get("Result.DryRun"), Tone.Neutral),
            _ => (Loc.Get("Result.Skipped"), Tone.Muted),
        };

        Message = BuildMessage(result);
        Commands = [.. result.Operations.Select(o => o.Operation.Command)];
    }

    public TweakResult Result { get; }

    public string TweakName { get; }

    public string StatusText { get; }

    public Tone Tone { get; }

    public string Message { get; }

    public bool HasMessage => Message.Length > 0;

    public IReadOnlyList<string> Commands { get; }

    public bool HasCommands => Commands.Count > 0;

    public string CommandsText => string.Join(Environment.NewLine, Commands);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleCommandsText))]
    private bool _showCommands;

    public string ToggleCommandsText => Loc.Get(ShowCommands ? "Result.HideCommands" : "Result.ShowCommands");

    [RelayCommand]
    private void ToggleCommands() => ShowCommands = !ShowCommands;

    private static string BuildMessage(TweakResult result)
    {
        var parts = new List<string>();

        if (result.Status == ResultStatus.Skipped && result.SkipReason != SkipReason.None)
        {
            parts.Add(Loc.Get($"Skip.{result.SkipReason}"));
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            parts.Add(result.Error);
        }

        if (result.RolledBack)
        {
            parts.Add(Loc.Get("Result.RolledBack"));
        }

        if (result.UsedBackup && result.Status == ResultStatus.Success)
        {
            parts.Add(Loc.Get("Result.UsedBackup"));
        }

        if (result.RequiresReboot && result.Status == ResultStatus.Success)
        {
            parts.Add(Loc.Get("Result.Reboot"));
        }

        return string.Join(" ", parts);
    }
}
