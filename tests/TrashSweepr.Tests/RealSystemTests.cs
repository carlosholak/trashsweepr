using System.Reflection;
using System.Text.Json;
using TrashSweepr.Limpeza.Core.Abstractions;
using TrashSweepr.Limpeza.Core.Catalog;
using TrashSweepr.Limpeza.Core.Execution;
using TrashSweepr.Limpeza.Core.Models;
using TrashSweepr.Limpeza.Infrastructure;
using TrashSweepr.Tests.Helpers;
using Xunit.Abstractions;

namespace TrashSweepr.Tests;

/// <summary>
/// Valida os scripts sem executá-los: o parser do PowerShell apenas lê o texto e aponta erros de sintaxe.
/// Cobre também os scripts que alteram o sistema, que nenhum teste automático executa.
/// </summary>
public class PowerShellScriptSyntaxTests
{
    private static IEnumerable<PowerShellCall> AllCalls() =>
    [
        PowerShellCalls.GetService("Foo"),
        PowerShellCalls.SetServiceStartType("Foo", ServiceStartType.Disabled),
        PowerShellCalls.StopService("Foo"),
        PowerShellCalls.StartService("Foo"),
        PowerShellCalls.GetTask(@"\A\", "B"),
        PowerShellCalls.SetTaskEnabled(@"\A\", "B", true),
        PowerShellCalls.ListAppx(),
        PowerShellCalls.GetAppx("A.B"),
        PowerShellCalls.RemoveAppx("A.B"),
        PowerShellCalls.ReinstallAppx("A.B"),
        PowerShellCalls.GetRestoreProtection(),
        PowerShellCalls.CreateRestorePoint("descricao"),
    ];

    [Fact]
    public async Task Every_script_parses_without_syntax_errors()
    {
        var checker = new PowerShellCall(
            """
            $result = foreach ($script in $a.scripts) {
                $errors = $null
                [void][System.Management.Automation.Language.Parser]::ParseInput($script, [ref]$null, [ref]$errors)
                , @($errors | ForEach-Object { $_.Message + ' (linha ' + $_.Extent.StartLineNumber + ')' })
            }
            ConvertTo-Json -Compress -Depth 4 -InputObject @($result)
            """,
            new Dictionary<string, object?> { ["scripts"] = AllCalls().Select(c => WrapLikeTheRunner(c.Script)).ToArray() });

        var output = await new PowerShellRunner().RunAsync(checker, CancellationToken.None);

        Assert.True(output.Succeeded, output.StandardError);
        using var json = JsonDocument.Parse(output.StandardOutput);
        var all = AllCalls().ToList();
        Assert.Equal(all.Count, json.RootElement.GetArrayLength());
        for (var i = 0; i < all.Count; i++)
        {
            var errors = json.RootElement[i].ValueKind == JsonValueKind.Array
                ? json.RootElement[i].EnumerateArray().Select(e => e.GetString()).ToList()
                : [json.RootElement[i].GetString()];
            Assert.True(errors.Count == 0, $"Script {i} ({all[i].Script.Split('\n')[0].Trim()}...) tem erros de sintaxe: {string.Join("; ", errors)}");
        }
    }

    // Reproduz o texto exato que o PowerShellRunner executa (prelúdio + script + tratamento de erro).
    private static string WrapLikeTheRunner(string script)
    {
        var prelude = (string)typeof(PowerShellRunner).GetField("Prelude", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        var postlude = (string)typeof(PowerShellRunner).GetField("Postlude", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        return prelude + Environment.NewLine + script + postlude;
    }

    [Fact]
    public async Task The_syntax_checker_itself_detects_broken_scripts()
    {
        var checker = new PowerShellCall(
            """
            $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseInput($a.script, [ref]$null, [ref]$errors)
            ConvertTo-Json -Compress -InputObject @{ count = @($errors).Count }
            """,
            new Dictionary<string, object?> { ["script"] = "if ($true) { Get-Process " });

        var output = await new PowerShellRunner().RunAsync(checker, CancellationToken.None);

        using var json = JsonDocument.Parse(output.StandardOutput);
        Assert.True(json.RootElement.GetProperty("count").GetInt32() > 0);
    }
}

/// <summary>
/// Smoke test SOMENTE DE LEITURA no sistema real: detecta o estado do catálogo e roda um dry run.
/// Não escreve nada, não cria backup nem ponto de restauração. Rode com TRASHSWEEPR_READONLY_SMOKE=1.
/// </summary>
public sealed class ReadOnlyRealSystemSmokeTests(ITestOutputHelper output)
{
    [ReadOnlySmokeFact]
    public async Task Detects_the_state_of_the_shipped_catalog_on_this_machine()
    {
        var catalog = TweakCatalogLoader.LoadFromDirectory(RepoPaths.TweaksDirectory).Catalog;
        var temp = Path.Combine(Path.GetTempPath(), "trashsweepr-smoke-" + Guid.NewGuid().ToString("N"));
        using var runtime = LimpezaRuntime.Create(new AppPaths(temp));

        output.WriteLine($"Windows build {runtime.Windows.Build} ({runtime.Windows.DisplayVersion}), elevado: {runtime.Windows.IsElevated}");
        var states = await runtime.Detector.DetectAsync(catalog.Tweaks, CancellationToken.None);

        foreach (var tweak in catalog.Tweaks)
        {
            var state = states[tweak.Id];
            output.WriteLine($"{tweak.Id,-34} {state.Applied,-14} compat={state.Compatibility.Status}");
        }

        Assert.Equal(catalog.Tweaks.Count, states.Count);
        Assert.False(Directory.Exists(temp), "a detecção não deveria criar nada em disco");
    }

    [ReadOnlySmokeFact]
    public async Task Dry_run_of_the_whole_catalog_changes_nothing_and_lists_the_commands()
    {
        var catalog = TweakCatalogLoader.LoadFromDirectory(RepoPaths.TweaksDirectory).Catalog;
        var temp = Path.Combine(Path.GetTempPath(), "trashsweepr-smoke-" + Guid.NewGuid().ToString("N"));
        using var runtime = LimpezaRuntime.Create(new AppPaths(temp));

        var batch = await runtime.Engine.ApplyAllAsync(catalog.Tweaks, new ExecutionOptions { DryRun = true }, null, CancellationToken.None);

        output.WriteLine($"Proteção do sistema: {batch.ProtectionEnabled?.ToString() ?? "indeterminada"} | ponto de restauração: {batch.RestorePoint?.Outcome.ToString() ?? "não criado (dry run)"} {batch.RestorePoint?.Detail}");
        foreach (var result in batch.Results)
        {
            output.WriteLine($"{result.TweakId,-34} {result.Status,-8} {result.SkipReason} {result.Error}");
            foreach (var operation in result.Operations)
            {
                output.WriteLine($"    {operation.Operation.Command.Split('\n')[0]}");
            }
        }

        Assert.All(batch.Results, r => Assert.NotEqual(ResultStatus.Success, r.Status));
        Assert.All(batch.Results, r => Assert.NotEqual(ResultStatus.Failed, r.Status));
        Assert.False(Directory.Exists(Path.Combine(temp, "backups")), "dry run não pode criar backups");
    }
}

public sealed class ReadOnlySmokeFactAttribute : FactAttribute
{
    public ReadOnlySmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TRASHSWEEPR_READONLY_SMOKE") != "1")
        {
            Skip = "Smoke test somente de leitura no sistema real: defina TRASHSWEEPR_READONLY_SMOKE=1.";
        }
    }
}
