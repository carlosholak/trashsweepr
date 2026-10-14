using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrashSweepr.Limpeza.Services;
using TrashSweepr.Limpeza.Core.Abstractions;
using TrashSweepr.Limpeza.Core.Backup;
using TrashSweepr.Limpeza.Core.Catalog;
using TrashSweepr.Limpeza.Core.Execution;
using TrashSweepr.Limpeza.Core.Models;

namespace TrashSweepr.Limpeza.ViewModels;

public sealed partial class LimpezaViewModel : ObservableObject
{
    private readonly TweakCatalog _catalog;
    private readonly TweakEngine _engine;
    private readonly TweakStateDetector _detector;
    private readonly IWindowsInfo _windows;
    private readonly IRestorePointService _restorePoints;
    private readonly BackupStore _backups;
    private readonly IDialogService _dialogs;
    private readonly IAppRelauncher _relauncher;
    private readonly IRunGate _gate;
    private readonly string _logDirectory;
    private readonly int _catalogIssueCount;
    private readonly List<TweakItemViewModel> _all;
    private readonly Dictionary<string, TweakItemViewModel> _byId;
    private readonly FilterOption<Profile?> _customProfile;

    private CancellationTokenSource? _cts;
    private int _busyDepth;
    private bool _suppressSelection;
    private bool _bulkStateUpdate;
    private bool? _protectionEnabled;
    private bool _protectionChecked;
    private bool _rebootPending;

