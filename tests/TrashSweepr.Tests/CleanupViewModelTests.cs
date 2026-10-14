using TrashSweepr.Cleanup.Services;
using TrashSweepr.Cleanup.ViewModels;
using TrashSweepr.Cleanup.Core.Abstractions;
using TrashSweepr.Cleanup.Core.Catalog;
using TrashSweepr.Cleanup.Core.Execution;
using TrashSweepr.Cleanup.Core.Models;
using TrashSweepr.Tests.Helpers;
using static TrashSweepr.Tests.Helpers.CatalogJson;
using static TrashSweepr.Tests.Helpers.TestTweaks;

namespace TrashSweepr.Tests;

public sealed class CleanupViewModelTests : IDisposable
{
    private readonly FakeSystem _sys = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeRelauncher _relauncher = new();
    private readonly FakeGate _gate = new();

    public CleanupViewModelTests()
    {
        _sys.Services.Services["FooSvc"] = new ServiceState(true, ServiceStartType.Automatic, true);
        _sys.Appx.Install("Contoso.B");
    }

    public void Dispose() => _sys.Dispose();

    private static CatalogLoadResult BuildCatalog() => TweakCatalogLoader.LoadFromSources(
    [
        ("tweaks.json", TweakFile(
            Tweak(id: "test.safe-a", name: "Alfa privado", description: "Desliga a coleta de dados", category: "privacy",
                  apply: Registry(path: Key, name: "A", valueJson: "0"), revert: Registry(path: Key, name: "A", valueJson: "1")),
            Tweak(id: "test.safe-b", name: "Remover Contoso", description: "Tira o app", category: "apps",
                  apply: Appx("Contoso.B", "remove"), revert: Appx("Contoso.B", "reinstall"), extra: ", \"requiresReboot\": true"),
            Tweak(id: "test.moderate", name: "Serviço Foo", description: "Para o serviço", category: "services", risk: "moderate",
                  riskNote: "Pode afetar relatórios.", apply: Service("FooSvc", "disabled", stop: true), revert: Service("FooSvc", "automatic")),
            Tweak(id: "test.optin", name: "Opcional", description: "Só se pedir", category: "interface",
                  apply: Registry(path: Key, name: "O", valueJson: "0"), revert: Registry(path: Key, name: "O", valueJson: "1"), extra: ", \"optIn\": true"),
            Tweak(id: "test.future", name: "Recurso novo", description: "Precisa de Windows novo", category: "interface",
                  apply: Registry(path: Key, name: "F", valueJson: "0"), revert: Registry(path: Key, name: "F", valueJson: "1"), extra: ", \"minBuild\": 26100"))),
        ("profiles.json", ProfileFile(Profile("light", "safe", "Leve"), Profile("moderate", "moderate", "Moderado"))),
    ]);

    private CleanupViewModel CreateViewModel(CatalogLoadResult? catalog = null)
    {
        catalog ??= BuildCatalog();
        Assert.False(catalog.HasErrors, string.Join(Environment.NewLine, catalog.Issues));
        return new CleanupViewModel(
            catalog.Catalog, catalog.Issues.Count, _sys.Engine, _sys.Detector, _sys.Windows, _sys.RestorePoints, _sys.Backups, _dialogs, _relauncher, _gate, "logs");
    }

    private static TweakItemViewModel Item(CleanupViewModel vm, string id) => vm.AllTweaks.First(t => t.Id == id);

    private async Task<CleanupViewModel> InitializedAsync(CatalogLoadResult? catalog = null)
    {
        var vm = CreateViewModel(catalog);
        await vm.InitializeAsync();
        return vm;
    }

    // ---------- Inicialização ----------

    [Fact]
    public async Task Initialize_detects_the_state_and_blocks_selection_of_applied_or_incompatible_tweaks()
    {
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "A", RegistryValueType.Dword, 0);

        var vm = await InitializedAsync();

        var applied = Item(vm, "test.safe-a");
        Assert.Equal(TweakAppliedState.Applied, applied.Applied);
        Assert.False(applied.CanSelect);
        Assert.Equal("Aplicado", applied.StateText);

