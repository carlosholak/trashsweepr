using System.Text.Json;
using TrashSweepr.Limpeza.Core.Abstractions;
using TrashSweepr.Limpeza.Core.Backup;
using TrashSweepr.Limpeza.Core.Logging;
using TrashSweepr.Limpeza.Core.Models;
using TrashSweepr.Limpeza.Core.Operations;
using TrashSweepr.Tests.Helpers;
using static TrashSweepr.Tests.Helpers.CatalogJson;
using static TrashSweepr.Tests.Helpers.TestTweaks;

namespace TrashSweepr.Tests;

public sealed class BackupStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trashsweepr-store-" + Guid.NewGuid().ToString("N"));
    private readonly BackupStore _store;

    public BackupStoreTests() => _store = new BackupStore(_dir);

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static BackupRecord Record(string tweakId = "test.one", string id = "aaa111", int minute = 0, BackupStatus status = BackupStatus.Active) => new()
    {
        Id = id,
        TweakId = tweakId,
        CreatedAt = new DateTimeOffset(2026, 10, 2, 12, minute, 0, TimeSpan.Zero),
        Status = status,
        RequiresReboot = true,
        WindowsBuild = 22631,
        Items =
        [
            new RegistryValueBackup(RegistryHive.Hkcu, @"Software\X", "V", true, RegistryValueType.Dword, JsonSerializer.SerializeToElement(5), null),
            new RegistryValueBackup(RegistryHive.Hklm, @"SOFTWARE\Y", "", false, null, null, @"SOFTWARE\Y"),
            new ServiceBackup("FooSvc", true, ServiceStartType.AutomaticDelayed, true),
            new TaskBackup(@"\A\B\", "Job", true, false),
            new AppxBackup("Contoso.Sample", true, [new AppxPackageFile("Contoso.Sample_1.0_x64__abc", @"C:\WindowsApps\Contoso.Sample")]),
        ],
    };

    [Fact]
    public async Task Every_kind_of_item_survives_a_round_trip()
    {
        var original = Record();
        await _store.SaveAsync(original, Ct);

        var loaded = Assert.Single((await _store.LoadAllAsync(Ct)).Records);

        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(original.TweakId, loaded.TweakId);
        Assert.Equal(original.CreatedAt, loaded.CreatedAt);
        Assert.True(loaded.RequiresReboot);
        Assert.Equal(22631, loaded.WindowsBuild);
        Assert.Equal(5, loaded.Items.Count);

        var reg = Assert.IsType<RegistryValueBackup>(loaded.Items[0]);
        Assert.True(reg.Existed);
        Assert.Equal(RegistryValueType.Dword, reg.Type);
        Assert.Equal(5, reg.Value!.Value.GetInt32());

        var created = Assert.IsType<RegistryValueBackup>(loaded.Items[1]);
        Assert.False(created.Existed);
        Assert.Equal(@"SOFTWARE\Y", created.CreatedKeyRoot);
        Assert.Equal(string.Empty, created.Name);

        Assert.Equal(original.Items[2], loaded.Items[2]);
        Assert.Equal(original.Items[3], loaded.Items[3]);
        var appx = Assert.IsType<AppxBackup>(loaded.Items[4]);
        Assert.Equal("Contoso.Sample_1.0_x64__abc", Assert.Single(appx.Packages).FullName);
    }

    [Fact]
    public async Task Save_leaves_no_temporary_files_and_overwrites_the_same_record()
    {
        await _store.SaveAsync(Record(), Ct);
        await _store.SaveAsync(Record(status: BackupStatus.Reverted), Ct);

        Assert.Single(Directory.GetFiles(_dir));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Equal(BackupStatus.Reverted, Assert.Single((await _store.LoadAllAsync(Ct)).Records).Status);
    }

    [Fact]
    public async Task Active_backups_are_returned_newest_first_and_reverted_ones_are_hidden()
    {
        await _store.SaveAsync(Record("test.a", "a1", minute: 1), Ct);
        await _store.SaveAsync(Record("test.b", "b1", minute: 3), Ct);
        await _store.SaveAsync(Record("test.c", "c1", minute: 2), Ct);
        await _store.SaveAsync(Record("test.d", "d1", minute: 4, status: BackupStatus.Reverted), Ct);

        var active = await _store.GetAllActiveNewestFirstAsync(Ct);

        Assert.Equal(["test.b", "test.c", "test.a"], active.Select(r => r.TweakId));
        Assert.Null(await _store.GetActiveAsync("test.d", Ct));
        Assert.Equal("b1", (await _store.GetActiveAsync("test.b", Ct))!.Id);
    }

    [Fact]
    public async Task Latest_active_backup_wins_for_the_same_tweak()
    {
        await _store.SaveAsync(Record("test.a", "old", minute: 1, status: BackupStatus.Reverted), Ct);
        await _store.SaveAsync(Record("test.a", "new", minute: 5), Ct);

        Assert.Equal("new", (await _store.GetActiveAsync("test.a", Ct))!.Id);
    }

    [Fact]
    public async Task Corrupt_files_are_reported_and_do_not_hide_valid_backups()
    {
        await _store.SaveAsync(Record(), Ct);
        await File.WriteAllTextAsync(Path.Combine(_dir, "quebrado.json"), "{ nao é json");
        await File.WriteAllTextAsync(Path.Combine(_dir, "vazio.json"), "null");

        var loaded = await _store.LoadAllAsync(Ct);

        Assert.Single(loaded.Records);
        Assert.Equal(["quebrado.json", "vazio.json"], loaded.CorruptFiles.Order());
    }

    [Theory]
    [InlineData("""{ "id": "x1", "tweakId": "test.one", "createdAt": "2026-10-02T12:00:00+00:00", "items": null }""")]
    [InlineData("""{ "id": "x1", "tweakId": "test.one", "createdAt": "2026-10-02T12:00:00+00:00", "items": [ null ] }""")]
    [InlineData("""{ "id": null, "tweakId": "test.one", "createdAt": "2026-10-02T12:00:00+00:00", "items": [] }""")]
    [InlineData("""{ "id": "..\\x", "tweakId": "test.one", "createdAt": "2026-10-02T12:00:00+00:00", "items": [] }""")]
    [InlineData("""{ "id": "x1", "tweakId": null, "createdAt": "2026-10-02T12:00:00+00:00", "items": [] }""")]
    [InlineData("""{ "id": "x1", "tweakId": "Test One", "createdAt": "2026-10-02T12:00:00+00:00", "items": [] }""")]
    [InlineData("""{ "id": "x1", "tweakId": "test.one", "createdAt": "2026-10-02T12:00:00+00:00", "items": [ { "itemType": "desconhecido" } ] }""")]
    [InlineData("""{ "id": "x1", "tweakId": "test.one", "createdAt": "2026-10-02T12:00:00+00:00", "items": [ { "name": "FooSvc", "existed": true, "startType": "manual", "wasRunning": false } ] }""")]
    public async Task Malformed_records_are_reported_as_corrupt(string json)
    {
        await _store.SaveAsync(Record(), Ct);
        await File.WriteAllTextAsync(Path.Combine(_dir, "ruim.json"), json);

        var loaded = await _store.LoadAllAsync(Ct);

        Assert.Equal("aaa111", Assert.Single(loaded.Records).Id);
        Assert.Equal(["ruim.json"], loaded.CorruptFiles);
    }

    [Fact]
    public async Task A_hand_written_valid_record_is_accepted()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "manual.json"),
            """{ "id": "x1", "tweakId": "test.one", "createdAt": "2026-10-02T12:00:00+00:00", "items": [ { "itemType": "service", "name": "FooSvc", "existed": true, "startType": "manual", "wasRunning": false } ] }""");

        var loaded = await _store.LoadAllAsync(Ct);

        Assert.Empty(loaded.CorruptFiles);
        Assert.IsType<ServiceBackup>(Assert.Single(Assert.Single(loaded.Records).Items));
    }

    [Fact]
    public async Task Unsafe_ids_are_never_used_in_file_names()
    {
        var evil = Record() with { TweakId = @"..\..\Windows\evil" };
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(evil, Ct));

        var evilId = Record() with { Id = @"..\x" };
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(evilId, Ct));
    }

    [Fact]
    public async Task Missing_directory_means_no_backups()
    {
        var result = await _store.LoadAllAsync(Ct);

        Assert.Empty(result.Records);
        Assert.Empty(result.CorruptFiles);
    }

    [Fact]
    public async Task Concurrent_saves_do_not_corrupt_files()
    {
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => _store.SaveAsync(Record($"test.t{i}", $"id{i:D3}", minute: i), Ct)));

        var loaded = await _store.LoadAllAsync(Ct);

        Assert.Equal(20, loaded.Records.Count);
        Assert.Empty(loaded.CorruptFiles);
    }
}

