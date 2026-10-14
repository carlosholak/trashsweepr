using System.Text.Json;
using Microsoft.Win32;
using TrashSweepr.Limpeza.Core.Abstractions;
using TrashSweepr.Limpeza.Core.Models;
using TrashSweepr.Limpeza.Infrastructure;
using TrashSweepr.Tests.Helpers;
using RegistryHive = TrashSweepr.Limpeza.Core.Models.RegistryHive;

namespace TrashSweepr.Tests;

/// <summary>Executor falso: devolve respostas prontas e guarda as chamadas recebidas.</summary>
internal sealed class FakeRunner(Func<PowerShellCall, PowerShellResult> respond) : IPowerShellRunner
{
    public List<PowerShellCall> Calls { get; } = [];

    public static FakeRunner Json(string json) => new(_ => new PowerShellResult(0, json, string.Empty));

    public static FakeRunner Failing(string stderr, int code = 1) => new(_ => new PowerShellResult(code, string.Empty, stderr));

    public Task<PowerShellResult> RunAsync(PowerShellCall invocation, CancellationToken cancellationToken)
    {
        Calls.Add(invocation);
        return Task.FromResult(respond(invocation));
    }
}

public class PowerShellBackedServiceTests
{
    private static CancellationToken Ct => CancellationToken.None;

    // ---------- Serviços ----------

    [Fact]
    public async Task Service_state_is_parsed_and_uses_the_core_script()
    {
        var runner = FakeRunner.Json("""{"exists":true,"startType":"Automatic","delayed":false,"running":true}""");

        var state = await new ServiceManager(runner).GetAsync("DiagTrack", Ct);

        Assert.Equal(new ServiceState(true, ServiceStartType.Automatic, true), state);
        Assert.Equal(PowerShellCalls.GetService("DiagTrack").Script, Assert.Single(runner.Calls).Script);
        Assert.Equal("DiagTrack", runner.Calls[0].Arguments["name"]);
    }

    [Theory]
    [InlineData("""{"exists":true,"startType":"Automatic","delayed":true,"running":false}""", ServiceStartType.AutomaticDelayed, false)]
    [InlineData("""{"exists":true,"startType":"Manual","delayed":false,"running":false}""", ServiceStartType.Manual, false)]
    [InlineData("""{"exists":true,"startType":"Disabled","delayed":false,"running":false}""", ServiceStartType.Disabled, false)]
    public async Task Service_start_types_are_mapped(string json, ServiceStartType expected, bool running)
    {
        var state = await new ServiceManager(FakeRunner.Json(json)).GetAsync("Foo", Ct);

        Assert.Equal(new ServiceState(true, expected, running), state);
    }

    [Fact]
    public async Task Missing_service_is_reported_as_missing()
    {
        var state = await new ServiceManager(FakeRunner.Json("""{"exists":false}""")).GetAsync("Foo", Ct);

        Assert.False(state.Exists);
    }

