using TrashSweepr.Cleanup.Core.Abstractions;
using TrashSweepr.Cleanup.Core.Backup;
using TrashSweepr.Cleanup.Core.Execution;
using TrashSweepr.Cleanup.Core.Logging;
using TrashSweepr.Cleanup.Core.Models;
using TrashSweepr.Cleanup.Core.Operations;
using TrashSweepr.Tests.Helpers;
using static TrashSweepr.Tests.Helpers.CatalogJson;
using static TrashSweepr.Tests.Helpers.TestTweaks;

namespace TrashSweepr.Tests;

public sealed class EngineApplyTests : IDisposable
{
    private readonly FakeSystem _sys = new();

    public void Dispose() => _sys.Dispose();

    // ---------- Fluxo básico ----------

    [Fact]
    public async Task Apply_writes_value_saves_backup_first_and_verifies()
    {
        var result = await _sys.Engine.ApplyAsync(RegistryTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.Equal(0, _sys.Registry.Get(RegistryHive.Hkcu, Key, "V"));
        Assert.Equal(1, _sys.BackupFileCount());
        var op = Assert.Single(result.Operations);
        Assert.Equal(ResultStatus.Success, op.Status);
        Assert.IsType<RegistrySetOperation>(op.Operation);
    }

    [Fact]
    public async Task Backup_is_on_disk_before_the_first_change()
    {
        // Falha na primeira escrita: o backup já tem que existir, e a restauração automática o usa.
        _sys.Registry.FailOnSet = _ => true;

        var result = await _sys.Engine.ApplyAsync(RegistryTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(1, _sys.BackupFileCount());
    }

    [Fact]
    public async Task Already_applied_tweak_is_skipped_without_backup_or_changes()
    {
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "V", RegistryValueType.Dword, 0);

        var result = await _sys.Engine.ApplyAsync(RegistryTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Skipped, result.Status);
        Assert.Equal(SkipReason.AlreadyApplied, result.SkipReason);
        Assert.Empty(_sys.Mutations);
        Assert.Equal(0, _sys.BackupFileCount());
    }

    [Fact]
    public async Task Only_pending_actions_are_executed()
    {
        var tweak = Make(
            "test.two",
            Registry(path: Key, name: "A", valueJson: "0") + "," + Registry(path: Key, name: "B", valueJson: "0"),
            Registry(path: Key, name: "A", valueJson: "1"));
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "A", RegistryValueType.Dword, 0);

        var result = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.Single(result.Operations);
        Assert.Equal([$@"reg set Hkcu\{Key}\B"], _sys.Mutations);
    }

    [Fact]
    public async Task Missing_service_is_skipped_as_not_applicable()
    {
        var result = await _sys.Engine.ApplyAsync(ServiceTweak(), FakeSystem.Real, Ct);

        Assert.Equal(SkipReason.NotApplicable, result.SkipReason);
        Assert.Empty(_sys.Mutations);
    }

    // ---------- Dry run ----------

    [Fact]
    public async Task Dry_run_changes_nothing_and_lists_the_exact_commands()
    {
        _sys.Services.Services["FooSvc"] = new ServiceState(true, ServiceStartType.Automatic, true);
        _sys.Appx.Install("Contoso.Sample");
        var tweak = Make(
            "test.mix",
            Registry(path: Key, name: "V", valueJson: "0") + "," + Service("FooSvc", "disabled", stop: true) + "," + Appx("Contoso.Sample", "remove"),
            Registry(path: Key, name: "V", valueJson: "1"));

        var result = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Dry, Ct);

        Assert.Equal(ResultStatus.DryRun, result.Status);
        Assert.Empty(_sys.Mutations);
        Assert.Equal(0, _sys.BackupFileCount());
        Assert.Null(_sys.Registry.Get(RegistryHive.Hkcu, Key, "V"));
        Assert.Equal(0, _sys.RestorePoints.CreateCalls);

