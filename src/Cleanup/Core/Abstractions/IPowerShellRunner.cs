using System.Text.Json;

namespace TrashSweepr.Cleanup.Core.Abstractions;

/// <summary>
/// Uma chamada de PowerShell: script fixo (constante do trashsweepr) + argumentos como dados.
/// Os argumentos nunca são interpolados no texto do script: o runner os entrega em JSON.
/// </summary>
public sealed record PowerShellCall(string Script, IReadOnlyDictionary<string, object?> Arguments)
{
    private static readonly JsonSerializerOptions DisplayOptions = new() { WriteIndented = false };

    public string ArgumentsJson => JsonSerializer.Serialize(Arguments, DisplayOptions);

    /// <summary>Texto exato do que será executado (script + argumentos), usado no dry run e no log.</summary>
    public string ToDisplayString() => $"powershell> {Script.Trim()} | args: {ArgumentsJson}";
}

public sealed record PowerShellResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IPowerShellRunner
{
    /// <summary>Executa a chamada. Um código de saída diferente de zero é retornado, não lançado.</summary>
    Task<PowerShellResult> RunAsync(PowerShellCall invocation, CancellationToken cancellationToken);
}