    public LimpezaViewModel(
        TweakCatalog catalog,
        int catalogIssueCount,
        TweakEngine engine,
        TweakStateDetector detector,
        IWindowsInfo windows,
        IRestorePointService restorePoints,
        BackupStore backups,
        IDialogService dialogs,
        IAppRelauncher relauncher,
        IRunGate gate,
        string logDirectory)
    {
        _relauncher = relauncher;
        _gate = gate;
        _catalog = catalog;
        _catalogIssueCount = catalogIssueCount;
        _engine = engine;
        _detector = detector;
        _windows = windows;
        _restorePoints = restorePoints;
        _backups = backups;
        _dialogs = dialogs;
        _logDirectory = logDirectory;

        _all = [.. catalog.Tweaks
            .Select(t => new TweakItemViewModel(t, windows.Build))
            .OrderBy(t => t.Category)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
        _byId = _all.ToDictionary(t => t.Id, StringComparer.Ordinal);
        foreach (var item in _all)
        {
            item.SelectionChanged += OnItemSelectionChanged;
            item.PropertyChanged += (_, e) =>
            {
                if (!_bulkStateUpdate && e.PropertyName == nameof(TweakItemViewModel.State))
                {
                    UpdateCounts();
                }
            };
        }

        CategoryFilter = new FilterGroup<TweakCategory?>(
            new[] { new FilterOption<TweakCategory?>(Loc.Get("Filter.AllCategories"), null) { Count = _all.Count } }
                .Concat(Enum.GetValues<TweakCategory>().Select(c => new FilterOption<TweakCategory?>(Loc.Get($"Category.{c}"), c)
                {
                    Count = _all.Count(t => t.Category == c),
                })));

        RiskFilter = new FilterGroup<RiskLevel?>(
            new[] { new FilterOption<RiskLevel?>(Loc.Get("Filter.AllRisks"), null) { Count = _all.Count } }
                .Concat(Enum.GetValues<RiskLevel>().Select(r => new FilterOption<RiskLevel?>(Loc.Get($"Risk.{r}"), r)
                {
                    Count = _all.Count(t => t.Risk == r),
                })));

        StateFilter = new FilterGroup<StateFilterKind>(
        [
            new FilterOption<StateFilterKind>(Loc.Get("Filter.AllStates"), StateFilterKind.All),
            new FilterOption<StateFilterKind>(Loc.Get("Filter.StatePending"), StateFilterKind.Pending),
            new FilterOption<StateFilterKind>(Loc.Get("Filter.StateApplied"), StateFilterKind.Applied),
        ]);

        _customProfile = new FilterOption<Profile?>(Loc.Get("Profile.Custom"), null);
        ProfileFilter = new FilterGroup<Profile?>(
            catalog.Profiles
                .Select(p => new FilterOption<Profile?>(p.Name.Resolve(CultureInfo.CurrentUICulture), p))
                .Append(_customProfile));
        ProfileFilter.SelectSilently(_customProfile);

        CategoryFilter.Changed += (_, _) => RefreshVisible();
        RiskFilter.Changed += (_, _) => RefreshVisible();
        StateFilter.Changed += (_, _) => RefreshVisible();
        ProfileFilter.Changed += (_, _) => OnProfileChanged();

        VisibleTweaks = [];
        Results = [];
        Banners = [];
        _statusText = Loc.Get("Status.Loading");

        RefreshVisible();
        UpdateSelectionSummary();
        BuildBanners();
    }

    // ---------- Estado exposto à tela ----------

    public FilterGroup<TweakCategory?> CategoryFilter { get; }

    public FilterGroup<RiskLevel?> RiskFilter { get; }

    public FilterGroup<StateFilterKind> StateFilter { get; }

    public FilterGroup<Profile?> ProfileFilter { get; }

    public ObservableCollection<TweakItemViewModel> VisibleTweaks { get; }

    public ObservableCollection<ResultItemViewModel> Results { get; }

    public ObservableCollection<BannerItem> Banners { get; }

    /// <summary>Todos os tweaks do catálogo, sem filtros (a lista visível é <see cref="VisibleTweaks"/>).</summary>
    internal IReadOnlyList<TweakItemViewModel> AllTweaks => _all;

    public int SelectedCount => _all.Count(t => t.IsSelected);

    public string RunButtonText => Loc.Format(IsDryRun ? "Run.Simulate" : "Run.Apply", SelectedCount);

    public string ListCountText => Loc.Format("List.Count", VisibleTweaks.Count, _all.Count);

    public bool HasNoVisibleTweaks => VisibleTweaks.Count == 0;

    public bool HasNoResults => Results.Count == 0;

    public bool IsDetailsTab
    {
        get => !IsResultsTab;
        set => IsResultsTab = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailsTab))]
    private bool _isResultsTab;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunButtonText))]
    private bool _isDryRun = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    /// <summary>Há uma execução em andamento que pode ser cancelada (a detecção não pode).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isCancellable;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMax = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
    private TweakItemViewModel? _selectedTweak;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string? _restorePointText;

    public bool SelectAllVisible
    {
        get
        {
            var selectable = VisibleTweaks.Where(t => t.CanSelect).ToList();
            return selectable.Count > 0 && selectable.All(t => t.IsSelected);
        }

        set
        {
            foreach (var item in VisibleTweaks.Where(t => t.CanSelect))
            {
                item.IsSelected = value;
            }
        }
    }

    // ---------- Inicialização e detecção ----------

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            using (BeginBusy())
            {
                StatusText = Loc.Get("Status.Detecting");
                await DetectStatesAsync(ct, keepKnownOnFailure: false).ConfigureAwait(true);
                await CheckProtectionAsync(ct).ConfigureAwait(true);
            }
        }
        finally
        {
            BuildBanners();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => DetectWithBusyAsync();

    private bool CanRefresh() => !IsBusy;

    private async Task DetectWithBusyAsync()
    {
        using (BeginBusy())
        {
            StatusText = Loc.Get("Status.Detecting");
            await DetectStatesAsync(CancellationToken.None, keepKnownOnFailure: true).ConfigureAwait(true);
        }
    }

    /// <param name="keepKnownOnFailure">Se a detecção falhar, mantém os estados já conhecidos em vez de marcar tudo como desconhecido.</param>
    private async Task DetectStatesAsync(CancellationToken ct, bool keepKnownOnFailure)
    {
        IReadOnlyDictionary<string, TweakState>? states = null;
        try
        {
            states = await _detector.DetectAsync(_all.Select(t => t.Tweak), ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ShowUnexpectedAsync(ex).ConfigureAwait(true);
            if (keepKnownOnFailure && _all.All(t => t.State is not null))
            {
                return;
            }
        }

        // Em lote: tirar da seleção o que já foi aplicado não conta como mudança manual (não troca o perfil),
        // e as contagens são refeitas uma vez só, no final.
        _bulkStateUpdate = _suppressSelection = true;
        try
        {
            foreach (var item in _all)
            {
                item.State = states is not null && states.TryGetValue(item.Id, out var state)
                    ? state
                    : new TweakState(TweakAppliedState.Unknown, new CompatibilityResult(CompatibilityStatus.Compatible));
            }
        }
        finally
        {
            _bulkStateUpdate = _suppressSelection = false;
        }

        UpdateCounts();
        UpdateSelectionSummary();
        RefreshVisible();
    }

    private async Task CheckProtectionAsync(CancellationToken ct)
    {
        _protectionChecked = false;
        if (!_windows.IsElevated)
        {
            return; // sem administrador não dá para saber; o aviso de elevação já cobre o caso
        }

        try
        {
            _protectionEnabled = await _restorePoints.IsProtectionEnabledAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _protectionEnabled = null;
        }

        _protectionChecked = true;
    }

    // ---------- Filtros, busca e seleção ----------

    partial void OnSearchTextChanged(string value) => RefreshVisible();

    partial void OnSelectedTweakChanged(TweakItemViewModel? value)
    {
        if (value is not null)
        {
            IsResultsTab = false;
        }
    }

    private void RefreshVisible()
    {
        var category = CategoryFilter.Selected.Value;
        var risk = RiskFilter.Selected.Value;
        var state = StateFilter.Selected.Value;
        var term = SearchText.Trim();
        var compare = CultureInfo.CurrentCulture.CompareInfo;

        bool Contains(string text) =>
            compare.IndexOf(text, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

        var visible = _all.Where(t =>
            (category is null || t.Category == category)
            && (risk is null || t.Risk == risk)
            && state switch
            {
                StateFilterKind.Pending => t.IsPending,
                StateFilterKind.Applied => t.IsApplied,
                _ => true,
            }
            && (term.Length == 0 || Contains(t.Name) || Contains(t.Description) || Contains(t.Id) || Contains(t.CategoryText)))
            .ToList();

        if (visible.SequenceEqual(VisibleTweaks))
        {
            return;
        }

        VisibleTweaks.Clear();
        foreach (var item in visible)
        {
            VisibleTweaks.Add(item);
        }

        if (SelectedTweak is not null && !visible.Contains(SelectedTweak))
        {
            SelectedTweak = null;
        }

        OnPropertyChanged(nameof(ListCountText));
        OnPropertyChanged(nameof(HasNoVisibleTweaks));
        OnPropertyChanged(nameof(SelectAllVisible));
    }

    private void UpdateCounts()
    {
        foreach (var option in StateFilter.Options)
        {
            option.Count = option.Value switch
            {
                StateFilterKind.Pending => _all.Count(t => t.IsPending),
                StateFilterKind.Applied => _all.Count(t => t.IsApplied),
                _ => _all.Count,
            };
        }

        RevertSelectedCommand.NotifyCanExecuteChanged();
    }

    private void OnItemSelectionChanged(object? sender, EventArgs e)
    {
        UpdateSelectionSummary();

        // Marcar/desmarcar à mão torna a seleção "Personalizada".
        if (!_suppressSelection && ProfileFilter.Selected.Value is not null)
        {
            ProfileFilter.SelectSilently(_customProfile);
        }
    }

    private void UpdateSelectionSummary()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(RunButtonText));
        OnPropertyChanged(nameof(SelectAllVisible));
        RunCommand.NotifyCanExecuteChanged();
    }

    private void OnProfileChanged()
    {
        var profile = ProfileFilter.Selected.Value;
        if (profile is null)
        {
            return; // "Personalizado": mantém a seleção atual
        }

        var members = ProfileResolver.Resolve(_catalog, profile, _windows.Build).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

        _suppressSelection = true;
        try
        {
            foreach (var item in _all)
            {
                item.IsSelected = members.Contains(item.Id) && item.CanSelect;
            }
        }
        finally
        {
            _suppressSelection = false;
        }

        UpdateSelectionSummary();
    }

    // ---------- Executar ----------

    private bool CanRun() => !IsBusy && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var selected = _all.Where(t => t.IsSelected && t.CanSelect).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var dryRun = IsDryRun;
        if (!dryRun && !await EnsureElevatedAsync().ConfigureAwait(true))
        {
            return;
        }

        var confirmed = dryRun ? new HashSet<string>(StringComparer.Ordinal) : await ConfirmRisksAsync(selected).ConfigureAwait(true);
        if (confirmed is null)
        {
            return;
        }

        var tweaks = selected.Select(t => t.Tweak).ToList();
        var options = new ExecutionOptions { DryRun = dryRun, ConfirmedTweakIds = confirmed };

        var run = await RunWithProgressAsync(async (progress, ct) =>
        {
            var result = await _engine.ApplyAllAsync(tweaks, options, progress, ct).ConfigureAwait(true);
            if (result.Aborted && !dryRun)
            {
                var detail = result.RestorePoint?.Detail ?? result.RestorePoint?.Outcome.ToString() ?? string.Empty;
                var proceed = await _dialogs.ConfirmAsync(
                    Loc.Get("Dialog.NoRestoreTitle"),
                    Loc.Format("Dialog.NoRestoreMessage", detail),
                    Loc.Get("Dialog.NoRestoreContinue"),
                    isDanger: true).ConfigureAwait(true);
                if (proceed)
                {
                    result = await _engine.ApplyAllAsync(tweaks, options with { ProceedWithoutRestorePoint = true }, progress, ct).ConfigureAwait(true);
                }
            }

            return result;
        }, exclusive: !dryRun).ConfigureAwait(true);

        await FinishAsync(run, dryRun).ConfigureAwait(true);
    }

    /// <summary>Pede a confirmação dos tweaks Moderados e Avançados. Retorna os ids confirmados, ou null se o usuário cancelou.</summary>
    private async Task<HashSet<string>?> ConfirmRisksAsync(List<TweakItemViewModel> selected)
    {
        var confirmed = new HashSet<string>(StringComparer.Ordinal);
        var risky = selected.Where(t => t.Risk >= RiskLevel.Moderate).ToList();
        if (risky.Count == 0)
        {
            return confirmed;
        }

        var items = risky.Select(t => new RiskConfirmationItem(t.Name, t.Risk, t.RiskNote ?? string.Empty)).ToList();
        if (!await _dialogs.ConfirmRisksAsync(items).ConfigureAwait(true))
        {
            return null;
        }

        confirmed.UnionWith(risky.Select(t => t.Id));
        return confirmed;
    }

    private bool CanRevertSelected() => !IsBusy && SelectedTweak?.CanRevert == true;

    [RelayCommand(CanExecute = nameof(CanRevertSelected))]
    private async Task RevertSelectedAsync()
    {
        var item = SelectedTweak;
        if (item is null)
        {
            return;
        }

        var dryRun = IsDryRun;
        if (!dryRun)
        {
            if (!await EnsureElevatedAsync().ConfigureAwait(true))
            {
                return;
            }

            var ok = await _dialogs.ConfirmAsync(
                Loc.Get("Dialog.RevertOneTitle"),
                Loc.Format("Dialog.RevertOneMessage", item.Name),
                Loc.Get("Dialog.RevertOneConfirm")).ConfigureAwait(true);
            if (!ok)
            {
                return;
            }
        }

        var options = new ExecutionOptions { DryRun = dryRun };
        var run = await RunWithProgressAsync(async (_, ct) =>
        {
            var result = await _engine.RevertAsync(item.Tweak, options, ct).ConfigureAwait(true);
            return new BatchResult([result], null, null, false);
        }, exclusive: !dryRun).ConfigureAwait(true);

        await FinishAsync(run, dryRun).ConfigureAwait(true);
    }

    private bool CanRevertAll() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRevertAll))]
    private async Task RevertAllAsync()
    {
        var dryRun = IsDryRun;

        IReadOnlyList<BackupRecord> active;
        using (BeginBusy())
        {
            try
            {
                active = await _backups.GetAllActiveNewestFirstAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await ShowUnexpectedAsync(ex).ConfigureAwait(true);
                return;
            }
        }

        if (active.Count == 0)
        {
            await _dialogs.ShowMessageAsync(Loc.Get("Dialog.RevertAllTitle"), Loc.Get("Dialog.RevertAllNone")).ConfigureAwait(true);
            return;
        }

        if (!dryRun)
        {
            if (!await EnsureElevatedAsync().ConfigureAwait(true))
            {
                return;
            }

            var ok = await _dialogs.ConfirmAsync(
                Loc.Get("Dialog.RevertAllTitle"),
                Loc.Format("Dialog.RevertAllMessage", active.Count),
                Loc.Get("Dialog.RevertAllConfirm"),
                isDanger: true).ConfigureAwait(true);
            if (!ok)
            {
                return;
            }
        }

        // Desfaz exatamente os registros que a confirmação mostrou.
        var options = new ExecutionOptions { DryRun = dryRun };
        var run = await RunWithProgressAsync((progress, ct) => _engine.RevertAllAsync(active, options, progress, ct), exclusive: !dryRun).ConfigureAwait(true);
        await FinishAsync(run, dryRun).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(IsCancellable))]
    private void Cancel()
    {
        StatusText = Loc.Get("Status.Cancelling");
        _cts?.Cancel();
        IsCancellable = false;
    }

    private async Task<bool> EnsureElevatedAsync()
    {
        if (_windows.IsElevated)
        {
            return true;
        }

        var reopen = await _dialogs.ConfirmAsync(
            Loc.Get("Dialog.NeedAdminTitle"),
            Loc.Get("Dialog.NeedAdminMessage"),
            Loc.Get("Dialog.NeedAdminConfirm")).ConfigureAwait(true);
        if (reopen)
        {
            await RelaunchAsAdminAsync().ConfigureAwait(true);
        }

        return false;
    }

    /// <summary>Reabre o app com permissão de administrador, já nesta seção. Se o usuário recusar o pedido do Windows, nada muda.</summary>
    [RelayCommand]
    private async Task RelaunchAsAdminAsync()
    {
        if (_windows.IsElevated)
        {
            return;
        }

        var message = _relauncher.RelaunchElevated() switch
        {
            RelaunchOutcome.Started => null,
            RelaunchOutcome.Declined => "Dialog.NeedAdminDeclined",
            _ => "Dialog.NeedAdminUnavailable",
        };
        if (message is not null)
        {
            await _dialogs.ShowMessageAsync(Loc.Get("Dialog.NeedAdminTitle"), Loc.Get(message)).ConfigureAwait(true);
        }
    }

    /// <summary>Resultado de <see cref="RunWithProgressAsync"/>. Started é false quando nada rodou (outra execução em andamento).</summary>
    private readonly record struct RunOutcome(bool Started, BatchResult? Batch);

    private async Task<RunOutcome> RunWithProgressAsync(
        Func<IProgress<BatchProgress>, CancellationToken, Task<BatchResult>> work,
        bool exclusive)
    {
        if (_cts is not null)
        {
            return new RunOutcome(false, null); // já há uma execução nesta janela
        }

        // Só uma janela por vez pode aplicar ou desfazer: dois fluxos gravando backups ao mesmo tempo se embaralhariam.
        IDisposable? lease = null;
        if (exclusive)
        {
            lease = _gate.TryEnter();
            if (lease is null)
            {
                await _dialogs.ShowMessageAsync(Loc.Get("Dialog.AnotherRunTitle"), Loc.Get("Dialog.AnotherRunMessage")).ConfigureAwait(true);
                return new RunOutcome(false, null);
            }
        }

        using var busy = BeginBusy();
        ProgressValue = 0;
        ProgressMax = 1;
        _cts = new CancellationTokenSource();
        IsCancellable = true;
        try
        {
            return new RunOutcome(true, await work(new InlineProgress(OnProgress), _cts.Token).ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
            return new RunOutcome(true, null);
        }
        catch (Exception ex)
        {
            await ShowUnexpectedAsync(ex).ConfigureAwait(true);
            return new RunOutcome(true, null);
        }
        finally
        {
            IsCancellable = false;
            _cts.Dispose();
            _cts = null;
            lease?.Dispose();
        }
    }

    /// <summary>Deixa a tela ocupada até o retorno ser descartado. Pode ser aninhado.</summary>
    private IDisposable BeginBusy()
    {
        _busyDepth++;
        IsBusy = true;
        return new BusyScope(this);
    }

    private sealed class BusyScope(LimpezaViewModel owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (--owner._busyDepth == 0)
            {
                owner.IsBusy = false;
                owner.StatusText = string.Empty;
            }
        }
    }

    private void OnProgress(BatchProgress progress)
    {
        ProgressMax = Math.Max(1, progress.Total);
        ProgressValue = progress.Completed;

        if (_cts?.IsCancellationRequested == true)
        {
            return; // mantém o "Cancelando…" até a execução parar
        }

        var position = Math.Min(progress.Total, progress.Completed + (progress.LastResult is null ? 1 : 0));
        StatusText = Loc.Format("Status.Running", NameOf(progress.CurrentTweakId), position, progress.Total);
    }

    private async Task FinishAsync(RunOutcome run, bool dryRun)
    {
        if (!run.Started)
        {
            return;
        }

        if (run.Batch is not null)
        {
            ShowResults(run.Batch, dryRun);
        }

        // Mesmo cancelada ou com erro, uma execução real pode ter alterado algo: a lista precisa mostrar isso.
        if (!dryRun)
        {
            await DetectWithBusyAsync().ConfigureAwait(true);
        }

        BuildBanners();
    }

    private void ShowResults(BatchResult batch, bool dryRun)
    {
        Results.Clear();
        foreach (var result in batch.Results)
        {
            Results.Add(new ResultItemViewModel(NameOf(result.TweakId), result));
        }

        var success = batch.Results.Count(r => r.Status == ResultStatus.Success);
        var wouldChange = batch.Results.Count(r => r.Status == ResultStatus.DryRun);
        var skipped = batch.Results.Count(r => r.Status == ResultStatus.Skipped);
        var failed = batch.Results.Count(r => r.Status == ResultStatus.Failed);

        SummaryText = dryRun
            ? Loc.Format("Result.SummaryDryRun", wouldChange, skipped, failed)
            : Loc.Format("Result.Summary", success, skipped, failed);

        RestorePointText = batch.RestorePoint switch
        {
            null => null,
            { Outcome: RestorePointOutcome.Created } => Loc.Get("Result.RestorePointCreated"),
            { Outcome: RestorePointOutcome.Throttled } => Loc.Get("Result.RestorePointThrottled"),
            var other => Loc.Format("Result.RestorePointSkipped", other.Detail ?? other.Outcome.ToString()),
        };

        if (!dryRun && batch.Results.Any(r => r.Status == ResultStatus.Success && r.RequiresReboot))
        {
            _rebootPending = true;
        }

        OnPropertyChanged(nameof(HasNoResults));
        IsResultsTab = true;
    }

    // ---------- Avisos ----------

    private void BuildBanners()
    {
        Banners.Clear();

        if (_catalog.Tweaks.Count == 0)
        {
            Banners.Add(new BannerItem(Loc.Get("Banner.CatalogMissing"), Tone.Danger));
        }
        else if (_catalogIssueCount > 0)
        {
            Banners.Add(new BannerItem(Loc.Format("Banner.CatalogIssues", _catalogIssueCount), Tone.Warning));
        }

        if (!_windows.IsElevated)
        {
            Banners.Add(new BannerItem(Loc.Get("Banner.NotElevated"), Tone.Warning, OffersElevation: true));
        }
        else if (_protectionChecked && _protectionEnabled == false)
        {
            Banners.Add(new BannerItem(Loc.Get("Banner.NoProtection"), Tone.Warning));
        }
        else if (_protectionChecked && _protectionEnabled is null)
        {
            Banners.Add(new BannerItem(Loc.Get("Banner.ProtectionUnknown"), Tone.Neutral));
        }

        if (_rebootPending)
        {
            Banners.Add(new BannerItem(Loc.Get("Banner.Reboot"), Tone.Neutral));
        }
    }

    private string NameOf(string? tweakId) =>
        tweakId is not null && _byId.TryGetValue(tweakId, out var item) ? item.Name : tweakId ?? string.Empty;

    private async Task ShowUnexpectedAsync(Exception ex) =>
        await _dialogs.ShowMessageAsync(
            Loc.Get("Dialog.UnexpectedTitle"),
            Loc.Format("Dialog.UnexpectedMessage", ex.Message, _logDirectory)).ConfigureAwait(true);

    /// <summary>
    /// Entrega o progresso na thread da interface, preservando a ordem. Sem contexto de sincronização
    /// (testes) o callback roda na hora, ao contrário de Progress&lt;T&gt;, que sempre despacha de forma assíncrona.
    /// </summary>
    private sealed class InlineProgress(Action<BatchProgress> handler) : IProgress<BatchProgress>
    {
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;

        public void Report(BatchProgress value)
        {
            if (_context is null || SynchronizationContext.Current == _context)
            {
                handler(value);
            }
            else
            {
                _context.Post(_ => handler(value), null);
            }
        }
    }
}