        var commands = result.Operations.Select(o => o.Operation.Command).ToList();
        Assert.Equal(4, commands.Count);
        Assert.Contains($@"registry> SET HKCU\{Key}\V = 0 (REG_DWORD)", commands);
        Assert.Contains(commands, c => c.StartsWith("powershell> ", StringComparison.Ordinal) && c.Contains("sc.exe config", StringComparison.Ordinal) && c.Contains("\"FooSvc\"", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.Contains("Stop-Service", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.Contains("Remove-AppxPackage", StringComparison.Ordinal) && c.Contains("Contoso.Sample", StringComparison.Ordinal));
        Assert.All(result.Operations, o => Assert.Equal(ResultStatus.DryRun, o.Status));
    }

    [Fact]
    public async Task Dry_run_of_a_pending_revert_lists_what_would_be_restored()
    {
        await _sys.Engine.ApplyAsync(RegistryTweak(), FakeSystem.Real, Ct);
        _sys.Mutations.Clear();

        var result = await _sys.Engine.RevertAsync(RegistryTweak(), FakeSystem.Dry, Ct);

        Assert.Equal(ResultStatus.DryRun, result.Status);
        Assert.True(result.UsedBackup);
        Assert.Empty(_sys.Mutations);
        Assert.NotEmpty(result.Operations);
        Assert.Equal(0, _sys.Registry.Get(RegistryHive.Hkcu, Key, "V"));
    }

    // ---------- Confirmação e compatibilidade ----------

    [Fact]
    public async Task Moderate_tweak_requires_explicit_confirmation()
    {
        var tweak = RegistryTweak(risk: "moderate", riskNote: "Pode afetar algo.");

        var refused = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);
        Assert.Equal(SkipReason.NotConfirmed, refused.SkipReason);
        Assert.Empty(_sys.Mutations);

        var other = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Confirmed("test.outro"), Ct);
        Assert.Equal(SkipReason.NotConfirmed, other.SkipReason);

