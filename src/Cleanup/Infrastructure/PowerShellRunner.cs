using System.Diagnostics;
using System.Text;
using TrashSweepr.Cleanup.Core;
using TrashSweepr.Cleanup.Core.Abstractions;

namespace TrashSweepr.Cleanup.Infrastructure;

/// <summary>
/// Executa um script fixo no Windows PowerShell 5.1. Os argumentos viajam como JSON em Base64 numa variável
/// de ambiente e o script os lê em $a: nenhum valor jamais é concatenado ao texto do script, então não há
/// como um nome de pacote ou de serviço virar código.
/// </summary>
public sealed class PowerShellRunner(TimeSpan? timeout = null, string? executablePath = null) : IPowerShellRunner
{
    public const string ArgumentsVariable = "TRASHSWEEPR_ARGS";

    /// <summary>Tempo máximo de um script quando o construtor não recebe outro.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    // A leitura de $a fica dentro do try: um argumento ilegível sai pelo mesmo caminho de erro (stderr + exit 1).
    private const string Prelude =
        """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [Console]::OutputEncoding = $utf8
        try {
        $a = $utf8.GetString([System.Convert]::FromBase64String($env:TRASHSWEEPR_ARGS)) | ConvertFrom-Json
        """;

    private const string Postlude =
        """

        } catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        """;

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    // Caminho absoluto: não depende do PATH, que poderia apontar para outro powershell.exe.
    private readonly string _executable = executablePath
        ?? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public async Task<PowerShellResult> RunAsync(PowerShellCall invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var script = Prelude + Environment.NewLine + invocation.Script + Postlude;
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var encodedArguments = Convert.ToBase64String(Encoding.UTF8.GetBytes(invocation.ArgumentsJson));

        var startInfo = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");

        // Saída sempre como texto (nunca CLIXML serializado), que é o que o chamador lê.
        startInfo.ArgumentList.Add("-OutputFormat");
        startInfo.ArgumentList.Add("Text");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedScript);
        startInfo.Environment[ArgumentsVariable] = encodedArguments;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderr = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return new PowerShellResult(
                process.ExitCode,
                (await stdout.ConfigureAwait(false)).TrimStart('﻿').Trim(),
                (await stderr.ConfigureAwait(false)).TrimStart('﻿').Trim());
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException(CoreText.Format("PowerShell.Timeout", _timeout.TotalSeconds));
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // O processo já terminou entre a checagem e o Kill.
        }
    }
}