        var future = Item(vm, "test.future");
        Assert.False(future.IsCompatible);
        Assert.False(future.CanSelect);
        Assert.Contains("26100", future.CompatibilityMessage, StringComparison.Ordinal);
        Assert.Contains("22631", future.CompatibilityMessage, StringComparison.Ordinal);

        Assert.True(Item(vm, "test.safe-b").CanSelect);
        Assert.Equal("Não aplicado", Item(vm, "test.safe-b").StateText);
        Assert.False(vm.IsBusy);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.Empty(vm.Banners);
    }

    [Fact]
    public async Task Items_expose_translated_text_and_what_the_tweak_does()
    {
        var vm = await InitializedAsync();

        var service = Item(vm, "test.moderate");
        Assert.Equal("Serviços", service.CategoryText);
        Assert.Equal("Moderado", service.RiskText);
        Assert.Equal(Tone.Warning, service.RiskTone);
        Assert.Equal("Pode afetar relatórios.", service.RiskNote);
        Assert.Contains("Serviço FooSvc: inicialização Desativada, e o serviço é parado", service.ApplyDescriptions);
        Assert.Contains("Serviço FooSvc: inicialização Automática", service.RevertDescriptions);

        Assert.Contains($@"Registro: define HKCU\{Key}\A = 0 (DWORD)", Item(vm, "test.safe-a").ApplyDescriptions);
        Assert.Contains("App Contoso.B: remove, somente para o seu usuário", Item(vm, "test.safe-b").ApplyDescriptions);
    }

    // ---------- Perfis e seleção ----------

    [Fact]
    public async Task Light_profile_selects_only_safe_non_optin_compatible_tweaks()
    {
        var vm = await InitializedAsync();

        SelectProfile(vm, "Leve");

        Assert.Equal(["test.safe-a", "test.safe-b"], Selected(vm));
        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal("Simular 2 selecionado(s)", vm.RunButtonText);
    }

    [Fact]
    public async Task Moderate_profile_adds_moderate_tweaks_but_never_optin_ones()
    {
        var vm = await InitializedAsync();

        SelectProfile(vm, "Moderado");

        Assert.Equal(["test.moderate", "test.safe-a", "test.safe-b"], Selected(vm));
        Assert.DoesNotContain("test.optin", Selected(vm));
    }

    [Fact]
    public async Task Profile_skips_what_is_already_applied()
    {
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "A", RegistryValueType.Dword, 0);
        var vm = await InitializedAsync();

        SelectProfile(vm, "Leve");

        Assert.Equal(["test.safe-b"], Selected(vm));
    }

    [Fact]
    public async Task Marking_an_item_by_hand_switches_to_the_custom_profile()
    {
        var vm = await InitializedAsync();
        SelectProfile(vm, "Leve");
        Assert.Equal("Leve", vm.ProfileFilter.Selected.Label);

        Item(vm, "test.optin").IsSelected = true;

        Assert.Equal("Personalizado", vm.ProfileFilter.Selected.Label);
        Assert.Equal(["test.optin", "test.safe-a", "test.safe-b"], Selected(vm));
    }

    [Fact]
    public async Task Selecting_a_profile_replaces_the_previous_selection()
    {
        var vm = await InitializedAsync();
        Item(vm, "test.optin").IsSelected = true;

        SelectProfile(vm, "Leve");

        Assert.False(Item(vm, "test.optin").IsSelected);
    }

    [Fact]
    public async Task Select_all_visible_only_marks_selectable_tweaks()
    {
        var vm = await InitializedAsync();

        vm.SelectAllVisible = true;

        Assert.Equal(["test.moderate", "test.optin", "test.safe-a", "test.safe-b"], Selected(vm));
        Assert.True(vm.SelectAllVisible);
        Assert.False(Item(vm, "test.future").IsSelected);

        vm.SelectAllVisible = false;
        Assert.Empty(Selected(vm));
    }

    // ---------- Filtros e busca ----------

    [Fact]
    public async Task Category_filter_limits_the_list_and_shows_counts()
    {
        var vm = await InitializedAsync();

        var apps = vm.CategoryFilter.Options.Single(o => o.Value == TweakCategory.Apps);
        Assert.Equal(1, apps.Count);
        Assert.Equal(5, vm.CategoryFilter.Options[0].Count);

        apps.IsSelected = true;

        Assert.Equal(["test.safe-b"], vm.VisibleTweaks.Select(t => t.Id));
        Assert.Equal("1 de 5 tweaks", vm.ListCountText);
        Assert.True(apps.IsSelected);
        Assert.False(vm.CategoryFilter.Options[0].IsSelected);
    }

    [Fact]
    public async Task Risk_and_state_filters_combine()
    {
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "A", RegistryValueType.Dword, 0);
        var vm = await InitializedAsync();

        vm.RiskFilter.Options.Single(o => o.Value == RiskLevel.Safe).IsSelected = true;
        Assert.Equal(["test.future", "test.optin", "test.safe-a", "test.safe-b"], vm.VisibleTweaks.Select(t => t.Id).Order());

        vm.StateFilter.Options.Single(o => o.Value == StateFilterKind.Applied).IsSelected = true;
        Assert.Equal(["test.safe-a"], vm.VisibleTweaks.Select(t => t.Id));

        vm.StateFilter.Options.Single(o => o.Value == StateFilterKind.Pending).IsSelected = true;
        Assert.DoesNotContain("test.safe-a", vm.VisibleTweaks.Select(t => t.Id));
        Assert.Empty(vm.RiskFilter.Options.Where(o => o.Value == RiskLevel.Advanced && o.Count > 0));
    }

    [Theory]
    [InlineData("alfa", "test.safe-a")]
    [InlineData("SERVICO", "test.moderate")]
    [InlineData("windows NOVO", "test.future")]
    [InlineData("coleta de dados", "test.safe-a")]
    [InlineData("test.optin", "test.optin")]
    public async Task Search_ignores_case_and_accents(string term, string expectedId)
    {
        var vm = await InitializedAsync();

        vm.SearchText = term;

        Assert.Contains(expectedId, vm.VisibleTweaks.Select(t => t.Id));
    }

    [Fact]
    public async Task Search_with_no_match_shows_the_empty_state_and_clearing_restores_the_list()
    {
        var vm = await InitializedAsync();

        vm.SearchText = "zzzzzz";
        Assert.True(vm.HasNoVisibleTweaks);
        Assert.Empty(vm.VisibleTweaks);

        vm.SearchText = string.Empty;
        Assert.Equal(5, vm.VisibleTweaks.Count);
        Assert.False(vm.HasNoVisibleTweaks);
    }

    [Fact]
    public async Task Filtering_out_the_selected_tweak_clears_the_details_but_keeps_checkbox_selection()
    {
        var vm = await InitializedAsync();
        vm.SelectedTweak = Item(vm, "test.safe-a");
        Item(vm, "test.safe-a").IsSelected = true;

        vm.SearchText = "serviço";

        Assert.Null(vm.SelectedTweak);
        Assert.True(Item(vm, "test.safe-a").IsSelected);
    }

    // ---------- Simulação ----------

    [Fact]
    public async Task Dry_run_changes_nothing_asks_nothing_and_shows_the_planned_commands()
    {
        var vm = await InitializedAsync();
        Assert.True(vm.IsDryRun);
        SelectProfile(vm, "Moderado");

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Empty(_sys.Mutations);
        Assert.Empty(_dialogs.RiskPrompts);
        Assert.Empty(_dialogs.ConfirmPrompts);
        Assert.Equal(0, _sys.BackupFileCount());
        Assert.Equal(3, vm.Results.Count);
        Assert.All(vm.Results, r => Assert.Equal("Simulado", r.StatusText));
        Assert.All(vm.Results, r => Assert.True(r.HasCommands));
        Assert.Contains("Simulação: 3 tweak(s) seriam alterados", vm.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Nada foi modificado", vm.SummaryText, StringComparison.Ordinal);
        Assert.True(vm.IsResultsTab);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Dry_run_results_expose_the_exact_commands_for_the_details_view()
    {
        var vm = await InitializedAsync();
        Item(vm, "test.safe-a").IsSelected = true;

        await vm.RunCommand.ExecuteAsync(null);

        var result = Assert.Single(vm.Results);
        Assert.Equal("Alfa privado", result.TweakName);
        Assert.Equal($@"registry> SET HKCU\{Key}\A = 0 (REG_DWORD)", result.CommandsText);
        Assert.False(result.ShowCommands);
        result.ToggleCommandsCommand.Execute(null);
        Assert.True(result.ShowCommands);
        Assert.Equal("Ocultar comandos", result.ToggleCommandsText);
    }

    // ---------- Aplicar de verdade ----------

    [Fact]
    public async Task Applying_without_administrator_rights_offers_to_reopen_and_changes_nothing_if_declined()
    {
        _sys.Windows.IsElevated = false;
        var vm = await InitializedAsync();
        vm.IsDryRun = false;
        Item(vm, "test.safe-a").IsSelected = true;
        _dialogs.ApproveConfirm = false;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Empty(_sys.Mutations);
        Assert.Equal(0, _relauncher.Calls);
        var prompt = Assert.Single(_dialogs.ConfirmPrompts);
        Assert.Equal("Modo administrador necessário", prompt.Title);
        Assert.Equal("Reabrir como administrador", prompt.Confirm);
        Assert.Contains(vm.Banners, b => b.Text.Contains("Sem o modo administrador", StringComparison.Ordinal) && b.OffersElevation);
        Assert.Equal(0, _gate.Entered);
    }

    [Fact]
    public async Task Accepting_the_prompt_reopens_the_app_as_administrator_instead_of_applying()
    {
        _sys.Windows.IsElevated = false;
        var vm = await InitializedAsync();
        vm.IsDryRun = false;
        Item(vm, "test.safe-a").IsSelected = true;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(1, _relauncher.Calls);
        Assert.Empty(_sys.Mutations);
        Assert.Empty(_dialogs.Messages);
    }

    [Fact]
    public async Task When_the_windows_prompt_is_refused_the_user_is_told_and_nothing_changes()
    {
        _sys.Windows.IsElevated = false;
        _relauncher.Outcome = RelaunchOutcome.Declined;
        var vm = await InitializedAsync();
        vm.IsDryRun = false;
        Item(vm, "test.safe-a").IsSelected = true;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(1, _relauncher.Calls);
        Assert.Contains("Nada foi alterado", Assert.Single(_dialogs.Messages).Message, StringComparison.Ordinal);
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task When_the_app_cannot_be_reopened_the_message_does_not_blame_the_user()
    {
        _sys.Windows.IsElevated = false;
        _relauncher.Outcome = RelaunchOutcome.Unavailable;
        var vm = await InitializedAsync();

        await vm.RelaunchAsAdminCommand.ExecuteAsync(null);

        var message = Assert.Single(_dialogs.Messages).Message;
        Assert.Contains("Não foi possível reabrir", message, StringComparison.Ordinal);
        Assert.DoesNotContain("recusado", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detecting_after_a_real_run_keeps_the_chosen_profile()
    {
        var vm = await InitializedAsync();
        vm.IsDryRun = false;
        SelectProfile(vm, "Leve");

        await vm.RunCommand.ExecuteAsync(null);

        // O que foi aplicado sai da seleção, mas isso não é uma mudança manual: o perfil continua "Leve".
        Assert.True(Item(vm, "test.safe-a").IsApplied);
        Assert.Empty(Selected(vm));
        Assert.Equal("Leve", vm.ProfileFilter.Selected.Label);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task The_banner_button_reopens_as_administrator_and_does_nothing_when_already_elevated()
    {
        _sys.Windows.IsElevated = false;
        var vm = await InitializedAsync();

        await vm.RelaunchAsAdminCommand.ExecuteAsync(null);
        Assert.Equal(1, _relauncher.Calls);

        _sys.Windows.IsElevated = true;
        await vm.RelaunchAsAdminCommand.ExecuteAsync(null);
        Assert.Equal(1, _relauncher.Calls);
    }

    [Fact]
    public async Task Only_one_window_can_apply_at_a_time()
    {
        var vm = await InitializedAsync();
        vm.IsDryRun = false;
        Item(vm, "test.safe-a").IsSelected = true;
        _gate.Busy = true;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Empty(_sys.Mutations);
        Assert.Equal("Outra janela está em execução", Assert.Single(_dialogs.Messages).Title);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task The_run_gate_is_taken_for_real_runs_released_afterwards_and_not_needed_for_simulations()
    {
        var vm = await InitializedAsync();
        Item(vm, "test.safe-a").IsSelected = true;

        await vm.RunCommand.ExecuteAsync(null); // simulação
        Assert.Equal(0, _gate.Entered);

        vm.IsDryRun = false;
        Item(vm, "test.safe-a").IsSelected = true;
        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(1, _gate.Entered);
        Assert.Equal(0, _gate.Active);
    }

    [Fact]
    public async Task The_run_gate_is_released_even_when_the_run_fails()
    {
        _sys.Registry.FailOnSet = _ => true;
        var vm = await InitializedAsync();
        vm.IsDryRun = false;
        Item(vm, "test.safe-a").IsSelected = true;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("Falhou", Assert.Single(vm.Results).StatusText);
        Assert.Equal(0, _gate.Active);
    }
    [Fact]
    public async Task Dry_run_is_still_available_without_administrator_rights()
    {
        _sys.Windows.IsElevated = false;
        var vm = await InitializedAsync();
        Item(vm, "test.safe-a").IsSelected = true;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("Simulado", Assert.Single(vm.Results).StatusText);
        Assert.Empty(_dialogs.Messages);
    }

    [Fact]
    public async Task Applying_safe_tweaks_needs_no_risk_confirmation_and_refreshes_the_states()
    {
        var vm = await InitializedAsync();
        vm.IsDryRun = false;
        SelectProfile(vm, "Leve");

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Empty(_dialogs.RiskPrompts);
        Assert.Equal(0, _sys.Registry.Get(RegistryHive.Hkcu, Key, "A"));
        Assert.False(_sys.Appx.Packages["Contoso.B"].InstalledForCurrentUser);
        Assert.All(vm.Results, r => Assert.Equal("Sucesso", r.StatusText));
        Assert.Contains("2 com sucesso, 0 ignorado(s), 0 falha(s).", vm.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Ponto de restauração criado", vm.RestorePointText, StringComparison.Ordinal);

        Assert.True(Item(vm, "test.safe-a").IsApplied);
        Assert.False(Item(vm, "test.safe-a").IsSelected);
        Assert.Equal(0, vm.SelectedCount);
        Assert.Contains(vm.Banners, b => b.Text.Contains("reiniciar", StringComparison.Ordinal));
        Assert.Equal(1, _sys.RestorePoints.CreateCalls);
    }

    [Fact]
    public async Task Moderate_tweaks_show_their_risk_note_and_nothing_runs_if_the_user_declines()
    {
        var vm = await InitializedAsync();
        vm.IsDryRun = false;
        _dialogs.ApproveRisks = false;
        Item(vm, "test.moderate").IsSelected = true;
        Item(vm, "test.safe-a").IsSelected = true;

        await vm.RunCommand.ExecuteAsync(null);

        var prompt = Assert.Single(_dialogs.RiskPrompts);
        var risk = Assert.Single(prompt); // só o moderado é perguntado
        Assert.Equal("Serviço Foo", risk.Name);
        Assert.Equal("Pode afetar relatórios.", risk.Note);
        Assert.Empty(_sys.Mutations);
        Assert.Equal(0, _sys.RestorePoints.CreateCalls);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task Moderate_tweaks_run_after_the_user_confirms()
    {
        var vm = await InitializedAsync();
        vm.IsDryRun = false;
        Item(vm, "test.moderate").IsSelected = true;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(ServiceStartType.Disabled, _sys.Services.Services["FooSvc"].StartType);
        Assert.Equal("Sucesso", Assert.Single(vm.Results).StatusText);
    }

    [Fact]
    public async Task Without_a_restore_point_the_user_decides_whether_to_continue()
    {
        _sys.RestorePoints.ProtectionEnabled = false;
        var vm = await InitializedAsync();
        Assert.Contains(vm.Banners, b => b.Text.Contains("Proteção do Sistema está desativada", StringComparison.Ordinal));
        vm.IsDryRun = false;
        Item(vm, "test.safe-a").IsSelected = true;

        _dialogs.ApproveConfirm = false;
        await vm.RunCommand.ExecuteAsync(null);

        var prompt = Assert.Single(_dialogs.ConfirmPrompts);
        Assert.Equal("Sem ponto de restauração", prompt.Title);
        Assert.True(prompt.IsDanger);
        Assert.Empty(_sys.Mutations);
        Assert.Equal("Ignorado", Assert.Single(vm.Results).StatusText);
        Assert.Contains("ponto de restauração", vm.Results[0].Message, StringComparison.Ordinal);

        _dialogs.ApproveConfirm = true;
        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(0, _sys.Registry.Get(RegistryHive.Hkcu, Key, "A"));
        Assert.Equal("Sucesso", Assert.Single(vm.Results).StatusText);
    }

    [Fact]
    public async Task Failures_are_listed_with_their_error_and_the_rollback_note()
    {
        _sys.Services.FailSetStartType.Add("FooSvc");
        var vm = await InitializedAsync();
        vm.IsDryRun = false;
        Item(vm, "test.moderate").IsSelected = true;

        await vm.RunCommand.ExecuteAsync(null);

        var result = Assert.Single(vm.Results);
        Assert.Equal("Falhou", result.StatusText);
        Assert.Equal(Tone.Danger, result.Tone);
        Assert.Contains("sc.exe", result.Message, StringComparison.Ordinal);
        Assert.Contains("desfeitas automaticamente", result.Message, StringComparison.Ordinal);
        Assert.Contains("0 com sucesso", vm.SummaryText, StringComparison.Ordinal);
        Assert.Contains("1 falha(s)", vm.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_is_disabled_without_a_selection_and_while_busy()
    {
        var vm = await InitializedAsync();
        Assert.False(vm.RunCommand.CanExecute(null));

        Item(vm, "test.safe-a").IsSelected = true;
        Assert.True(vm.RunCommand.CanExecute(null));

        vm.IsBusy = true;
        Assert.False(vm.RunCommand.CanExecute(null));

        // Detecção ocupa a tela mas não tem o que cancelar.
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    // ---------- Desfazer ----------

    [Fact]
    public async Task Undo_one_tweak_asks_for_confirmation_and_restores_the_previous_state()
    {
        var vm = await InitializedAsync();
        vm.IsDryRun = false;
        Item(vm, "test.safe-a").IsSelected = true;
        await vm.RunCommand.ExecuteAsync(null);
        var item = Item(vm, "test.safe-a");
        Assert.True(item.IsApplied);

        vm.SelectedTweak = item;
        Assert.True(vm.RevertSelectedCommand.CanExecute(null));
        await vm.RevertSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Desfazer tweak", Assert.Single(_dialogs.ConfirmPrompts).Title);
        Assert.Null(_sys.Registry.Get(RegistryHive.Hkcu, Key, "A"));
        Assert.Equal(TweakAppliedState.NotApplied, item.Applied);
        Assert.Contains("backup", vm.Results[0].Message, StringComparison.Ordinal);
        Assert.False(vm.RevertSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Undo_is_only_offered_for_tweaks_that_are_applied()
    {
        var vm = await InitializedAsync();

        vm.SelectedTweak = Item(vm, "test.safe-a");

        Assert.False(vm.RevertSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Declining_the_undo_confirmation_changes_nothing()
    {
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "A", RegistryValueType.Dword, 0);
        var vm = await InitializedAsync();
        vm.IsDryRun = false;
        vm.SelectedTweak = Item(vm, "test.safe-a");
        _dialogs.ApproveConfirm = false;

        await vm.RevertSelectedCommand.ExecuteAsync(null);

        Assert.Empty(_sys.Mutations);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task Revert_all_with_nothing_to_revert_tells_the_user()
    {
        var vm = await InitializedAsync();

        await vm.RevertAllCommand.ExecuteAsync(null);

        Assert.Equal("Reverter tudo", Assert.Single(_dialogs.Messages).Title);
        Assert.Empty(_dialogs.ConfirmPrompts);
    }

    [Fact]
    public async Task Revert_all_confirms_with_the_backup_count_and_undoes_everything()
    {
        var vm = await InitializedAsync();
        vm.IsDryRun = false;
        SelectProfile(vm, "Leve");
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(Item(vm, "test.safe-b").IsApplied);

        await vm.RevertAllCommand.ExecuteAsync(null);

        var prompt = Assert.Single(_dialogs.ConfirmPrompts);
        Assert.Contains("2 backup(s)", prompt.Message, StringComparison.Ordinal);
        Assert.True(prompt.IsDanger);
        Assert.Null(_sys.Registry.Get(RegistryHive.Hkcu, Key, "A"));
        Assert.True(_sys.Appx.Packages["Contoso.B"].InstalledForCurrentUser);
        Assert.Equal(TweakAppliedState.NotApplied, Item(vm, "test.safe-b").Applied);
        Assert.Equal(2, vm.Results.Count);
    }

    [Fact]
    public async Task Revert_all_in_simulation_mode_changes_nothing_and_does_not_ask()
    {
        var vm = await InitializedAsync();
        vm.IsDryRun = false;
        Item(vm, "test.safe-a").IsSelected = true;
        await vm.RunCommand.ExecuteAsync(null);
        vm.IsDryRun = true;
        _sys.Mutations.Clear();

        await vm.RevertAllCommand.ExecuteAsync(null);

        Assert.Empty(_sys.Mutations);
        Assert.Empty(_dialogs.ConfirmPrompts);
        Assert.Equal("Simulado", Assert.Single(vm.Results).StatusText);
        Assert.Equal(0, _sys.Registry.Get(RegistryHive.Hkcu, Key, "A"));
    }

    // ---------- Avisos ----------

    [Fact]
    public async Task Catalog_problems_are_surfaced_in_a_banner()
    {
        var broken = TweakCatalogLoader.LoadFromSources(
        [
            ("ok.json", TweakFile(Tweak(id: "test.ok"))),
            ("bad.json", TweakFile(Tweak(id: "Bad Id"), Tweak(id: "test.alsobad", risk: "moderate"))),
        ]);
        var vm = new CleanupViewModel(
            broken.Catalog, broken.Issues.Count, _sys.Engine, _sys.Detector, _sys.Windows, _sys.RestorePoints, _sys.Backups, _dialogs, _relauncher, _gate, "logs");

        await vm.InitializeAsync();

        Assert.Single(vm.AllTweaks);
        Assert.Contains(vm.Banners, b => b.Text.Contains("2 problema(s)", StringComparison.Ordinal) && b.Tone == Tone.Warning);
    }

    [Fact]
    public async Task An_empty_catalog_shows_a_clear_error_banner()
    {
        var vm = new CleanupViewModel(
            TweakCatalog.Empty, 0, _sys.Engine, _sys.Detector, _sys.Windows, _sys.RestorePoints, _sys.Backups, _dialogs, _relauncher, _gate, "logs");

        await vm.InitializeAsync();

        Assert.Contains(vm.Banners, b => b.Tone == Tone.Danger && b.Text.Contains("catálogo", StringComparison.Ordinal));
        Assert.True(vm.HasNoVisibleTweaks);
    }

    [Fact]
    public async Task Protection_state_is_not_checked_without_administrator_rights()
    {
        _sys.Windows.IsElevated = false;
        _sys.RestorePoints.ProtectionEnabled = false;

        var vm = await InitializedAsync();

        Assert.DoesNotContain(vm.Banners, b => b.Text.Contains("Proteção do Sistema", StringComparison.Ordinal));
        Assert.Single(vm.Banners);
    }

    // ---------- Utilitários ----------

    private static void SelectProfile(CleanupViewModel vm, string name) =>
        vm.ProfileFilter.Options.Single(o => o.Label == name).IsSelected = true;

    private static string[] Selected(CleanupViewModel vm) =>
        [.. vm.AllTweaks.Where(t => t.IsSelected).Select(t => t.Id).Order()];

}
