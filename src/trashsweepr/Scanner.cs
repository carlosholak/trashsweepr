using System.IO;

namespace TrashSweepr;

/// <summary>
/// Varre uma pasta recursivamente. Só lê metadados — nunca abre, altera ou apaga arquivos.
/// Os contadores podem ser lidos de outra thread enquanto a varredura roda.
/// </summary>
public sealed class Scanner
{
    // Sem IgnoreInaccessible: a pasta sem permissão precisa dar erro para ser marcada (senão parece vazia).
    // A enumeração é de um nível só, então o erro é sempre da própria pasta, não de uma subpasta.
    static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: arquivo só na nuvem (OneDrive), baixado ao ser aberto.</summary>
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;
    const FileAttributes CloudOnly = RecallOnDataAccess | FileAttributes.Offline;

    long _files;
    long _bytes;
    int _denied;
    volatile string _currentPath = "";

    public long Files => Interlocked.Read(ref _files);
    public long Bytes => Interlocked.Read(ref _bytes);
    public int DeniedFolders => Volatile.Read(ref _denied);
    public string CurrentPath => _currentPath;

    public DiskNode Scan(string path, CancellationToken ct)
    {
        var root = new DiskNode(path, null, true);
        ScanDirectory(root, new DirectoryInfo(path), 0, ct);
        return root;
    }

    void ScanDirectory(DiskNode node, DirectoryInfo dir, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _currentPath = dir.FullName;

        var children = new List<DiskNode>();
        var subdirs = new List<(DiskNode Node, DirectoryInfo Dir)>();
        try
        {
            foreach (var entry in dir.EnumerateFileSystemInfos("*", Options))
            {
                if (entry is DirectoryInfo d)
                {
                    bool link = d.Attributes.HasFlag(FileAttributes.ReparsePoint);
                    var child = new DiskNode(d.Name, node, true) { IsLink = link, LastWriteTicks = d.LastWriteTimeUtc.Ticks };
                    children.Add(child);
                    if (link) child.Children = [];
                    else subdirs.Add((child, d));
                }
                else if (entry is FileInfo f)
                {
                    // Arquivo só na nuvem não ocupa disco: conta como arquivo, mas com 0 bytes.
                    long length = (f.Attributes & CloudOnly) != 0 ? 0 : f.Length;
                    children.Add(new DiskNode(f.Name, node, false)
                    {
                        Size = length,
                        FileCount = 1,
                        Category = FileCategories.Of(f.Name),
                        LastWriteTicks = f.LastWriteTimeUtc.Ticks,
                    });
                    Interlocked.Increment(ref _files);
                    Interlocked.Add(ref _bytes, length);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // A pasta sumiu durante a varredura: fica vazia, não é falta de permissão.
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Se o erro vier no meio da listagem, o que já foi lido fica: cada item contado está em
            // "children", então os totais da pasta (somados abaixo) batem com os contadores globais.
            node.AccessDenied = true;
            Interlocked.Increment(ref _denied);
        }

        // Paraleliza só os primeiros níveis: é onde o volume se divide; mais fundo só gera disputa.
        if (depth < 2 && subdirs.Count > 1)
        {
            var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };
            Parallel.ForEach(subdirs, po, s => ScanDirectory(s.Node, s.Dir, depth + 1, ct));
        }
        else
        {
            foreach (var s in subdirs) ScanDirectory(s.Node, s.Dir, depth + 1, ct);
        }

        long size = 0;
        int count = 0;
        long lastWrite = node.LastWriteTicks;
        foreach (var c in children)
        {
            size += c.Size;
            count += c.FileCount;
            if (c.LastWriteTicks > lastWrite) lastWrite = c.LastWriteTicks;
        }
        node.LastWriteTicks = lastWrite;
        children.Sort(DiskNode.BySizeDescending);
        node.Children = children;
        node.Size = size;
        node.FileCount = count;
    }

    public static bool IsCancellation(Exception e) => e switch
    {
        OperationCanceledException => true,
        AggregateException ae => ae.Flatten().InnerExceptions.All(IsCancellation),
        _ => false,
    };
}
