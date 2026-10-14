using System.Text;

namespace TrashSweepr.Cleanup.Core.Logging;

public enum LogSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>Uma linha do log: data, tweak, comando, resultado e erro.</summary>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogSeverity Severity,
    string? TweakId,
    string Operation,
    string? Command,
    string Result,
    string? Error);

public interface IOperationLog
{
    /// <summary>Registra uma entrada. Nunca deve lançar: falha de log não pode interromper um tweak.</summary>
    void Write(LogEntry entry);
}

public sealed class NullOperationLog : IOperationLog
{
    public static NullOperationLog Instance { get; } = new();

    public void Write(LogEntry entry)
    {
    }
}

/// <summary>
/// Log em arquivo diário (limpeza-AAAAMMDD.log), uma entrada por linha, campos separados por TAB:
/// data/hora ISO 8601, severidade, tweak, operação, comando, resultado, erro.
/// </summary>
public sealed class FileOperationLog(string directory, TimeProvider? timeProvider = null) : IOperationLog
{
    private readonly object _gate = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string CurrentFilePath => Path.Combine(directory, $"limpeza-{_time.GetLocalNow():yyyyMMdd}.log");

    public void Write(LogEntry entry)
    {
        try
        {
            var line = string.Join(
                '\t',
                entry.Timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                entry.Severity.ToString().ToUpperInvariant(),
                Clean(entry.TweakId),
                Clean(entry.Operation),
                Clean(entry.Command),
                Clean(entry.Result),
                Clean(entry.Error));

            lock (_gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(CurrentFilePath, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Falha de log (disco cheio, sem permissão) não pode derrubar a execução do tweak.
        }
    }

    private static string Clean(string? value) =>
        string.IsNullOrEmpty(value)
            ? "-"
            : value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