        var accepted = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Confirmed("test.reg"), Ct);
        Assert.Equal(ResultStatus.Success, accepted.Status);
    }

    [Fact]
    public async Task Advanced_tweak_requires_explicit_confirmation()
    {
        var tweak = RegistryTweak(risk: "advanced", riskNote: "Risco alto.");

        var refused = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal(SkipReason.NotConfirmed, refused.SkipReason);
    }

    [Fact]
    public async Task Dry_run_does_not_need_confirmation()
    {
        var tweak = RegistryTweak(risk: "moderate", riskNote: "Pode afetar algo.");

        var result = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Dry, Ct);

        Assert.Equal(ResultStatus.DryRun, result.Status);
    }

    [Fact]
    public async Task Incompatible_build_is_skipped()
    {
        var tweak = Make("test.new", Registry(path: Key, valueJson: "0"), Registry(path: Key, valueJson: "1"), extra: ", \"minBuild\": 26100");

        var result = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal(SkipReason.Incompatible, result.SkipReason);
        Assert.Empty(_sys.Mutations);

        _sys.Windows.Build = 19045;
        var windows10 = await _sys.Engine.ApplyAsync(RegistryTweak(), FakeSystem.Real, Ct);
        Assert.Equal(SkipReason.Incompatible, windows10.SkipReason);
    }

    // ---------- Falhas e rollback ----------

    [Fact]
    public async Task Failure_midway_rolls_back_earlier_changes()
    {
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "V", RegistryValueType.Dword, 5);
        _sys.Services.Services["FooSvc"] = new ServiceState(true, ServiceStartType.Automatic, true);
        _sys.Services.FailSetStartType.Add("FooSvc");
        var tweak = Make(
            "test.rollback",
            Registry(path: Key, name: "V", valueJson: "0") + "," + Service("FooSvc", "disabled"),
            Registry(path: Key, name: "V", valueJson: "1"));

        var result = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.True(result.RolledBack);
        Assert.Contains("sc.exe", result.Error, StringComparison.Ordinal);
        Assert.Equal(5, _sys.Registry.Get(RegistryHive.Hkcu, Key, "V"));
        Assert.Equal(ServiceStartType.Automatic, _sys.Services.Services["FooSvc"].StartType);
        Assert.Equal([ResultStatus.Success, ResultStatus.Failed], result.Operations.Select(o => o.Status));
        Assert.Null(await _sys.Backups.GetActiveAsync("test.rollback", Ct));
    }

    [Fact]
    public async Task Command_that_has_no_effect_is_detected_by_verification_and_rolled_back()
    {
        _sys.Services.Services["FooSvc"] = new ServiceState(true, ServiceStartType.Automatic, true);
        _sys.Services.IgnoreSetStartType.Add("FooSvc");

        var result = await _sys.Engine.ApplyAsync(ServiceTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Contains("verificação", result.Error, StringComparison.Ordinal);
        Assert.True(result.RolledBack);
    }

    [Fact]
    public async Task Access_denied_becomes_a_failed_result_not_an_exception()
    {
        _sys.Registry.FailOnSet = target => target.Contains("HKLM", StringComparison.OrdinalIgnoreCase) || target.Contains("Hklm", StringComparison.Ordinal);
        var tweak = Make("test.hklm", Registry(hive: "hklm", path: @"SOFTWARE\Cleanup\Test", valueJson: "0"), Registry(hive: "hklm", path: @"SOFTWARE\Cleanup\Test", valueJson: "1"));

        var result = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Contains("Acesso negado", result.Error, StringComparison.Ordinal);
        Assert.Contains(_sys.Log.Entries, e => e.Severity == LogSeverity.Error && e.TweakId == "test.hklm");
    }

    [Fact]
    public async Task Failing_to_remove_an_app_does_not_leave_other_changes_behind()
    {
        _sys.Appx.Install("Contoso.A");
        _sys.Appx.Install("Contoso.B");
        _sys.Appx.FailRemove.Add("Contoso.B");
        var tweak = Make("test.apps", Appx("Contoso.A", "remove") + "," + Appx("Contoso.B", "remove"), Appx("Contoso.A", "reinstall"));

        var result = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.True(result.RolledBack);
        Assert.True(_sys.Appx.Packages["Contoso.A"].InstalledForCurrentUser);
        Assert.Contains("appx reinstall Contoso.A", _sys.Mutations);
    }

    [Fact]
    public async Task Cancellation_during_apply_is_rolled_back()
    {
        using var cts = new CancellationTokenSource();
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "V", RegistryValueType.Dword, 5);
        _sys.Services.Services["FooSvc"] = new ServiceState(true, ServiceStartType.Automatic, true);
        _sys.Services.OnSetStartType = _ => cts.Cancel();
        var tweak = Make(
            "test.cancel",
            Registry(path: Key, name: "V", valueJson: "0") + "," + Service("FooSvc", "disabled"),
            Registry(path: Key, name: "V", valueJson: "1"));

        var result = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, cts.Token);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Contains("Cancelado", result.Error, StringComparison.Ordinal);
        Assert.True(result.RolledBack);
        Assert.Equal(5, _sys.Registry.Get(RegistryHive.Hkcu, Key, "V"));
    }

    [Theory]
    [InlineData("en-US", "Verification after running failed: the system did not end up in the expected state.")]
    [InlineData("pt-BR", "A verificação após a execução falhou: o sistema não ficou no estado esperado.")]
    [InlineData("pt-PT", "A verificação após a execução falhou: o sistema não ficou no estado esperado.")]
    [InlineData("de-DE", "Verification after running failed: the system did not end up in the expected state.")]
    public async Task Engine_errors_follow_the_interface_language(string culture, string expected)
    {
        _sys.Services.Services["FooSvc"] = new ServiceState(true, ServiceStartType.Automatic, true);
        _sys.Services.IgnoreSetStartType.Add("FooSvc");

        using (TestCulture.Use(culture))
        {
            var result = await _sys.Engine.ApplyAsync(ServiceTweak(), FakeSystem.Real, Ct);

            Assert.Equal(expected, result.Error);
        }
    }

    [Theory]
    [InlineData("en-US", @"Backup failed; nothing was changed. The value Hkcu\Software\Cleanup\Test\V is of type Binary, which trashsweepr cannot back up. Nothing was changed.")]
    [InlineData("pt-BR", @"Backup não realizado; nada foi alterado. O valor Hkcu\Software\Cleanup\Test\V é do tipo Binary, que o trashsweepr não consegue salvar. Nada foi alterado.")]
    public async Task Backup_errors_follow_the_interface_language(string culture, string expected)
    {
        _sys.Registry.SeedBinary(RegistryHive.Hkcu, Key, "V");

        using (TestCulture.Use(culture))
        {
            var result = await _sys.Engine.ApplyAsync(RegistryTweak(), FakeSystem.Real, Ct);

            Assert.Equal(expected, result.Error);
        }
    }

    [Fact]
    public async Task Existing_value_of_an_unsupported_type_blocks_the_change()
    {
        _sys.Registry.SeedBinary(RegistryHive.Hkcu, Key, "V");

        var result = await _sys.Engine.ApplyAsync(RegistryTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Contains("nada foi alterado", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_sys.Mutations);
        Assert.Equal(0, _sys.BackupFileCount());
    }

    // ---------- Defesa em profundidade ----------

    [Fact]
    public async Task Engine_refuses_critical_components_even_when_the_loader_was_bypassed()
    {
        _sys.Services.Services["wuauserv"] = new ServiceState(true, ServiceStartType.Automatic, true);

        var service = await _sys.Engine.ApplyAsync(
            Unvalidated(new ServiceAction { Name = "wuauserv", StartType = ServiceStartType.Disabled }), FakeSystem.Real, Ct);
        var package = await _sys.Engine.ApplyAsync(
            Unvalidated(new AppxAction { Operation = AppxOperation.Remove, PackageName = "Microsoft.WindowsStore" }), FakeSystem.Real, Ct);
        var registry = await _sys.Engine.ApplyAsync(
            Unvalidated(new RegistryAction
            {
                Hive = RegistryHive.Hklm,
                Path = @"SOFTWARE\Policies\Microsoft\Windows Defender",
                Name = "DisableAntiSpyware",
                Value = System.Text.Json.JsonDocument.Parse("1").RootElement,
            }),
            FakeSystem.Real,
            Ct);

        foreach (var result in new[] { service, package, registry })
        {
            Assert.Equal(ResultStatus.Failed, result.Status);
            Assert.Contains("protegido", result.Error, StringComparison.Ordinal);
        }

        Assert.Empty(_sys.Mutations);
        Assert.Equal(0, _sys.BackupFileCount());
    }

    [Fact]
    public async Task Engine_refuses_injection_in_names_even_when_the_loader_was_bypassed()
    {
        var result = await _sys.Engine.ApplyAsync(
            Unvalidated(new ServiceAction { Name = "Foo; Remove-Item C:\\ -Recurse", StartType = ServiceStartType.Disabled }),
            FakeSystem.Real,
            Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task Delete_key_is_not_accepted_as_an_apply_action()
    {
        var result = await _sys.Engine.ApplyAsync(
            Unvalidated(new RegistryAction
            {
                Operation = RegistryOperation.DeleteKey,
                Hive = RegistryHive.Hkcu,
                Path = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}",
            }),
            FakeSystem.Real,
            Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Empty(_sys.Mutations);
    }

    // ---------- Serviços, tarefas, apps ----------

    [Fact]
    public async Task Service_is_disabled_and_stopped_and_backup_remembers_it_was_running()
    {
        _sys.Services.Services["FooSvc"] = new ServiceState(true, ServiceStartType.Automatic, true);

        var result = await _sys.Engine.ApplyAsync(ServiceTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.Equal(new ServiceState(true, ServiceStartType.Disabled, false), _sys.Services.Services["FooSvc"]);
        var backup = await _sys.Backups.GetActiveAsync("test.svc", Ct);
        var item = Assert.IsType<ServiceBackup>(Assert.Single(backup!.Items));
        Assert.Equal(new ServiceBackup("FooSvc", true, ServiceStartType.Automatic, true), item);
    }

    [Fact]
    public async Task Delayed_automatic_services_keep_their_delayed_mode_in_the_backup()
    {
        _sys.Services.Services["FooSvc"] = new ServiceState(true, ServiceStartType.AutomaticDelayed, true);
        await _sys.Engine.ApplyAsync(ServiceTweak(), FakeSystem.Real, Ct);

        await _sys.Engine.RevertAsync(ServiceTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ServiceStartType.AutomaticDelayed, _sys.Services.Services["FooSvc"].StartType);
    }

    [Fact]
    public async Task Scheduled_task_is_disabled_and_not_deleted()
    {
        _sys.Tasks.Tasks[@"\Cleanup\Test\Job"] = new TaskState(true, true);
        var tweak = Make("test.task", SchedTask(@"\Cleanup\Test\", "Job", enabled: false), SchedTask(@"\Cleanup\Test\", "Job", enabled: true));

        var result = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.Equal(new TaskState(true, false), _sys.Tasks.Tasks[@"\Cleanup\Test\Job"]);
    }

    [Fact]
    public async Task Missing_task_is_skipped()
    {
        var tweak = Make("test.task", SchedTask(@"\Cleanup\Test\", "Job", enabled: false), SchedTask(@"\Cleanup\Test\", "Job", enabled: true));

        var result = await _sys.Engine.ApplyAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal(SkipReason.NotApplicable, result.SkipReason);
    }

    [Fact]
    public async Task App_removal_is_verified_and_already_removed_app_counts_as_applied()
    {
        _sys.Appx.Install("Contoso.Sample");

        var first = await _sys.Engine.ApplyAsync(AppTweak(), FakeSystem.Real, Ct);
        var second = await _sys.Engine.ApplyAsync(AppTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, first.Status);
        Assert.False(_sys.Appx.Packages["Contoso.Sample"].InstalledForCurrentUser);
        Assert.Equal(SkipReason.AlreadyApplied, second.SkipReason);
    }

    // ---------- Chaves criadas / backup original ----------

    [Fact]
    public async Task Reapplying_after_a_manual_change_keeps_the_original_backup()
    {
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "V", RegistryValueType.Dword, 5);
        await _sys.Engine.ApplyAsync(RegistryTweak(), FakeSystem.Real, Ct);
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "V", RegistryValueType.Dword, 9); // alteração manual do usuário

        var again = await _sys.Engine.ApplyAsync(RegistryTweak(), FakeSystem.Real, Ct);
        var revert = await _sys.Engine.RevertAsync(RegistryTweak(), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, again.Status);
        Assert.Equal(ResultStatus.Success, revert.Status);
        Assert.Equal(5, _sys.Registry.Get(RegistryHive.Hkcu, Key, "V"));
        Assert.Equal(1, _sys.BackupFileCount());
    }

    // ---------- Log ----------

    [Fact]
    public async Task Every_operation_is_logged_with_tweak_command_and_result()
    {
        await _sys.Engine.ApplyAsync(RegistryTweak(), FakeSystem.Real, Ct);

        var applyEntry = Assert.Single(_sys.Log.Entries, e => e.Operation == "apply");
        Assert.Equal("test.reg", applyEntry.TweakId);
        Assert.Equal("ok", applyEntry.Result);
        Assert.Contains("registry> SET", applyEntry.Command, StringComparison.Ordinal);
        Assert.Contains(_sys.Log.Entries, e => e.Operation == "backup");
    }

    [Fact]
    public async Task Failures_are_logged_with_the_error_message()
    {
        _sys.Registry.FailOnSet = _ => true;

        await _sys.Engine.ApplyAsync(RegistryTweak(), FakeSystem.Real, Ct);

        var entry = Assert.Single(_sys.Log.Entries, e => e is { Operation: "apply", Result: "failed" });
        Assert.Contains("Acesso negado", entry.Error, StringComparison.Ordinal);
        Assert.Equal(LogSeverity.Error, entry.Severity);
    }

    [Fact]
    public async Task Skips_are_logged_too()
    {
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "V", RegistryValueType.Dword, 0);

        await _sys.Engine.ApplyAsync(RegistryTweak(), FakeSystem.Real, Ct);

        Assert.Contains(_sys.Log.Entries, e => e.TweakId == "test.reg" && e.Result.StartsWith("skipped", StringComparison.Ordinal));
    }
}
