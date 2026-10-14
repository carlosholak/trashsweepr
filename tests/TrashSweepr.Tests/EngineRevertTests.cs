using TrashSweepr.Limpeza.Core.Abstractions;
using TrashSweepr.Limpeza.Core.Backup;
using TrashSweepr.Limpeza.Core.Execution;
using TrashSweepr.Limpeza.Core.Models;
using TrashSweepr.Tests.Helpers;
using static TrashSweepr.Tests.Helpers.CatalogJson;
using static TrashSweepr.Tests.Helpers.TestTweaks;

namespace TrashSweepr.Tests;

public sealed class EngineRevertTests : IDisposable
{
    private readonly FakeSystem _sys = new();

    public void Dispose() => _sys.Dispose();

    [Fact]
    public async Task Revert_restores_a_value_that_did_not_exist_and_removes_the_keys_it_created()
    {
        _sys.Registry.SeedKey(RegistryHive.Hkcu, @"Software\Limpeza");
        var tweak = RegistryTweak(path: @"Software\Limpeza\Novo\Fundo");

        await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);
        Assert.True(_sys.Registry.HasKey(RegistryHive.Hkcu, @"Software\Limpeza\Novo\Fundo"));

        var result = await _sys.Engine.RevertAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.True(result.UsedBackup);
        Assert.False(_sys.Registry.HasKey(RegistryHive.Hkcu, @"Software\Limpeza\Novo\Fundo"));
        Assert.False(_sys.Registry.HasKey(RegistryHive.Hkcu, @"Software\Limpeza\Novo"));
        Assert.True(_sys.Registry.HasKey(RegistryHive.Hkcu, @"Software\Limpeza"));
    }

    [Fact]
    public async Task Revert_does_not_remove_keys_that_gained_other_content()
    {
        var tweak = RegistryTweak(path: @"Software\Limpeza\Novo");
        await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);
        _sys.Registry.Seed(RegistryHive.Hkcu, @"Software\Limpeza\Novo", "OutraCoisa", RegistryValueType.Sz, "x");

        await _sys.Engine.RevertAsync(tweak, FakeSystem.Real, Ct);

        Assert.True(_sys.Registry.HasKey(RegistryHive.Hkcu, @"Software\Limpeza\Novo"));
        Assert.Equal("x", _sys.Registry.Get(RegistryHive.Hkcu, @"Software\Limpeza\Novo", "OutraCoisa"));
        Assert.Null(_sys.Registry.Get(RegistryHive.Hkcu, @"Software\Limpeza\Novo", "V"));
    }

    [Fact]
    public async Task Revert_restores_the_users_previous_value_and_type_not_the_windows_default()
    {
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "V", RegistryValueType.Dword, 7);

        await _sys.Engine.ApplyAsync(RegistryTweak(), FakeSystem.Real, Ct);
        var result = await _sys.Engine.RevertAsync(RegistryTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.Equal(7, _sys.Registry.Get(RegistryHive.Hkcu, Key, "V")); // o revert "padrão" do catálogo escreveria 1
        Assert.Null(await _sys.Backups.GetActiveAsync("test.reg", Ct));
    }

    [Fact]
    public async Task Revert_restores_string_and_qword_values_with_their_types()
    {
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "S", RegistryValueType.Sz, "texto original");
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "Q", RegistryValueType.Qword, 9_000_000_000L);
        var tweak = Make(
            "test.types",
            Registry(path: Key, name: "S", kind: "sz", valueJson: "\"novo\"") + "," + Registry(path: Key, name: "Q", kind: "qword", valueJson: "1"),
            Registry(path: Key, name: "S", kind: "sz", valueJson: "\"padrao\""));

        await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);
        await _sys.Engine.RevertAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal("texto original", _sys.Registry.Get(RegistryHive.Hkcu, Key, "S"));
        Assert.Equal(9_000_000_000L, _sys.Registry.Get(RegistryHive.Hkcu, Key, "Q"));
    }

    [Fact]
    public async Task Revert_of_the_classic_context_menu_style_default_value_removes_the_created_key_chain()
    {
        const string guidKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
        _sys.Registry.SeedKey(RegistryHive.Hkcu, @"Software\Classes\CLSID");
        var tweak = Make(
            "test.menu",
            Registry(path: guidKey + @"\InprocServer32", name: "", kind: "sz", valueJson: "\"\""),
            $$"""{ "type": "registry", "operation": "deleteKey", "hive": "hkcu", "path": "{{guidKey.Replace("\\", "\\\\", StringComparison.Ordinal)}}" }""");

        var apply = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);
        Assert.Equal(ResultStatus.Success, apply.Status);
        Assert.Equal(string.Empty, _sys.Registry.Get(RegistryHive.Hkcu, guidKey + @"\InprocServer32", string.Empty));

        var revert = await _sys.Engine.RevertAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, revert.Status);
        Assert.False(_sys.Registry.HasKey(RegistryHive.Hkcu, guidKey + @"\InprocServer32"));
        Assert.False(_sys.Registry.HasKey(RegistryHive.Hkcu, guidKey));
        Assert.True(_sys.Registry.HasKey(RegistryHive.Hkcu, @"Software\Classes\CLSID"));
    }

    [Fact]
    public async Task Revert_restarts_a_service_that_was_running_before()
    {
        _sys.Services.Services["FooSvc"] = new ServiceState(true, ServiceStartType.Automatic, true);
        await _sys.Engine.ApplyAsync(ServiceTweak(), FakeSystem.Real, Ct);

        var result = await _sys.Engine.RevertAsync(ServiceTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.Equal(new ServiceState(true, ServiceStartType.Automatic, true), _sys.Services.Services["FooSvc"]);
    }

    [Fact]
    public async Task Revert_does_not_start_a_service_that_was_not_running_before()
    {
        _sys.Services.Services["FooSvc"] = new ServiceState(true, ServiceStartType.Manual, false);
        await _sys.Engine.ApplyAsync(ServiceTweak(), FakeSystem.Real, Ct);
        _sys.Mutations.Clear();

        await _sys.Engine.RevertAsync(ServiceTweak(), FakeSystem.Real, Ct);

        Assert.DoesNotContain("svc start FooSvc", _sys.Mutations);
        Assert.Equal(ServiceStartType.Manual, _sys.Services.Services["FooSvc"].StartType);
    }

    [Fact]
    public async Task Revert_re_enables_a_task_that_was_enabled()
    {
        _sys.Tasks.Tasks[@"\Limpeza\Test\Job"] = new TaskState(true, true);
        var tweak = Make("test.task", SchedTask(@"\Limpeza\Test\", "Job", enabled: false), SchedTask(@"\Limpeza\Test\", "Job", enabled: true));
        await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);

        await _sys.Engine.RevertAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal(new TaskState(true, true), _sys.Tasks.Tasks[@"\Limpeza\Test\Job"]);
    }

    [Fact]
    public async Task Revert_re_registers_a_removed_app()
    {
        _sys.Appx.Install("Contoso.Sample");
        await _sys.Engine.ApplyAsync(AppTweak(), FakeSystem.Real, Ct);

        var result = await _sys.Engine.RevertAsync(AppTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.True(_sys.Appx.Packages["Contoso.Sample"].InstalledForCurrentUser);
    }

    [Fact]
    public async Task Revert_of_an_app_that_was_not_installed_before_installs_nothing()
    {
        // O app já estava removido: o backup diz "não instalado", então desfazer não reinstala.
        var tweak = Make("test.app", Appx("Contoso.Sample", "remove"), Appx("Contoso.Sample", "reinstall"));
        _sys.Appx.Install("Contoso.Sample");
        await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);
        var record = (await _sys.Backups.GetActiveAsync("test.app", Ct))!;
        var notInstalledBefore = record with { Items = [new AppxBackup("Contoso.Sample", false, [])] };

        var result = await _sys.Engine.RevertRecordAsync(notInstalledBefore, FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.False(_sys.Appx.Packages["Contoso.Sample"].InstalledForCurrentUser);
    }

    [Fact]
    public async Task Revert_that_cannot_reinstall_fails_clearly_and_keeps_the_backup_active()
    {
        _sys.Appx.Install("Contoso.Sample");
        await _sys.Engine.ApplyAsync(AppTweak(), FakeSystem.Real, Ct);
        _sys.Appx.Packages["Contoso.Sample"] = AppxPackageState.Missing; // arquivos do pacote sumiram

        var result = await _sys.Engine.RevertAsync(AppTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Contains("Microsoft Store", result.Error, StringComparison.Ordinal);
        Assert.NotNull(await _sys.Backups.GetActiveAsync("test.app", Ct));
    }

    [Fact]
    public async Task Partial_restore_continues_with_other_items_and_keeps_the_backup()
    {
        _sys.Appx.Install("Contoso.A");
        _sys.Appx.Install("Contoso.B");
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "V", RegistryValueType.Dword, 5);
        var tweak = Make(
            "test.partial",
            Registry(path: Key, name: "V", valueJson: "0") + "," + Appx("Contoso.A", "remove") + "," + Appx("Contoso.B", "remove"),
            Registry(path: Key, name: "V", valueJson: "1"));
        await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);
        _sys.Appx.Packages["Contoso.B"] = AppxPackageState.Missing;

        var result = await _sys.Engine.RevertAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(5, _sys.Registry.Get(RegistryHive.Hkcu, Key, "V"));
        Assert.True(_sys.Appx.Packages["Contoso.A"].InstalledForCurrentUser);
        Assert.NotNull(await _sys.Backups.GetActiveAsync("test.partial", Ct));
    }

    [Fact]
    public async Task Restore_that_does_not_take_effect_fails_and_keeps_the_backup_active()
    {
        _sys.Services.Services["FooSvc"] = new ServiceState(true, ServiceStartType.Manual, false);
        await _sys.Engine.ApplyAsync(ServiceTweak(), FakeSystem.Real, Ct);
        _sys.Services.IgnoreSetStartType.Add("FooSvc"); // o comando "funciona", mas nada muda

        var result = await _sys.Engine.RevertAsync(ServiceTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Contains("verificação", result.Error, StringComparison.Ordinal);
        Assert.NotNull(await _sys.Backups.GetActiveAsync("test.svc", Ct));
    }

    // ---------- Sem backup (padrões do Windows) ----------

    [Fact]
    public async Task Without_a_backup_revert_uses_the_catalog_defaults()
    {
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "V", RegistryValueType.Dword, 0); // aplicado fora do trashsweepr

        var result = await _sys.Engine.RevertAsync(RegistryTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.False(result.UsedBackup);
        Assert.Equal(1, _sys.Registry.Get(RegistryHive.Hkcu, Key, "V"));
        Assert.Equal(0, _sys.BackupFileCount());
    }

    [Fact]
    public async Task Without_a_backup_revert_skips_tweaks_that_are_not_applied()
    {
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "V", RegistryValueType.Dword, 5);

        var result = await _sys.Engine.RevertAsync(RegistryTweak(), FakeSystem.Real, Ct);

        Assert.Equal(SkipReason.NotApplied, result.SkipReason);
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task Without_a_backup_revert_reinstalls_only_the_apps_that_are_missing()
    {
        _sys.Appx.Install("Contoso.A");
        _sys.Appx.Install("Contoso.B");
        _sys.Appx.Packages["Contoso.A"] = _sys.Appx.Packages["Contoso.A"] with { InstalledForCurrentUser = false };
        var tweak = Make(
            "test.apps",
            Appx("Contoso.A", "remove") + "," + Appx("Contoso.B", "remove"),
            Appx("Contoso.A", "reinstall") + "," + Appx("Contoso.B", "reinstall"));

        var result = await _sys.Engine.RevertAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.Equal(["appx reinstall Contoso.A"], _sys.Mutations);
    }

    [Fact]
    public async Task Without_a_backup_a_failing_default_is_reported_without_throwing()
    {
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "V", RegistryValueType.Dword, 0);
        _sys.Registry.FailOnSet = _ => true;

        var result = await _sys.Engine.RevertAsync(RegistryTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Contains("Acesso negado", result.Error, StringComparison.Ordinal);
    }

    // ---------- Reverter tudo ----------

    [Fact]
    public async Task Revert_all_undoes_every_active_backup_newest_first()
    {
        var first = RegistryTweak("test.first", name: "A");
        var second = RegistryTweak("test.second", name: "B");
        await _sys.Engine.ApplyAsync(first, FakeSystem.Real, Ct);
        await _sys.Engine.ApplyAsync(second, FakeSystem.Real, Ct);
        _sys.Mutations.Clear();

        var batch = await _sys.Engine.RevertAllAsync(FakeSystem.Real, null, Ct);

        Assert.Equal(["test.second", "test.first"], batch.Results.Select(r => r.TweakId));
        Assert.All(batch.Results, r => Assert.Equal(ResultStatus.Success, r.Status));
        Assert.Equal(
            [$@"reg delete-value Hkcu\{Key}\B", $@"reg delete-value Hkcu\{Key}\A"],
            _sys.Mutations.Where(m => m.StartsWith("reg delete-value", StringComparison.Ordinal)));
        Assert.Empty(await _sys.Backups.GetAllActiveNewestFirstAsync(Ct));
    }

    [Fact]
    public async Task Revert_all_works_even_if_the_tweak_is_no_longer_in_the_catalog()
    {
        await _sys.Engine.ApplyAsync(RegistryTweak("test.removed-from-catalog"), FakeSystem.Real, Ct);

        var batch = await _sys.Engine.RevertAllAsync(FakeSystem.Real, null, Ct);

        Assert.Equal(ResultStatus.Success, Assert.Single(batch.Results).Status);
        Assert.Null(_sys.Registry.Get(RegistryHive.Hkcu, Key, "V"));
    }

    [Fact]
    public async Task Revert_all_ignores_already_reverted_backups_and_reports_progress()
    {
        var tweak = RegistryTweak();
        await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);
        await _sys.Engine.RevertAsync(tweak, FakeSystem.Real, Ct);
        await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);
        var progress = new SyncProgress<BatchProgress>();

        var batch = await _sys.Engine.RevertAllAsync(FakeSystem.Real, progress, Ct);

        Assert.Single(batch.Results);
        Assert.Equal(2, progress.Reports.Count);
        Assert.Equal(1, progress.Reports[^1].Completed);
        Assert.Equal(1, progress.Reports[^1].Total);
    }

    [Fact]
    public async Task Revert_all_dry_run_changes_nothing()
    {
        await _sys.Engine.ApplyAsync(RegistryTweak(), FakeSystem.Real, Ct);
        _sys.Mutations.Clear();

        var batch = await _sys.Engine.RevertAllAsync(FakeSystem.Dry, null, Ct);

        Assert.Equal(ResultStatus.DryRun, Assert.Single(batch.Results).Status);
        Assert.Empty(_sys.Mutations);
        Assert.NotNull(await _sys.Backups.GetActiveAsync("test.reg", Ct));
    }

    [Fact]
    public async Task Revert_all_with_a_list_reverts_only_the_given_records_in_that_order()
    {
        await _sys.Engine.ApplyAsync(RegistryTweak("test.first", name: "A"), FakeSystem.Real, Ct);
        await _sys.Engine.ApplyAsync(RegistryTweak("test.second", name: "B"), FakeSystem.Real, Ct);
        await _sys.Engine.ApplyAsync(RegistryTweak("test.third", name: "C"), FakeSystem.Real, Ct);
        var shown = (await _sys.Backups.GetAllActiveNewestFirstAsync(Ct)).Where(r => r.TweakId != "test.second").ToList();

        var batch = await _sys.Engine.RevertAllAsync(shown, FakeSystem.Real, null, Ct);

        Assert.Equal(["test.third", "test.first"], batch.Results.Select(r => r.TweakId));
        Assert.All(batch.Results, r => Assert.Equal(ResultStatus.Success, r.Status));
        Assert.Equal(["test.second"], (await _sys.Backups.GetAllActiveNewestFirstAsync(Ct)).Select(r => r.TweakId));
    }

    [Fact]
    public async Task Revert_all_with_a_list_skips_records_that_are_no_longer_active()
    {
        var tweak = RegistryTweak();
        await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);
        var shown = await _sys.Backups.GetAllActiveNewestFirstAsync(Ct);
        await _sys.Engine.RevertAsync(tweak, FakeSystem.Real, Ct); // desfeito entre a confirmação e a execução
        _sys.Mutations.Clear();

        var batch = await _sys.Engine.RevertAllAsync(shown, FakeSystem.Real, null, Ct);

        var result = Assert.Single(batch.Results);
        Assert.Equal(ResultStatus.Skipped, result.Status);
        Assert.Empty(_sys.Mutations);
    }

    // ---------- Backup adulterado ----------

    [Fact]
    public async Task A_tampered_backup_cannot_touch_protected_components()
    {
        _sys.Services.Services["wuauserv"] = new ServiceState(true, ServiceStartType.Automatic, true);
        var tampered = new BackupRecord
        {
            Id = "abc123",
            TweakId = "test.tampered",
            CreatedAt = DateTimeOffset.UtcNow,
            Items =
            [
                new ServiceBackup("wuauserv", true, ServiceStartType.Disabled, false),
                new RegistryValueBackup(RegistryHive.Hklm, @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", false, null, null, null),
            ],
        };

        var result = await _sys.Engine.RevertRecordAsync(tampered, FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Contains("inválido", result.Error, StringComparison.Ordinal);
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task A_tampered_created_key_root_is_rejected()
    {
        var tampered = new BackupRecord
        {
            Id = "abc123",
            TweakId = "test.tampered",
            CreatedAt = DateTimeOffset.UtcNow,
            Items = [new RegistryValueBackup(RegistryHive.Hkcu, @"Software\Limpeza\A", "V", false, null, null, @"Software\Outra\Raiz")],
        };

        var result = await _sys.Engine.RevertRecordAsync(tampered, FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Empty(_sys.Mutations);
    }
}
