using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace TrashSweepr;

/// <summary>Nó de uma varredura gravada: só o necessário para comparar tamanhos.</summary>
public sealed class SnapNode
{
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public Dictionary<string, SnapNode>? Children { get; init; }
}

public sealed record SnapshotInfo(string FilePath, DateTime TakenUtc)
{
    public override string ToString() => $"{Format.Date(TakenUtc)}  ({Format.Ago(TakenUtc, DateTime.UtcNow)})";
}

/// <summary>
/// Cada varredura completa de uma pasta grava um registro compacto em %LOCALAPPDATA%\trashsweepr\snapshots
/// (pastas e arquivos de 1 MB ou mais), para mostrar depois o que cresceu.
/// </summary>
public static class Snapshots
{
    const long MinFileSize = 1L << 20;
    const int KeepPerFolder = 12;
    const string Magic = "MDS1";

    static readonly string Folder = ResolveFolder();

    /// <summary>
    /// O app se chamava "Mapa de Disco" e guardava os registros em %LOCALAPPDATA%\MapaDeDisco\snapshots. Na primeira
    /// execução com o nome novo, copia (não move) esses registros para não perder o histórico de "Mudanças".
    /// </summary>
    static string ResolveFolder()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string current = Path.Combine(local, "trashsweepr", "snapshots");
        string legacy = Path.Combine(local, "MapaDeDisco", "snapshots");
        try
        {
            if (!Directory.Exists(current) && Directory.Exists(legacy))
            {
                Directory.CreateDirectory(current);
                foreach (var file in Directory.GetFiles(legacy, "*.snap"))
                    File.Copy(file, Path.Combine(current, Path.GetFileName(file)), overwrite: false);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return current;
    }

    static string Key(string rootPath)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    /// <summary>Varreduras gravadas desta pasta, da mais recente para a mais antiga.</summary>
    public static List<SnapshotInfo> List(string rootPath)
    {
        if (!Directory.Exists(Folder)) return [];
        var list = new List<SnapshotInfo>();
        foreach (var file in Directory.GetFiles(Folder, Key(rootPath) + "_*.snap"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file).Split('_')[^1];
            if (long.TryParse(stamp, out long ticks))
                list.Add(new SnapshotInfo(file, new DateTime(ticks, DateTimeKind.Utc)));
        }
        return list.OrderByDescending(s => s.TakenUtc).ToList();
    }

    public static void Save(DiskNode root, DateTime takenUtc)
    {
        Directory.CreateDirectory(Folder);
        string key = Key(root.Name);
        string target = Path.Combine(Folder, $"{key}_{takenUtc.Ticks}.snap");
        string temp = target + ".tmp";
        using (var fs = File.Create(temp))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var w = new BinaryWriter(gz, Encoding.UTF8))
        {
            w.Write(Magic);
            w.Write(root.Name);
            w.Write(takenUtc.Ticks);
            Write(w, root);
        }
        File.Move(temp, target, overwrite: true);

        foreach (var old in List(root.Name).Skip(KeepPerFolder))
        {
            try { File.Delete(old.FilePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static bool Recorded(DiskNode n) => n.IsDirectory || n.Size >= MinFileSize;

    static void Write(BinaryWriter w, DiskNode n)
    {
        w.Write(n.Name);
        w.Write(n.IsDirectory);
        w.Write(n.Size);
        if (!n.IsDirectory) return;
        var kids = n.Children ?? [];
        int count = 0;
        for (int i = 0; i < kids.Count; i++) if (Recorded(kids[i])) count++;
        w.Write(count);
        for (int i = 0; i < kids.Count; i++) if (Recorded(kids[i])) Write(w, kids[i]);
    }

    public static SnapNode Load(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(gz, Encoding.UTF8);
        if (r.ReadString() != Magic) throw new InvalidDataException("Arquivo de varredura em formato desconhecido.");
        r.ReadString();
        r.ReadInt64();
        return Read(r);
    }

    static SnapNode Read(BinaryReader r)
    {
        string name = r.ReadString();
        bool isDir = r.ReadBoolean();
        long size = r.ReadInt64();
        if (!isDir) return new SnapNode { Name = name, Size = size };
        int count = r.ReadInt32();
        var kids = new Dictionary<string, SnapNode>(count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            var k = Read(r);
            kids[k.Name] = k;
        }
        return new SnapNode { Name = name, IsDirectory = true, Size = size, Children = kids };
    }

    /// <summary>Anota em cada nó o tamanho que tinha na varredura de referência.</summary>
    public static void Compare(DiskNode node, SnapNode? old, DateTime takenUtc)
    {
        if (old is null)
        {
            MarkAdded(node);
            return;
        }
        node.PreviousSize = old.Size;
        node.Removed = null;
        if (!node.IsDirectory || node.Children is not { } kids) return;

        long takenTicks = takenUtc.Ticks;
        var oldKids = old.Children;
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < kids.Count; i++)
        {
            var c = kids[i];
            if (oldKids is not null && oldKids.TryGetValue(c.Name, out var o) && o.IsDirectory == c.IsDirectory)
            {
                matched.Add(c.Name);
                Compare(c, o, takenUtc);
            }
            else if (c.IsDirectory)
            {
                MarkAdded(c);
            }
            else
            {
                // Arquivos abaixo de 1 MB não são gravados: se não foi modificado depois da
                // varredura de referência, ele já existia com o mesmo tamanho.
                c.PreviousSize = c.LastWriteTicks > takenTicks ? DiskNode.Added : c.Size;
                c.Removed = null;
            }
        }

        if (oldKids is not null && oldKids.Count > matched.Count)
        {
            var removed = new List<(string, long, bool)>();
            foreach (var o in oldKids.Values)
                if (!matched.Contains(o.Name)) removed.Add((o.Name, o.Size, o.IsDirectory));
            node.Removed = removed;
        }
    }

    static void MarkAdded(DiskNode n)
    {
        n.PreviousSize = DiskNode.Added;
        n.Removed = null;
        if (n.Children is { } kids)
            for (int i = 0; i < kids.Count; i++) MarkAdded(kids[i]);
    }

    public static void Clear(DiskNode n)
    {
        n.PreviousSize = DiskNode.NoBaseline;
        n.Removed = null;
        if (n.Children is { } kids)
            for (int i = 0; i < kids.Count; i++) Clear(kids[i]);
    }

    /// <summary>O nó gravado que corresponde a <paramref name="node"/>, descendo a partir da raiz.</summary>
    public static SnapNode? Find(SnapNode? baselineRoot, DiskNode node)
    {
        var chain = new List<DiskNode>();
        for (var n = node; n.Parent is not null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        var cur = baselineRoot;
        foreach (var n in chain)
        {
            if (cur?.Children is null || !cur.Children.TryGetValue(n.Name, out cur)) return null;
        }
        return cur;
    }
}