    [Theory]
    [InlineData("Boot")]
    [InlineData("System")]
    public async Task Start_types_that_cannot_be_restored_are_refused_instead_of_guessed(string startType)
    {
        var runner = FakeRunner.Json($$"""{"exists":true,"startType":"{{startType}}","delayed":false,"running":true}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ServiceManager(runner).GetAsync("Foo", Ct));

        Assert.Contains(startType, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_changes_pass_the_validated_values_as_arguments()
    {
        var runner = FakeRunner.Json(string.Empty);
        var manager = new ServiceManager(runner);

        await manager.SetStartTypeAsync("Foo", ServiceStartType.AutomaticDelayed, Ct);
        await manager.StopAsync("Foo", Ct);
        await manager.StartAsync("Foo", Ct);

        Assert.Equal("AutomaticDelayed", runner.Calls[0].Arguments["startType"]);
        Assert.Equal(PowerShellCalls.StopService("Foo").Script, runner.Calls[1].Script);
        Assert.Equal(PowerShellCalls.StartService("Foo").Script, runner.Calls[2].Script);
        Assert.All(runner.Calls, c => Assert.Equal("Foo", c.Arguments["name"]));
    }

    [Fact]
    public async Task PowerShell_failure_becomes_an_exception_with_its_message()
    {
        var runner = FakeRunner.Failing("Acesso negado ao serviço.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ServiceManager(runner).StopAsync("Foo", Ct));

        Assert.Equal("Acesso negado ao serviço.", ex.Message);
    }

    [Fact]
    public async Task Failure_without_a_message_still_explains_the_exit_code()
    {
        var runner = FakeRunner.Failing(string.Empty, code: 5);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ServiceManager(runner).StopAsync("Foo", Ct));

        Assert.Contains("5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Garbage_output_is_reported_clearly()
    {
        var runner = FakeRunner.Json("isto não é json");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ServiceManager(runner).GetAsync("Foo", Ct));

        Assert.Contains("Resposta inesperada", ex.Message, StringComparison.Ordinal);
    }

    // ---------- Tarefas ----------

    [Fact]
    public async Task Task_state_is_parsed()
    {
        var enabled = await new TaskSchedulerService(FakeRunner.Json("""{"exists":true,"enabled":true}""")).GetAsync(@"\A\", "B", Ct);
        var disabled = await new TaskSchedulerService(FakeRunner.Json("""{"exists":true,"enabled":false}""")).GetAsync(@"\A\", "B", Ct);
        var missing = await new TaskSchedulerService(FakeRunner.Json("""{"exists":false}""")).GetAsync(@"\A\", "B", Ct);

        Assert.Equal(new TaskState(true, true), enabled);
        Assert.Equal(new TaskState(true, false), disabled);
        Assert.False(missing.Exists);
    }

    [Fact]
    public async Task Task_change_sends_path_name_and_state_as_arguments()
    {
        var runner = FakeRunner.Json(string.Empty);

        await new TaskSchedulerService(runner).SetEnabledAsync(@"\A\", "B", false, Ct);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(@"\A\", call.Arguments["taskPath"]);
        Assert.Equal("B", call.Arguments["taskName"]);
        Assert.Equal(false, call.Arguments["enabled"]);
    }

    // ---------- Apps ----------

    [Theory]
    [InlineData("""["A.One","B.Two"]""", 2)]
    [InlineData("""["A.One"]""", 1)]
    [InlineData("\"A.One\"", 1)]
    [InlineData("[]", 0)]
    public async Task Installed_app_list_accepts_every_powershell_shape(string json, int expectedCount)
    {
        var names = await new AppxService(FakeRunner.Json(json)).ListInstalledNamesAsync(Ct);

        Assert.Equal(expectedCount, names.Count);
    }

    [Fact]
    public async Task Installed_app_names_ignore_case()
    {
        var names = await new AppxService(FakeRunner.Json("""["Clipchamp.Clipchamp"]""")).ListInstalledNamesAsync(Ct);

        Assert.Contains("clipchamp.clipchamp", names);
    }

    [Fact]
    public async Task App_state_lists_packages_even_when_powershell_returns_a_single_object()
    {
        var many = await new AppxService(FakeRunner.Json(
            """{"installed":true,"packages":[{"fullName":"A_1_x64__x","installLocation":"C:\\A"},{"fullName":"A_1_x86__x","installLocation":null}]}""")).GetAsync("A", Ct);
        var single = await new AppxService(FakeRunner.Json(
            """{"installed":false,"packages":{"fullName":"A_1_x64__x","installLocation":"C:\\A"}}""")).GetAsync("A", Ct);
        var none = await new AppxService(FakeRunner.Json("""{"installed":false,"packages":[]}""")).GetAsync("A", Ct);

        Assert.True(many.InstalledForCurrentUser);
        Assert.Equal(2, many.Packages.Count);
        Assert.Null(many.Packages[1].InstallLocation);
        Assert.False(single.InstalledForCurrentUser);
        Assert.Equal(@"C:\A", Assert.Single(single.Packages).InstallLocation);
        Assert.Empty(none.Packages);
    }

    [Fact]
    public async Task App_removal_and_reinstall_use_the_core_scripts()
    {
        var runner = FakeRunner.Json(string.Empty);
        var service = new AppxService(runner);

        await service.RemoveAsync("Contoso.Sample", Ct);
        await service.ReinstallAsync("Contoso.Sample", Ct);

        Assert.Equal(PowerShellCalls.RemoveAppx("x").Script, runner.Calls[0].Script);
        Assert.Equal(PowerShellCalls.ReinstallAppx("x").Script, runner.Calls[1].Script);
    }

    // ---------- Ponto de restauração ----------

    [Theory]
    [InlineData("""{"enabled":true,"reason":null}""", true)]
    [InlineData("""{"enabled":false,"reason":"noStorage"}""", false)]
    public async Task Protection_state_is_parsed(string json, bool expected)
    {
        Assert.Equal(expected, await new RestorePointService(FakeRunner.Json(json)).IsProtectionEnabledAsync(Ct));
    }

    [Fact]
    public async Task Restore_point_outcomes()
    {
        var created = await new RestorePointService(FakeRunner.Json("""{"created":true}""")).CreateAsync("desc", Ct);
        var throttled = await new RestorePointService(FakeRunner.Json("""{"created":false}""")).CreateAsync("desc", Ct);
        var failed = await new RestorePointService(FakeRunner.Failing("VSS indisponível")).CreateAsync("desc", Ct);

        Assert.Equal(RestorePointOutcome.Created, created.Outcome);
        Assert.Equal(RestorePointOutcome.Throttled, throttled.Outcome);
        Assert.Equal(RestorePointOutcome.Failed, failed.Outcome);
        Assert.Equal("VSS indisponível", failed.Detail);
    }

    [Fact]
    public async Task Restore_point_description_travels_as_an_argument()
    {
        var runner = FakeRunner.Json("""{"created":true}""");

        await new RestorePointService(runner).CreateAsync("trashsweepr'; calc; '", Ct);

        Assert.Equal("trashsweepr'; calc; '", Assert.Single(runner.Calls).Arguments["description"]);
        Assert.DoesNotContain("calc", runner.Calls[0].Script, StringComparison.Ordinal);
    }
}

/// <summary>
/// Usa o powershell.exe de verdade, mas só com scripts inofensivos que devolvem os argumentos recebidos:
/// provam que nenhum valor vira código e que erros, timeout e cancelamento são tratados.
/// </summary>
public class RealPowerShellRunnerTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static PowerShellCall Echo(object? value) => new(
        "ConvertTo-Json -Compress -InputObject @{ v = $a.value }",
        new Dictionary<string, object?> { ["value"] = value });

    public static TheoryData<string> HostileValues => new()
    {
        "x'; Write-Output pwned; '",
        "$(Get-Date)",
        "`\"; calc; `\"",
        "linha1\r\nlinha2",
        "ação não é ✓ 日本語",
        "{ \"a\": 1 }",
        "-DeleteMe",
        "%PATH% $env:USERNAME",
        @"\\servidor\share\""quoted""",
        "'",
        "\"",
        "<#comentario#>",
    };

    [Theory]
    [MemberData(nameof(HostileValues))]
    public async Task Arguments_come_back_exactly_as_sent_and_are_never_executed(string hostile)
    {
        var result = await new PowerShellRunner().RunAsync(Echo(hostile), Ct);

        Assert.True(result.Succeeded, result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(hostile, json.RootElement.GetProperty("v").GetString());

        // Se algum trecho tivesse sido executado como código, haveria saída extra além deste único JSON.
        Assert.Single(result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("{\"v\":", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Large_arguments_are_supported()
    {
        var big = new string('a', 20_000);

        var result = await new PowerShellRunner().RunAsync(Echo(big), Ct);

        Assert.True(result.Succeeded, result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(big.Length, json.RootElement.GetProperty("v").GetString()!.Length);
    }

    [Fact]
    public async Task Script_errors_return_a_nonzero_exit_code_and_the_message_in_utf8()
    {
        var call = new PowerShellCall("throw 'Pacote não encontrado'", new Dictionary<string, object?>());

        var result = await new PowerShellRunner().RunAsync(call, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Pacote não encontrado", result.StandardError);
    }

    [Fact]
    public async Task Script_text_with_accents_runs_correctly()
    {
        var call = new PowerShellCall("ConvertTo-Json -Compress -InputObject @{ v = 'ação' }", new Dictionary<string, object?>());

        var result = await new PowerShellRunner().RunAsync(call, Ct);

        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("ação", json.RootElement.GetProperty("v").GetString());
    }

    [Fact]
    public async Task Hanging_scripts_are_killed_after_the_timeout()
    {
        var runner = new PowerShellRunner(timeout: TimeSpan.FromSeconds(2));
        var call = new PowerShellCall("Start-Sleep -Seconds 60", new Dictionary<string, object?>());
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(call, Ct));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"demorou {clock.Elapsed}");
    }

    [Fact]
    public async Task Cancellation_stops_the_process()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var call = new PowerShellCall("Start-Sleep -Seconds 60", new Dictionary<string, object?>());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PowerShellRunner().RunAsync(call, cts.Token));
    }

    [Fact]
    public async Task Windows_info_reads_the_real_build()
    {
        var info = new WindowsInfo();

        Assert.True(info.Build >= 19041);
        Assert.False(string.IsNullOrWhiteSpace(info.DisplayVersion));
        await Task.CompletedTask;
    }
}

/// <summary>Só roda com TRASHSWEEPR_INTEGRATION=1: escreve numa chave temporária em HKCU\Software\LimpezaTests.</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TRASHSWEEPR_INTEGRATION") != "1")
        {
            Skip = "Teste de integração: defina TRASHSWEEPR_INTEGRATION=1 (usa uma chave temporária em HKCU\\Software\\LimpezaTests).";
        }
    }
}

public sealed class RegistryServiceIntegrationTests : IDisposable
{
    private readonly string _root = @"Software\LimpezaTests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryService _registry = new();

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\LimpezaTests", throwOnMissingSubKey: false);

