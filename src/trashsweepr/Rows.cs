using System.ComponentModel;
using System.Windows.Media;

namespace TrashSweepr;

public sealed record Crumb(DiskNode Node, string Label, bool ShowSeparator);

/// <summary>Linha que aponta para um item da árvore (permite entrar, selecionar e abrir o menu).</summary>
public interface INodeRow
{
    DiskNode? Node { get; }
}

static class Glyphs
{
    public const string Folder = "";
    public const string Link = "";
    public const string File = "";
    public static readonly Brush FolderBrush = FrozenPaint.Solid("#8A939D");
}

/// <summary>Linha da lista lateral (conteúdo da pasta ou maiores arquivos).</summary>
public sealed class Row : INodeRow
{
    const double BarMax = 46;

    public Row(DiskNode node, long reference, string? detail = null)
    {
        Node = node;
        double fraction = reference > 0 ? (double)node.Size / reference : 0;
        PercentText = Format.Percent(fraction);
        BarWidth = Math.Max(fraction > 0 ? 1 : 0, fraction * BarMax);
        SizeText = Format.Bytes(node.Size);

        if (node.IsDirectory)
        {
            Glyph = node.IsLink ? Glyphs.Link : Glyphs.Folder;
            GlyphBrush = node.Recoverable is not null ? Coloring.Recoverable : Glyphs.FolderBrush;
            Detail = detail ?? (node.IsLink ? "Atalho de pasta (não seguido)"
                : node.AccessDenied ? "Sem permissão de leitura"
                : node.Recoverable is { } r ? $"{Format.Files(node.FileCount)} · recriável ({r.Command})"
                : Format.Files(node.FileCount));
        }
        else
        {
            Glyph = Glyphs.File;
            GlyphBrush = FileCategories.Brush(node.Category);
            Detail = detail;
        }
        Name = node.Name;
    }

    public DiskNode? Node { get; }
    public string Name { get; }
    public string? Detail { get; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public string SizeText { get; }
    public string PercentText { get; }
    public double BarWidth { get; }
    public string Glyph { get; }
    public Brush GlyphBrush { get; }
}

/// <summary>Pasta recriável, com caixa para marcar antes de mandar para a Lixeira.</summary>
public sealed class RecoverRow : INodeRow, INotifyPropertyChanged
{
    bool _isChecked;

    public RecoverRow(DiskNode node, string basePath, long nowTicks)
    {
        Node = node;
        var rule = node.Recoverable!;
        Name = Format.RelativePath(basePath, node.FullPath, node.Name);
        SizeText = Format.Bytes(node.Size);
        Detail = $"{rule.Label} · {rule.Command}";
        if (rule.IsProject && node.ProjectActivityTicks > 0)
        {
            StaleDays = TimeSpan.FromTicks(nowTicks - node.ProjectActivityTicks).TotalDays;
            Detail += $" · projeto mexido {Format.Ago(new DateTime(node.ProjectActivityTicks, DateTimeKind.Utc), new DateTime(nowTicks, DateTimeKind.Utc))}";
        }
    }

    public DiskNode? Node { get; }
    public string Name { get; }
    public string Detail { get; }
    public string SizeText { get; }

    /// <summary>Dias desde a última modificação do projeto; null para caches globais.</summary>
    public double? StaleDays { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>O que mudou em relação à varredura de referência.</summary>
public sealed class ChangeRow : INodeRow
{
    static readonly Brush Up = FrozenPaint.Solid("#A4453C");
    static readonly Brush Down = FrozenPaint.Solid("#3F7D55");

    public ChangeRow(DiskNode node)
    {
        Node = node;
        Name = node.Name;
        Delta = Coloring.Delta(node);
        Detail = node.PreviousSize == DiskNode.Added
            ? $"novo · {Format.Bytes(node.Size)}"
            : $"era {Format.Bytes(node.PreviousSize)}, agora {Format.Bytes(node.Size)}";
        Glyph = node.IsDirectory ? Glyphs.Folder : Glyphs.File;
        GlyphBrush = node.IsDirectory ? Glyphs.FolderBrush : FileCategories.Brush(node.Category);
    }

    public ChangeRow(string removedName, long size, bool isDirectory)
    {
        Name = removedName;
        Delta = -size;
        Detail = $"removido · era {Format.Bytes(size)}";
        Glyph = isDirectory ? Glyphs.Folder : Glyphs.File;
        GlyphBrush = Glyphs.FolderBrush;
    }

    public DiskNode? Node { get; }
    public string Name { get; }
    public string Detail { get; }
    public long Delta { get; }
    public string DeltaText => Format.Signed(Delta);
    public Brush DeltaBrush => Delta >= 0 ? Up : Down;
    public string Glyph { get; }
    public Brush GlyphBrush { get; }
}

public sealed class LegendRow(Brush swatch, string label, long size, long total)
{
    public Brush Swatch { get; } = swatch;
    public string Label { get; } = label;
    public string SizeText { get; } = Format.Bytes(size);
    public double BarWidth { get; } = total > 0 ? Math.Max(1, 90.0 * size / total) : 0;
}
