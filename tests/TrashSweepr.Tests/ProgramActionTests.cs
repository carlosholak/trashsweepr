using System.Text.Json;
using TrashSweepr.Cleanup.Core.Abstractions;
using TrashSweepr.Cleanup.Core.Backup;
using TrashSweepr.Cleanup.Core.Catalog;
using TrashSweepr.Cleanup.Core.Execution;
using TrashSweepr.Cleanup.Core.Models;
using TrashSweepr.Cleanup.Core.Operations;
using TrashSweepr.Cleanup.Infrastructure;
using TrashSweepr.Cleanup.Services;
using TrashSweepr.Tests.Helpers;
using static TrashSweepr.Tests.Helpers.CatalogJson;
using static TrashSweepr.Tests.Helpers.TestTweaks;

namespace TrashSweepr.Tests;

/// <summary>Ação 'program' (Edge e OneDrive pelo desinstalador oficial): catálogo e deny-list.</summary>
public class ProgramValidationTests
{
    private static void AssertRejected(CatalogLoadResult result, string expectedMessagePart)
    {
        Assert.Empty(result.Catalog.Tweaks);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.Message.Contains(expectedMessagePart, StringComparison.OrdinalIgnoreCase));
    }

    private static string ProgramTweakJson(string apply, string revert) =>
        Tweak(id: "apps.test-program", risk: "moderate", riskNote: "Aviso.", apply: apply, revert: revert, category: "apps");

    [Theory]
    [InlineData("edge", ManagedProgram.Edge)]
    [InlineData("onedrive", ManagedProgram.OneDrive)]
    public void Uninstall_in_apply_with_the_matching_reinstall_in_revert_is_accepted(string program, ManagedProgram expected)
    {
        var result = Load(ProgramTweakJson(Program(program, "uninstall"), Program(program, "reinstall")));

        Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Issues));
        var tweak = Assert.Single(result.Catalog.Tweaks);
        Assert.Equal(new ProgramAction { Program = expected, Operation = ProgramOperation.Uninstall }, Assert.Single(tweak.Apply));
        Assert.Equal(new ProgramAction { Program = expected, Operation = ProgramOperation.Reinstall }, Assert.Single(tweak.Revert));
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("Edge")]
    [InlineData("EDGE")]
    [InlineData("oneDrive")]
    [InlineData("OneDrive")]
    [InlineData("edgewebview")]
    [InlineData("")]
    public void Programs_outside_the_closed_list_are_rejected(string program)
    {
        AssertRejected(Load(ProgramTweakJson(Program(program, "uninstall"), Program(program, "reinstall"))), "Programa desconhecido");
    }

    [Theory]
    [InlineData("""{ "type": "program", "program": 0, "operation": "uninstall" }""")]
    [InlineData("""{ "type": "program", "program": null, "operation": "uninstall" }""")]
    [InlineData("""{ "type": "program", "operation": "uninstall" }""")]
    [InlineData("""{ "type": "program", "program": "edge" }""")]
    [InlineData("""{ "type": "program", "program": "edge", "operation": "remove" }""")]
    [InlineData("""{ "type": "program", "program": "edge", "operation": 0 }""")]
    public void Malformed_program_actions_are_rejected(string action)
    {
        var result = Load(ProgramTweakJson(action, Program("edge", "reinstall")));

        Assert.Empty(result.Catalog.Tweaks);
        Assert.True(result.HasErrors);
    }

    [Theory]
    [InlineData("path", "\"C:\\\\Windows\\\\System32\\\\cmd.exe\"")]
    [InlineData("arguments", "\"/c calc\"")]
    [InlineData("script", "\"Remove-Item C:\\\\ -Recurse\"")]
    [InlineData("packageName", "\"Microsoft.MicrosoftEdge.Stable\"")]
    public void Unknown_fields_such_as_paths_or_commands_are_rejected(string field, string valueJson)
    {
        var action = $$"""{ "type": "program", "program": "edge", "operation": "uninstall", "{{field}}": {{valueJson}} }""";

        AssertRejected(Load(ProgramTweakJson(action, Program("edge", "reinstall"))), field);
    }

    [Fact]
    public void Uninstall_is_only_allowed_in_apply()
    {
        AssertRejected(
            Load(ProgramTweakJson(Program("onedrive", "uninstall"), Program("onedrive", "uninstall"))),
            "'uninstall' de um programa só é permitido em 'apply'");
    }

    [Fact]
    public void Reinstall_is_only_allowed_in_revert()
    {
        AssertRejected(
            Load(ProgramTweakJson(Program("onedrive", "reinstall"), Program("onedrive", "reinstall"))),
            "'reinstall' de um programa só é permitido em 'revert'");
    }

    [Fact]
    public void Uninstall_needs_the_reinstall_of_the_same_program_in_revert()
    {
        AssertRejected(Load(ProgramTweakJson(Program("edge", "uninstall"), Registry(valueJson: "0"))), "exige um 'reinstall'");
        AssertRejected(Load(ProgramTweakJson(Program("edge", "uninstall"), Program("onedrive", "reinstall"))), "desinstalar 'edge'");
    }

    [Fact]
    public void Validation_messages_follow_the_interface_language()
    {
        using (TestCulture.Use("en-US"))
        {
            AssertRejected(Load(ProgramTweakJson(Program("chrome", "uninstall"), Program("chrome", "reinstall"))), "Unknown program: 'chrome'");
            AssertRejected(Load(ProgramTweakJson(Program("edge", "uninstall"), Registry(valueJson: "0"))), "needs a 'reinstall'");
        }
    }

    [Fact]
    public void Placement_is_also_checked_when_an_action_is_validated_on_its_own()
    {
        var uninstall = new ProgramAction { Program = ManagedProgram.Edge, Operation = ProgramOperation.Uninstall };
        var reinstall = new ProgramAction { Program = ManagedProgram.Edge, Operation = ProgramOperation.Reinstall };

        Assert.Empty(TweakValidator.ValidateAction(uninstall, allowDeleteKey: false));
        Assert.NotEmpty(TweakValidator.ValidateAction(uninstall, allowDeleteKey: true));
        Assert.Empty(TweakValidator.ValidateAction(reinstall, allowDeleteKey: true));
        Assert.NotEmpty(TweakValidator.ValidateAction(reinstall, allowDeleteKey: false));
        Assert.NotEmpty(TweakValidator.ValidateAction(new ProgramAction { Program = (ManagedProgram)99, Operation = ProgramOperation.Uninstall }, allowDeleteKey: false));
        Assert.NotEmpty(TweakValidator.ValidateAction(new ProgramAction { Program = ManagedProgram.Edge, Operation = (ProgramOperation)99 }, allowDeleteKey: false));
    }

    [Fact]
    public void Only_edge_and_onedrive_are_removable_programs()
    {
        Assert.True(CriticalComponents.IsRemovableProgram(ManagedProgram.Edge));
        Assert.True(CriticalComponents.IsRemovableProgram(ManagedProgram.OneDrive));
        Assert.False(CriticalComponents.IsRemovableProgram((ManagedProgram)99));
        Assert.Equal([ManagedProgram.Edge, ManagedProgram.OneDrive], Enum.GetValues<ManagedProgram>());
    }

    // O Edge só sai pela ação 'program'. Qualquer outro caminho até ele (e até o WebView2 e o Edge Update) continua barrado.
    [Theory]
    [InlineData("hklm", @"SOFTWARE\Microsoft\Edge")]
    [InlineData("hkcu", @"Software\Microsoft\Edge\Main")]
    [InlineData("hklm", @"SOFTWARE\Policies\Microsoft\Edge")]
    [InlineData("hklm", @"SOFTWARE\Microsoft\EdgeUpdate\Clients")]
    [InlineData("hklm", @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate")]
    [InlineData("hklm", @"SOFTWARE\Microsoft\EdgeWebView")]
    [InlineData("hklm", @"SOFTWARE\WOW6432Node\Microsoft\EdgeWebView\BLBeacon")]
    [InlineData("hklm", @"SYSTEM\CurrentControlSet\Services\edgeupdate")]
    [InlineData("hklm", @"SYSTEM\CurrentControlSet\Services\MicrosoftEdgeElevationService")]
    public void Edge_registry_keys_stay_protected(string hive, string path)
    {
        AssertRejected(Load(Tweak(apply: Registry(hive: hive, path: path), revert: Registry(hive: hive, path: path, valueJson: "0"))), "protegid");
    }

    [Theory]
    [InlineData("edgeupdate")]
    [InlineData("edgeupdatem")]
    [InlineData("MicrosoftEdgeElevationService")]
    public void Edge_services_stay_protected(string name)
    {
        AssertRejected(Load(Tweak(apply: Service(name, "disabled"), revert: Service(name, "automatic"))), "protegid");
    }

    [Theory]
    [InlineData("Microsoft.MicrosoftEdge")]
    [InlineData("Microsoft.MicrosoftEdge.Stable")]
    [InlineData("Microsoft.Edge.GameAssist")]
    [InlineData("Microsoft.Win32WebViewHost")]
    public void Edge_packages_stay_protected(string package)
    {
        AssertRejected(Load(Tweak(apply: Appx(package, "remove"), revert: Appx(package, "reinstall"), category: "apps")), "protegid");
    }

    [Theory]
    [InlineData(@"\Microsoft\EdgeUpdate\", "Anything")]
    [InlineData(@"\", "MicrosoftEdgeUpdateTaskMachineUA")]
    public void Edge_update_tasks_stay_protected(string path, string name)
    {
        AssertRejected(Load(Tweak(apply: SchedTask(path, name, enabled: false), revert: SchedTask(path, name, enabled: true))), "protegid");
    }

    [Fact]
    public void Program_actions_round_trip_through_json_with_the_exact_names()
    {
        TweakAction action = new ProgramAction { Program = ManagedProgram.OneDrive, Operation = ProgramOperation.Uninstall };

        var json = JsonSerializer.Serialize(action, TweakJson.Options);

        Assert.Equal("""{"type":"program","program":"onedrive","operation":"uninstall"}""", json);
        Assert.Equal(action, JsonSerializer.Deserialize<TweakAction>(json, TweakJson.Options));
    }
}

/// <summary>Plano, dry run, aplicação, verificação, backup e "Desfazer" com o sistema falso.</summary>
public sealed class ProgramEngineTests : IDisposable
{
    private readonly FakeSystem _sys = new();

    public void Dispose() => _sys.Dispose();

    private static ExecutionOptions Confirmed => FakeSystem.Confirmed("test.program");

    [Theory]
    [InlineData("edge", ManagedProgram.Edge)]
    [InlineData("onedrive", ManagedProgram.OneDrive)]
    public void Planner_maps_each_operation_to_its_fixed_script(string name, ManagedProgram program)
    {
        var tweak = ProgramTweak(name);

        var apply = Assert.Single(OperationPlanner.Plan(tweak.Apply, allowDeleteKey: false).Operations);
        var revert = Assert.Single(OperationPlanner.Plan(tweak.Revert, allowDeleteKey: true).Operations);

        Assert.Equal(new ProgramUninstallOperation(program), apply);
        Assert.Equal(new ProgramReinstallOperation(program), revert);
        Assert.Equal($"program:{name}", apply.Target);
        Assert.Equal(PowerShellCalls.UninstallProgram(program).ToDisplayString(), apply.Command);
        Assert.Equal(PowerShellCalls.ReinstallProgram(program).ToDisplayString(), revert.Command);
    }

    [Fact]
    public void Planner_refuses_an_uninstall_coming_from_revert_or_a_reinstall_coming_from_apply()
    {
        var uninstall = new ProgramAction { Program = ManagedProgram.Edge, Operation = ProgramOperation.Uninstall };
        var reinstall = new ProgramAction { Program = ManagedProgram.Edge, Operation = ProgramOperation.Reinstall };

        Assert.False(OperationPlanner.Plan([uninstall], allowDeleteKey: true).IsValid);
        Assert.False(OperationPlanner.Plan([reinstall], allowDeleteKey: false).IsValid);
    }

    [Fact]
    public async Task Dry_run_shows_the_exact_edge_uninstall_command_and_changes_nothing()
    {
        _sys.Programs.Install(ManagedProgram.Edge);

        var result = await _sys.Engine.ApplyAsync(ProgramTweak("edge"), FakeSystem.Dry, Ct);

        Assert.Equal(ResultStatus.DryRun, result.Status);
        var command = Assert.Single(result.Operations).Operation.Command;
        Assert.Equal(PowerShellCalls.UninstallProgram(ManagedProgram.Edge).ToDisplayString(), command);
        Assert.StartsWith("powershell> ", command, StringComparison.Ordinal);
        Assert.Contains("'--uninstall', '--system-level', '--force-uninstall', '--verbose-logging'", command, StringComparison.Ordinal);
        Assert.Contains(@"Microsoft\Edge\Application", command, StringComparison.Ordinal);
        Assert.EndsWith("| args: {}", command, StringComparison.Ordinal);
        Assert.Empty(_sys.Mutations);
        Assert.Equal(0, _sys.Programs.UninstallCalls);
        Assert.Equal(0, _sys.BackupFileCount());
    }

    [Fact]
    public async Task Moderate_program_tweak_needs_confirmation()
    {
        _sys.Programs.Install(ManagedProgram.OneDrive);

        var result = await _sys.Engine.ApplyAsync(ProgramTweak("onedrive"), FakeSystem.Real, Ct);

        Assert.Equal(SkipReason.NotConfirmed, result.SkipReason);
        Assert.Contains(ManagedProgram.OneDrive, _sys.Programs.Installed);
    }

    [Theory]
    [InlineData("edge", ManagedProgram.Edge)]
    [InlineData("onedrive", ManagedProgram.OneDrive)]
    public async Task Apply_uninstalls_backs_up_first_verifies_and_revert_reinstalls(string name, ManagedProgram program)
    {
        _sys.Programs.Install(program);
        var tweak = ProgramTweak(name);

        var applied = await _sys.Engine.ApplyAsync(tweak, Confirmed, Ct);

        Assert.Equal(ResultStatus.Success, applied.Status);
        Assert.DoesNotContain(program, _sys.Programs.Installed);
        Assert.Equal([$"program uninstall {name}"], _sys.Mutations);
        var record = await _sys.Backups.GetActiveAsync("test.program", Ct);
        Assert.Equal(new ProgramBackup(program, WasInstalled: true), Assert.Single(record!.Items));
        Assert.Equal(TweakAppliedState.Applied, (await _sys.Detector.DetectAsync([tweak], Ct))["test.program"].Applied);

        var reverted = await _sys.Engine.RevertAsync(tweak, FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, reverted.Status);
        Assert.True(reverted.UsedBackup);
        Assert.Contains(program, _sys.Programs.Installed);
        Assert.Equal([$"program uninstall {name}", $"program reinstall {name}"], _sys.Mutations);
        Assert.Null(await _sys.Backups.GetActiveAsync("test.program", Ct));
        Assert.Equal(TweakAppliedState.NotApplied, (await _sys.Detector.DetectAsync([tweak], Ct))["test.program"].Applied);
    }

    [Fact]
    public async Task Program_that_is_not_installed_counts_as_already_removed()
    {
        var result = await _sys.Engine.ApplyAsync(ProgramTweak("onedrive"), Confirmed, Ct);

        Assert.Equal(ResultStatus.Skipped, result.Status);
        Assert.Equal(SkipReason.AlreadyApplied, result.SkipReason);
        Assert.Empty(_sys.Mutations);
        Assert.Equal(0, _sys.BackupFileCount());
    }

    [Fact]
    public async Task Edge_removal_refused_by_windows_changes_nothing_and_explains_why()
    {
        _sys.Programs.Install(ManagedProgram.Edge);
        _sys.Programs.FailRemove.Add(ManagedProgram.Edge);

        var result = await _sys.Engine.ApplyAsync(ProgramTweak("edge"), Confirmed, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.True(result.RolledBack);
        Assert.Equal(
            "O Windows não permitiu remover o Edge neste computador (a Microsoft só libera a remoção completa em algumas regiões). Nada foi alterado. Detalhes: código de saída do setup.exe 93.",
            result.Error);
        Assert.Contains(ManagedProgram.Edge, _sys.Programs.Installed);
        Assert.Empty(_sys.Mutations);

        // O rollback não tenta reinstalar um Edge que continua lá.
        Assert.Equal(0, _sys.Programs.ReinstallCalls);
        Assert.Null(await _sys.Backups.GetActiveAsync("test.program", Ct));
    }

    [Fact]
    public async Task Edge_removal_refused_is_explained_in_english_too()
    {
        using var english = TestCulture.Use("en-US");
        _sys.Programs.Install(ManagedProgram.Edge);
        _sys.Programs.FailRemove.Add(ManagedProgram.Edge);

        var result = await _sys.Engine.ApplyAsync(ProgramTweak("edge"), Confirmed, Ct);

        Assert.Equal(
            "Windows did not allow removing Edge on this computer (Microsoft only allows full removal in some regions). Nothing was changed. Details: setup.exe exit code 93.",
            result.Error);
    }

    [Fact]
    public async Task Revert_does_not_install_a_program_that_was_not_installed_before()
    {
        _sys.Programs.Install(ManagedProgram.Edge);
        await _sys.Engine.ApplyAsync(ProgramTweak("edge"), Confirmed, Ct);
        var record = (await _sys.Backups.GetActiveAsync("test.program", Ct))!;
        var notInstalledBefore = record with { Items = [new ProgramBackup(ManagedProgram.Edge, WasInstalled: false)] };

        var result = await _sys.Engine.RevertRecordAsync(notInstalledBefore, FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.Empty(result.Operations);
        Assert.Equal(0, _sys.Programs.ReinstallCalls);
        Assert.DoesNotContain(ManagedProgram.Edge, _sys.Programs.Installed);
    }

    [Fact]
    public async Task Revert_skips_the_download_when_the_program_is_already_back()
    {
        // Ex.: o Windows Update reinstalou o Edge depois da remoção.
        _sys.Programs.Install(ManagedProgram.Edge);
        await _sys.Engine.ApplyAsync(ProgramTweak("edge"), Confirmed, Ct);
        _sys.Programs.Install(ManagedProgram.Edge);

        var result = await _sys.Engine.RevertAsync(ProgramTweak("edge"), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.Equal(0, _sys.Programs.ReinstallCalls);
    }

    [Fact]
    public async Task Dry_run_of_the_revert_shows_the_reinstall_command()
    {
        _sys.Programs.Install(ManagedProgram.OneDrive);
        await _sys.Engine.ApplyAsync(ProgramTweak("onedrive"), Confirmed, Ct);

        var result = await _sys.Engine.RevertAsync(ProgramTweak("onedrive"), FakeSystem.Dry, Ct);

        Assert.Equal(ResultStatus.DryRun, result.Status);
        Assert.Equal(PowerShellCalls.ReinstallProgram(ManagedProgram.OneDrive).ToDisplayString(), Assert.Single(result.Operations).Operation.Command);
        Assert.Equal(0, _sys.Programs.ReinstallCalls);
    }

    [Fact]
    public async Task Failed_reinstall_keeps_the_backup_active_and_points_to_the_download_page()
    {
        _sys.Programs.Install(ManagedProgram.Edge);
        await _sys.Engine.ApplyAsync(ProgramTweak("edge"), Confirmed, Ct);
        _sys.Programs.FailReinstall.Add(ManagedProgram.Edge);

        var result = await _sys.Engine.RevertAsync(ProgramTweak("edge"), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Contains("https://www.microsoft.com/edge", result.Error, StringComparison.Ordinal);
        Assert.NotNull(await _sys.Backups.GetActiveAsync("test.program", Ct));
    }

    [Fact]
    public async Task Without_a_backup_the_catalog_revert_reinstalls_a_removed_program()
    {
        var result = await _sys.Engine.RevertAsync(ProgramTweak("onedrive"), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.False(result.UsedBackup);
        Assert.Contains(ManagedProgram.OneDrive, _sys.Programs.Installed);
    }

    [Fact]
    public async Task Engine_refuses_an_unknown_program_built_in_code()
    {
        var result = await _sys.Engine.ApplyAsync(
            Unvalidated(new ProgramAction { Program = (ManagedProgram)99, Operation = ProgramOperation.Uninstall }), FakeSystem.Real, Ct);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(0, _sys.Programs.UninstallCalls);
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task Program_backups_are_saved_with_the_exact_program_name()
    {
        var record = new BackupRecord
        {
            Id = "abc",
            TweakId = "apps.onedrive",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Items = [new ProgramBackup(ManagedProgram.OneDrive, WasInstalled: true)],
        };

        await _sys.Backups.SaveAsync(record, Ct);

        var text = File.ReadAllText(Assert.Single(Directory.GetFiles(_sys.BackupDirectory)));
        Assert.Contains("\"itemType\": \"program\"", text, StringComparison.Ordinal);
        Assert.Contains("\"program\": \"onedrive\"", text, StringComparison.Ordinal);
        var loaded = Assert.Single((await _sys.Backups.LoadAllAsync(Ct)).Records);
        Assert.Equal(new ProgramBackup(ManagedProgram.OneDrive, true), Assert.Single(loaded.Items));
    }

    [Fact]
    public async Task Backup_naming_an_unknown_program_is_treated_as_corrupt()
    {
        Directory.CreateDirectory(_sys.BackupDirectory);
        File.WriteAllText(
            Path.Combine(_sys.BackupDirectory, "20261002T120000000_abc_apps.chrome.json"),
            """{ "id": "abc", "tweakId": "apps.chrome", "createdAt": "2026-10-02T12:00:00+00:00", "items": [ { "itemType": "program", "program": "chrome", "wasInstalled": true } ] }""");

        var all = await _sys.Backups.LoadAllAsync(Ct);

        Assert.Empty(all.Records);
        Assert.Single(all.CorruptFiles);
    }
}

/// <summary>ProgramService real com um executor falso: leitura do JSON dos scripts e mensagens traduzidas.</summary>
public class ProgramServiceTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Theory]
    [InlineData(ManagedProgram.Edge, """{"installed":true}""", true)]
    [InlineData(ManagedProgram.OneDrive, """{"installed":false}""", false)]
    public async Task Installed_state_is_read_with_the_fixed_detection_script(ManagedProgram program, string json, bool expected)
    {
        var runner = FakeRunner.Json(json);

        Assert.Equal(expected, await new ProgramService(runner).IsInstalledAsync(program, Ct));
        Assert.Equal(PowerShellCalls.GetProgram(program).Script, Assert.Single(runner.Calls).Script);
    }

    [Fact]
    public void Scripts_take_no_arguments_and_each_pair_has_its_own_script()
    {
        var calls = Enum.GetValues<ManagedProgram>()
            .SelectMany(p => new[] { PowerShellCalls.GetProgram(p), PowerShellCalls.UninstallProgram(p), PowerShellCalls.ReinstallProgram(p) })
            .ToList();

        Assert.All(calls, c => Assert.Empty(c.Arguments));
        Assert.Equal(calls.Count, calls.Select(c => c.Script).Distinct().Count());
    }

    [Fact]
    public void Edge_scripts_never_touch_webview2_edge_update_or_the_registry()
    {
        foreach (var call in new[] { PowerShellCalls.GetProgram(ManagedProgram.Edge), PowerShellCalls.UninstallProgram(ManagedProgram.Edge), PowerShellCalls.ReinstallProgram(ManagedProgram.Edge) })
        {
            foreach (var forbidden in new[] { "EdgeWebView", "EdgeUpdate", "edgeupdate", "HKLM:", "HKCU:", "Registry::", "ItemProperty", "Remove-Item", "sc.exe", "Stop-Service" })
            {
                Assert.DoesNotContain(forbidden, call.Script, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Scripts_use_the_official_installers_with_the_agreed_arguments()
    {
        var oneDriveUninstall = PowerShellCalls.UninstallProgram(ManagedProgram.OneDrive).Script;
        var oneDriveReinstall = PowerShellCalls.ReinstallProgram(ManagedProgram.OneDrive).Script;
        var edgeReinstall = PowerShellCalls.ReinstallProgram(ManagedProgram.Edge).Script;

        Assert.Contains("Stop-Process -Name OneDrive -Force -ErrorAction SilentlyContinue", oneDriveUninstall, StringComparison.Ordinal);
        Assert.Contains("'/uninstall'", oneDriveUninstall, StringComparison.Ordinal);
        Assert.Contains("'SysWOW64'", oneDriveUninstall, StringComparison.Ordinal);
        Assert.Contains("'/silent'", oneDriveReinstall, StringComparison.Ordinal);
        Assert.Contains("install --id Microsoft.OneDrive -e --silent --accept-package-agreements --accept-source-agreements", oneDriveReinstall, StringComparison.Ordinal);
        Assert.Contains("install --id Microsoft.Edge -e --silent --accept-package-agreements --accept-source-agreements", edgeReinstall, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edge_removal_refused_by_setup_is_reported_in_the_interface_language_with_the_exit_code()
    {
        var service = new ProgramService(FakeRunner.Json("""{"found":true,"exitCode":93,"installed":true}"""));

        using (TestCulture.Use("pt-BR"))
        {
            var pt = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UninstallAsync(ManagedProgram.Edge, Ct));
            Assert.Equal(
                "O Windows não permitiu remover o Edge neste computador (a Microsoft só libera a remoção completa em algumas regiões). Nada foi alterado. Detalhes: código de saída do setup.exe 93.",
                pt.Message);
        }

        using (TestCulture.Use("en-US"))
        {
            var en = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UninstallAsync(ManagedProgram.Edge, Ct));
            Assert.Equal(
                "Windows did not allow removing Edge on this computer (Microsoft only allows full removal in some regions). Nothing was changed. Details: setup.exe exit code 93.",
                en.Message);
        }
    }

    [Fact]
    public async Task Edge_still_present_after_a_zero_exit_code_is_also_a_refusal()
    {
        var service = new ProgramService(FakeRunner.Json("""{"found":true,"exitCode":0,"installed":true}"""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UninstallAsync(ManagedProgram.Edge, Ct));

        Assert.EndsWith("código de saída do setup.exe 0.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_uninstalls_do_not_throw()
    {
        await new ProgramService(FakeRunner.Json("""{"found":true,"exitCode":0,"installed":false}""")).UninstallAsync(ManagedProgram.Edge, Ct);
        await new ProgramService(FakeRunner.Json("""{"found":true,"exitCode":0,"installed":false}""")).UninstallAsync(ManagedProgram.OneDrive, Ct);
    }

    [Theory]
    [InlineData(ManagedProgram.Edge, "O desinstalador do Microsoft Edge não foi encontrado neste computador. Nada foi alterado.")]
    [InlineData(ManagedProgram.OneDrive, "O desinstalador do OneDrive não foi encontrado neste computador. Nada foi alterado.")]
    public async Task Missing_uninstaller_is_reported(ManagedProgram program, string expected)
    {
        var service = new ProgramService(FakeRunner.Json("""{"found":false,"exitCode":null,"installed":true}"""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UninstallAsync(program, Ct));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public async Task OneDrive_uninstaller_failure_shows_the_exit_code()
    {
        var service = new ProgramService(FakeRunner.Json("""{"found":true,"exitCode":5,"installed":true}"""));

        using var english = TestCulture.Use("en-US");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UninstallAsync(ManagedProgram.OneDrive, Ct));

        Assert.Equal("The uninstaller for OneDrive ended with exit code 5.", error.Message);
    }

    [Theory]
    [InlineData(ManagedProgram.Edge, "https://www.microsoft.com/edge")]
    [InlineData(ManagedProgram.OneDrive, "https://www.microsoft.com/microsoft-365/onedrive/download")]
    public async Task Reinstall_without_any_installer_points_to_the_download_page(ManagedProgram program, string url)
    {
        var service = new ProgramService(FakeRunner.Json("""{"found":false,"exitCode":null,"installed":false}"""));

        using (TestCulture.Use("pt-BR"))
        {
            var pt = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReinstallAsync(program, Ct));
            Assert.Contains("winget não está disponível", pt.Message, StringComparison.Ordinal);
            Assert.EndsWith(url, pt.Message, StringComparison.Ordinal);
        }

        using (TestCulture.Use("en-US"))
        {
            var en = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReinstallAsync(program, Ct));
            Assert.Contains("winget is not available", en.Message, StringComparison.Ordinal);
            Assert.EndsWith(url, en.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Reinstall_failure_shows_the_exit_code_and_success_is_judged_by_the_program_being_back()
    {
        var failed = new ProgramService(FakeRunner.Json("""{"found":true,"exitCode":-1978335212,"installed":false}"""));
        var backAnyway = new ProgramService(FakeRunner.Json("""{"found":true,"exitCode":-1978335189,"installed":true}"""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.ReinstallAsync(ManagedProgram.Edge, Ct));
        await backAnyway.ReinstallAsync(ManagedProgram.Edge, Ct);

        Assert.Equal("O instalador do Microsoft Edge terminou com o código -1978335212.", error.Message);
    }

    [Fact]
    public async Task Reinstall_runs_on_the_runner_with_the_longer_timeout()
    {
        var quick = FakeRunner.Json("""{"found":true,"exitCode":0,"installed":false}""");
        var slow = FakeRunner.Json("""{"found":true,"exitCode":0,"installed":true}""");
        var service = new ProgramService(quick, slow);

        await service.ReinstallAsync(ManagedProgram.OneDrive, Ct);
        await service.UninstallAsync(ManagedProgram.OneDrive, Ct);

        Assert.Equal(PowerShellCalls.ReinstallProgram(ManagedProgram.OneDrive).Script, Assert.Single(slow.Calls).Script);
        Assert.Equal(PowerShellCalls.UninstallProgram(ManagedProgram.OneDrive).Script, Assert.Single(quick.Calls).Script);
        Assert.Equal(TimeSpan.FromMinutes(15), ProgramService.InstallTimeout);
    }

    [Fact]
    public async Task Garbage_output_is_reported_clearly()
    {
        using var english = TestCulture.Use("en-US");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ProgramService(FakeRunner.Json("nope")).IsInstalledAsync(ManagedProgram.Edge, Ct));

        Assert.StartsWith("Unexpected response from PowerShell when querying the program Microsoft Edge:", error.Message, StringComparison.Ordinal);
    }
}

public class ProgramDescriptionTests
{
    [Fact]
    public void Program_actions_are_described_in_both_languages()
    {
        var uninstall = new ProgramAction { Program = ManagedProgram.Edge, Operation = ProgramOperation.Uninstall };
        var reinstall = new ProgramAction { Program = ManagedProgram.OneDrive, Operation = ProgramOperation.Reinstall };

        using (TestCulture.Use("en-US"))
        {
            Assert.Equal("Program Microsoft Edge: uninstalls it with its own uninstaller", ActionDescriber.Describe(uninstall));
            Assert.Equal("Program OneDrive: reinstalls it (needs internet)", ActionDescriber.Describe(reinstall));
        }

        using (TestCulture.Use("pt-BR"))
        {
            Assert.Equal("Programa Microsoft Edge: desinstala pelo desinstalador oficial", ActionDescriber.Describe(uninstall));
            Assert.Equal("Programa OneDrive: reinstala (precisa de internet)", ActionDescriber.Describe(reinstall));
        }
    }
}