    [IntegrationFact]
    public void Round_trips_every_supported_type()
    {
        _registry.SetValue(RegistryHive.Hkcu, _root, "D", RegistryValueType.Dword, -1);
        _registry.SetValue(RegistryHive.Hkcu, _root, "Q", RegistryValueType.Qword, 9_000_000_000L);
        _registry.SetValue(RegistryHive.Hkcu, _root, "S", RegistryValueType.Sz, "texto ação");
        _registry.SetValue(RegistryHive.Hkcu, _root, "E", RegistryValueType.ExpandSz, "%SystemRoot%\\x");

        Assert.Equal(new RegistryValueSnapshot(true, RegistryValueType.Dword, -1), _registry.ReadValue(RegistryHive.Hkcu, _root, "D"));
        Assert.Equal(new RegistryValueSnapshot(true, RegistryValueType.Qword, 9_000_000_000L), _registry.ReadValue(RegistryHive.Hkcu, _root, "Q"));
        Assert.Equal(new RegistryValueSnapshot(true, RegistryValueType.Sz, "texto ação"), _registry.ReadValue(RegistryHive.Hkcu, _root, "S"));
        // O texto original, sem expandir a variável de ambiente.
        Assert.Equal(new RegistryValueSnapshot(true, RegistryValueType.ExpandSz, "%SystemRoot%\\x"), _registry.ReadValue(RegistryHive.Hkcu, _root, "E"));
    }

