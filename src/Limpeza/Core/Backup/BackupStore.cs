using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrashSweepr.Limpeza.Core.Backup;

public sealed record BackupLoadResult(IReadOnlyList<BackupRecord> Records, IReadOnlyList<string> CorruptFiles);

/// <summary>
/// Guarda os backups em disco (um JSON por aplicação de tweak). A gravação é atômica: escreve em
/// arquivo temporário e renomeia, para que uma queda de energia nunca deixe um backup pela metade.
/// </summary>
public sealed partial class BackupStore(string directory) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    [GeneratedRegex(@"^[a-z0-9.\-]+$")]
    private static partial Regex SafeIdRegex();

    public string Directory { get; } = directory;

    public void Dispose() => _gate.Dispose();

    public async Task SaveAsync(BackupRecord record, CancellationToken ct)
    {
        if (!SafeIdRegex().IsMatch(record.TweakId) || !SafeIdRegex().IsMatch(record.Id))
        {
            throw new ArgumentException("Id de tweak ou de backup inválido para uso em nome de arquivo.", nameof(record));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = FilePathFor(record);
            var temp = path + ".tmp";

            // WriteThrough + Flush(true): o conteúdo chega ao disco antes da troca de nome; sem isso, uma queda de
            // energia logo após o Move poderia deixar o arquivo final vazio.
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, record, BackupJson.Options, ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Carrega todos os backups, do mais antigo para o mais recente. Arquivos corrompidos são listados à parte.</summary>
    public async Task<BackupLoadResult> LoadAllAsync(CancellationToken ct)
    {
        var records = new List<BackupRecord>();
        var corrupt = new List<string>();

        if (!System.IO.Directory.Exists(Directory))
        {
            return new BackupLoadResult(records, corrupt);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                try
                {
                    await using var stream = File.OpenRead(file);
                    var record = await JsonSerializer.DeserializeAsync<BackupRecord>(stream, BackupJson.Options, ct).ConfigureAwait(false);
                    if (!IsWellFormed(record))
                    {
                        corrupt.Add(Path.GetFileName(file));
                        continue;
                    }

                    records.Add(record);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // NotSupportedException: item sem 'itemType' (tipo abstrato), por exemplo.
                    corrupt.Add(Path.GetFileName(file));
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return new BackupLoadResult([.. records.OrderBy(r => r.CreatedAt)], corrupt);
    }

    public async Task<BackupRecord?> GetActiveAsync(string tweakId, CancellationToken ct)
    {
        var all = await LoadAllAsync(ct).ConfigureAwait(false);
        return all.Records.LastOrDefault(r => r.TweakId == tweakId && r.Status == BackupStatus.Active);
    }

    /// <summary>Backups ativos do mais recente para o mais antigo (a ordem correta para "Reverter tudo").</summary>
    public async Task<IReadOnlyList<BackupRecord>> GetAllActiveNewestFirstAsync(CancellationToken ct)
    {
        var all = await LoadAllAsync(ct).ConfigureAwait(false);
        return [.. all.Records.Where(r => r.Status == BackupStatus.Active).OrderByDescending(r => r.CreatedAt)];
    }

    // Campos obrigatórios podem chegar null de um arquivo editado à mão; um registro assim não é usado.
    private static bool IsWellFormed([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] BackupRecord? record) =>
        record is { Id: not null, TweakId: not null, Items: not null }
        && SafeIdRegex().IsMatch(record.Id)
        && SafeIdRegex().IsMatch(record.TweakId)
        && record.Items.All(item => item is not null);

    private string FilePathFor(BackupRecord record) =>
        Path.Combine(Directory, $"{record.CreatedAt.UtcDateTime:yyyyMMddTHHmmssfff}_{record.Id}_{record.TweakId}.json");
}
