using System.IO;

namespace TrashSweepr;

/// <summary>Um arquivo ou pasta da árvore varrida. Pastas somam o tamanho dos filhos.</summary>
public sealed class DiskNode
{
    public DiskNode(string name, DiskNode? parent, bool isDirectory)
    {
        Name = name;
        Parent = parent;
        IsDirectory = isDirectory;
    }

    public string Name { get; }
    public DiskNode? Parent { get; internal set; }
    public bool IsDirectory { get; }
    public long Size { get; internal set; }
    public int FileCount { get; internal set; }
    public Category Category { get; init; } = Category.Outros;

    /// <summary>Filhos ordenados do maior para o menor; null em arquivos.</summary>
    public List<DiskNode>? Children { get; internal set; }

    /// <summary>A pasta não pôde ser lida, ou só em parte (sem permissão ou erro de leitura).</summary>
    public bool AccessDenied { get; internal set; }

    /// <summary>Junção ou link simbólico: não é seguido para não contar nada duas vezes.</summary>
    public bool IsLink { get; init; }

    /// <summary>Bloco sintético que junta itens pequenos demais para aparecer no mapa.</summary>
    public bool IsAggregate { get; private init; }

    /// <summary>Última modificação (UTC). Em pastas, a mais recente entre os itens dentro dela.</summary>
    public long LastWriteTicks { get; internal set; }

    // ----- Comparação com uma varredura anterior (ver Snapshots) -----

    public const long NoBaseline = -1;
    public const long Added = -2;

    /// <summary>Tamanho na varredura de referência, <see cref="NoBaseline"/> sem referência, <see cref="Added"/> se não existia.</summary>
    public long PreviousSize { get; internal set; } = NoBaseline;

    /// <summary>Itens que existiam na varredura de referência e sumiram (só pastas e arquivos ≥ 1 MB são registrados).</summary>
    public List<(string Name, long Size, bool IsDirectory)>? Removed { get; internal set; }

    // ----- Conteúdo recriável (ver Recoverables) -----

    /// <summary>A pasta inteira é cache ou resultado de build que um comando recria.</summary>
    public RecoverableRule? Recoverable { get; internal set; }

    /// <summary>Última modificação de arquivo do projeto dono da pasta recriável, ignorando as pastas recriáveis.</summary>
    public long ProjectActivityTicks { get; internal set; }

    public string FullPath => Parent is null ? Name : Path.Combine(Parent.FullPath, Name);

    /// <summary>A própria pasta ou a mais próxima acima dela que seja recriável.</summary>
    public DiskNode? RecoverableAncestor()
    {
        for (var n = this; n is not null; n = n.Parent)
            if (n.Recoverable is not null) return n;
        return null;
    }

    public bool IsInside(DiskNode ancestor)
    {
        for (var n = this; n is not null; n = n.Parent)
            if (n == ancestor) return true;
        return false;
    }

    public static DiskNode Aggregate(DiskNode parent, long size, int count) =>
        new($"{Format.Count(count)} itens menores", parent, false) { Size = size, FileCount = count, IsAggregate = true };

    public static int BySizeDescending(DiskNode a, DiskNode b) => b.Size.CompareTo(a.Size);
}