    [IntegrationFact]
    public void Default_value_is_distinguished_from_a_missing_one()
    {
        Assert.False(_registry.ReadValue(RegistryHive.Hkcu, _root, "").Exists);

        _registry.SetValue(RegistryHive.Hkcu, _root, "", RegistryValueType.Sz, "");

        var snapshot = _registry.ReadValue(RegistryHive.Hkcu, _root, "");
        Assert.True(snapshot.Exists);
        Assert.Equal(string.Empty, snapshot.Value);
    }

    [IntegrationFact]
    public void Unsupported_types_are_flagged_so_the_backup_refuses_them()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_root))
        {
            key.SetValue("B", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
        }

        var snapshot = _registry.ReadValue(RegistryHive.Hkcu, _root, "B");

        Assert.True(snapshot.Exists);
        Assert.Equal("Binary", snapshot.UnsupportedKind);
    }

    [IntegrationFact]
    public void Delete_value_is_idempotent_and_keys_prune_only_when_empty()
    {
        var leaf = _root + @"\A\B";
        _registry.SetValue(RegistryHive.Hkcu, leaf, "V", RegistryValueType.Dword, 1);

        Assert.False(_registry.DeleteKeyIfEmpty(RegistryHive.Hkcu, leaf)); // ainda tem valor
        _registry.DeleteValue(RegistryHive.Hkcu, leaf, "V");
        _registry.DeleteValue(RegistryHive.Hkcu, leaf, "V"); // sem erro na segunda vez
        Assert.True(_registry.DeleteKeyIfEmpty(RegistryHive.Hkcu, leaf));
        Assert.False(_registry.KeyExists(RegistryHive.Hkcu, leaf));
        Assert.False(_registry.DeleteKeyIfEmpty(RegistryHive.Hkcu, _root), "a raiz tem a subchave A");
        Assert.True(_registry.DeleteKeyIfEmpty(RegistryHive.Hkcu, _root + @"\A"));
    }

    [IntegrationFact]
    public void Delete_key_tree_removes_everything_below_and_ignores_missing_keys()
    {
        _registry.SetValue(RegistryHive.Hkcu, _root + @"\X\Y", "V", RegistryValueType.Dword, 1);

        _registry.DeleteKeyTree(RegistryHive.Hkcu, _root + @"\X");
        _registry.DeleteKeyTree(RegistryHive.Hkcu, _root + @"\X");

        Assert.False(_registry.KeyExists(RegistryHive.Hkcu, _root + @"\X\Y"));
    }

    [IntegrationFact]
    public void Missing_keys_read_as_missing_values()
    {
        Assert.False(_registry.KeyExists(RegistryHive.Hkcu, _root + @"\nao-existe"));
        Assert.False(_registry.ReadValue(RegistryHive.Hkcu, _root + @"\nao-existe", "V").Exists);
    }
}
