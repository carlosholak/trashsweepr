namespace TrashSweepr;

/// <summary>Totais do painel lateral de uma pasta: por tipo, idade e mudança, maiores arquivos e recriáveis.</summary>
internal sealed class Analysis
{
    public required DiskNode Node { get; init; }
    public long[] ByCategory { get; } = new long[FileCategories.All.Length];
    public long[] ByAge { get; } = new long[Coloring.AgeBuckets.Length];
    public long[] ByChange { get; } = new long[Enum.GetValues<ChangeKind>().Length];
    public List<DiskNode> Top { get; } = [];
    public List<DiskNode> Recoverable { get; } = [];
    public long RecoverableSize { get; set; }
}

internal static class DiskAnalysis
{
    public const int TopFileCount = 200;

    /// <summary>Percorre a pasta uma vez só e calcula tudo o que o painel lateral mostra. Só lê a árvore.</summary>
    public static Analysis Analyze(DiskNode node, long now)
    {
        var a = new Analysis { Node = node };
        var heap = new PriorityQueue<DiskNode, long>();
        var stack = new Stack<(DiskNode Node, bool InRecoverable)>();
        stack.Push((node, node.Parent?.RecoverableAncestor() is not null));
        while (stack.Count > 0)
        {
            var (n, inRec) = stack.Pop();
            if (!n.IsDirectory)
            {
                a.ByCategory[(int)n.Category] += n.Size;
                a.ByAge[(int)Coloring.Age(n.LastWriteTicks, now)] += n.Size;
                a.ByChange[(int)Coloring.Change(n)] += n.Size;
                heap.Enqueue(n, n.Size);
                if (heap.Count > TopFileCount) heap.Dequeue();
                continue;
            }
            if (n.Recoverable is not null && !inRec)
            {
                a.Recoverable.Add(n);
                a.RecoverableSize += n.Size;
                inRec = true;
            }
            var kids = n.Children;
            if (kids is null) continue;
            for (int i = 0; i < kids.Count; i++) stack.Push((kids[i], inRec));
        }
        while (heap.Count > 0) a.Top.Add(heap.Dequeue());
        a.Top.Reverse();
        a.Recoverable.Sort(DiskNode.BySizeDescending);
        return a;
    }
}