public sealed class RestorePlannerTests
{
    private static BackupRecord RecordWith(params BackupItem[] items) => new()
    {
        Id = "abc123",
        TweakId = "test.restore",
        CreatedAt = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero),
        Items = items,
    };

    private static RegistryValueBackup Created(string path, string? root) =>
        new(RegistryHive.Hkcu, path, "V", false, null, null, root);

    private static RegistryValueBackup Existing(RegistryValueType type, object value, string path = @"Software\Limpeza\Test", string name = "V") =>
        new(RegistryHive.Hkcu, path, name, true, type, JsonSerializer.SerializeToElement(value), null);

    [Theory]
    [InlineData(@"Software\Limpeza\Test\A", @"Software\Limpeza\Test\A")]
    [InlineData(@"Software\Limpeza\Test\A", @"Software\Limpeza\Test")]
    [InlineData(@"Software\Limpeza\Test\A", @"software\limpeza")]
    public void Created_key_root_may_be_the_key_itself_or_an_ancestor(string path, string root)
    {
        var plan = RestorePlanner.Plan(RecordWith(Created(path, root)));

        Assert.True(plan.IsValid, string.Join(" ", plan.Errors));
        Assert.Contains(plan.Operations, o => o is RegistryPruneKeysOperation prune && prune.StopAtPath == root);
    }

    [Theory]
    [InlineData(@"Software\Limpeza\TestX\A", @"Software\Limpeza\Test")]
    [InlineData(@"Software\Limpeza\Test", @"Software\Limpeza\Test\A")]
    [InlineData(@"Software\Limpeza\Test", @"Software\Outra")]
    [InlineData(@"Software\Limpeza\Test", "")]
    [InlineData(@"Software\Limpeza\Test", @"Software\Limpeza\")]
    public void Created_key_root_that_is_not_a_segment_ancestor_is_rejected(string path, string root)
    {
        var plan = RestorePlanner.Plan(RecordWith(Created(path, root)));

        Assert.False(plan.IsValid);
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void Restored_values_are_validated_like_set_actions()
    {
        var plan = RestorePlanner.Plan(RecordWith(
            Existing(RegistryValueType.Sz, "linha1\nlinha2", name: "Controle"),
            Existing(RegistryValueType.Sz, new string('x', 5000), name: "Longo"),
            Existing(RegistryValueType.Sz, @"C:\evil.dll", path: @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", name: ""),
            Existing(RegistryValueType.Dword, "texto", name: "TipoErrado")));

        Assert.False(plan.IsValid);
        Assert.Empty(plan.Operations);
        Assert.Equal(4, plan.Errors.Count);
    }

    [Fact]
    public void Valid_restored_values_become_set_operations_with_their_original_type()
    {
        var plan = RestorePlanner.Plan(RecordWith(
            Existing(RegistryValueType.ExpandSz, @"%SystemRoot%\x", name: "E"),
            Existing(RegistryValueType.Qword, 9_000_000_000L, name: "Q"),
            Existing(RegistryValueType.Sz, string.Empty, path: @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", name: "")));

        Assert.True(plan.IsValid, string.Join(" ", plan.Errors));
        var sets = plan.Operations.Cast<RegistrySetOperation>().ToList();
        Assert.Equal([RegistryValueType.Sz, RegistryValueType.Qword, RegistryValueType.ExpandSz], sets.Select(s => s.Type));
        Assert.Equal(9_000_000_000L, sets[1].Value);
    }

    [Fact]
    public void Null_items_are_reported_instead_of_skipped()
    {
        var plan = RestorePlanner.Plan(RecordWith(null!, Existing(RegistryValueType.Dword, 1)));

        Assert.False(plan.IsValid);
    }
}

public sealed class OperationPlannerTests
{
    [Fact]
    public void Service_with_stop_generates_two_operations_in_order()
    {
        var tweak = ServiceTweak();

        var plan = OperationPlanner.Plan(tweak.Apply, allowDeleteKey: false);

        Assert.True(plan.IsValid);
        Assert.Equal([OperationKind.ServiceSetStartType, OperationKind.ServiceStop], plan.Operations.Select(o => o.Kind));
        Assert.Single(plan.Groups);
    }

    [Fact]
    public void Each_operation_kind_has_a_readable_command()
    {
        Assert.Equal(
            @"registry> SET HKLM\SOFTWARE\X\(Default) = ""abc"" (REG_SZ)",
            new RegistrySetOperation(RegistryHive.Hklm, @"SOFTWARE\X", "", RegistryValueType.Sz, "abc").Command);
        Assert.Equal(
            @"registry> SET HKCU\Software\X\V = 7 (REG_QWORD)",
            new RegistrySetOperation(RegistryHive.Hkcu, @"Software\X", "V", RegistryValueType.Qword, 7L).Command);
        Assert.Equal(@"registry> DELETE VALUE HKCU\Software\X\V", new RegistryDeleteValueOperation(RegistryHive.Hkcu, @"Software\X", "V").Command);
        Assert.Equal(@"registry> DELETE KEY (recursive) HKCU\Software\X", new RegistryDeleteKeyOperation(RegistryHive.Hkcu, @"Software\X").Command);
    }

    [Fact]
    public void Plan_collects_validation_errors_instead_of_throwing()
    {
        TweakAction[] actions =
        [
            new ServiceAction { Name = "wuauserv", StartType = ServiceStartType.Disabled },
            new AppxAction { Operation = AppxOperation.Remove, PackageName = "Contoso.Ok" },
        ];

        var plan = OperationPlanner.Plan(actions, allowDeleteKey: false);

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.StartsWith("[0]", StringComparison.Ordinal));
        Assert.Single(plan.Groups);
    }
}

public class PowerShellCallsTests
{
    private static readonly string[] Hostile =
    [
        "x'; Remove-Item C:\\ -Recurse; '",
        "$(calc.exe)",
        "`\"; calc; `\"",
        "Foo\r\nCalc",
    ];

    private static IEnumerable<PowerShellCall> AllCalls(string hostile) =>
    [
        PowerShellCalls.GetService(hostile),
        PowerShellCalls.SetServiceStartType(hostile, ServiceStartType.Disabled),
        PowerShellCalls.StopService(hostile),
        PowerShellCalls.StartService(hostile),
        PowerShellCalls.GetTask(hostile, hostile),
        PowerShellCalls.SetTaskEnabled(hostile, hostile, false),
        PowerShellCalls.GetAppx(hostile),
        PowerShellCalls.RemoveAppx(hostile),
        PowerShellCalls.ReinstallAppx(hostile),
        PowerShellCalls.CreateRestorePoint(hostile),
    ];

    [Fact]
    public void Arguments_never_become_part_of_the_script_text()
    {
        foreach (var hostile in Hostile)
        {
            foreach (var call in AllCalls(hostile))
            {
                Assert.DoesNotContain(hostile, call.Script, StringComparison.Ordinal);
                Assert.Contains(call.Arguments.Values, v => Equals(v, hostile));
            }
        }
    }

    [Fact]
    public void Scripts_are_identical_regardless_of_the_arguments()
    {
        Assert.Equal(PowerShellCalls.RemoveAppx("A.One").Script, PowerShellCalls.RemoveAppx("B.Two").Script);
        Assert.Equal(PowerShellCalls.SetServiceStartType("A", ServiceStartType.Manual).Script, PowerShellCalls.SetServiceStartType("B", ServiceStartType.Disabled).Script);
    }

    [Fact]
    public void App_removal_is_current_user_only()
    {
        var script = PowerShellCalls.RemoveAppx("Contoso.Sample").Script;

        Assert.DoesNotContain("-AllUsers", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Deprovision", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-AppxProvisionedPackage", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_string_has_script_and_json_arguments()
    {
        var display = PowerShellCalls.RemoveAppx("Contoso.Sample").ToDisplayString();

        Assert.StartsWith("powershell> ", display, StringComparison.Ordinal);
        Assert.Contains("Remove-AppxPackage", display, StringComparison.Ordinal);
        Assert.Contains("{\"name\":\"Contoso.Sample\"}", display, StringComparison.Ordinal);
    }
}

public sealed class FileOperationLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trashsweepr-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Writes_one_tab_separated_line_per_entry_with_all_fields()
    {
        var log = new FileOperationLog(_dir, new ManualTime());
        var when = new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeSpan.FromHours(-3));

        log.Write(new LogEntry(when, LogSeverity.Error, "privacy.advertising-id", "apply", @"registry> SET HKCU\X = 0", "failed", "Acesso negado"));
        log.Write(new LogEntry(when, LogSeverity.Info, null, "batch", null, "ok", null));

        var lines = File.ReadAllLines(log.CurrentFilePath);
        Assert.Equal(2, lines.Length);
        var fields = lines[0].Split('\t');
        Assert.Equal(7, fields.Length);
        Assert.Equal("2026-10-02T09:30:00.0000000-03:00", fields[0]);
        Assert.Equal("ERROR", fields[1]);
        Assert.Equal("privacy.advertising-id", fields[2]);
        Assert.Equal("apply", fields[3]);
        Assert.Equal(@"registry> SET HKCU\X = 0", fields[4]);
        Assert.Equal("failed", fields[5]);
        Assert.Equal("Acesso negado", fields[6]);
        Assert.Equal(["-", "-"], [lines[1].Split('\t')[2], lines[1].Split('\t')[4]]);
    }

    [Fact]
    public void Line_breaks_and_tabs_cannot_forge_new_log_lines()
    {
        var log = new FileOperationLog(_dir, new ManualTime());

        log.Write(new LogEntry(DateTimeOffset.UtcNow, LogSeverity.Info, "t.x", "apply", "a\nFORGED\tline\r\nmore", "ok", "e\r\nrror"));

        var lines = File.ReadAllLines(log.CurrentFilePath);
        Assert.Single(lines);
        Assert.Equal(7, lines[0].Split('\t').Length);
    }

    [Fact]
    public void Preserves_accents_as_utf8_without_bom()
    {
        var log = new FileOperationLog(_dir, new ManualTime());

        log.Write(new LogEntry(DateTimeOffset.UtcNow, LogSeverity.Info, "t.x", "apply", null, "concluído", "não encontrado"));

        var bytes = File.ReadAllBytes(log.CurrentFilePath);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("não encontrado", File.ReadAllText(log.CurrentFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_failure_never_throws()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "arquivo");
        File.WriteAllText(blocker, "x");
        var log = new FileOperationLog(Path.Combine(blocker, "subpasta"), new ManualTime()); // "pasta" dentro de um arquivo

        var exception = Record.Exception(() => log.Write(new LogEntry(DateTimeOffset.UtcNow, LogSeverity.Info, null, "x", null, "ok", null)));

        Assert.Null(exception);
    }
}
