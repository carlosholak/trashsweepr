using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TrashSweepr;

/// <summary>
/// Desenha os filhos de <see cref="Root"/> como treemap aninhado. O mapa fica num visual próprio e
/// só é refeito quando muda a pasta ou o tamanho; hover e seleção vão numa camada por cima.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    const double HeaderHeight = 17;     // faixa do nome da pasta no topo do bloco
    const double MinHeaderWidth = 48;   // bloco de pasta menor que isto não ganha cabeçalho
    const double MinHeaderHeight = 36;
    const double MinLabelWidth = 56;    // bloco de arquivo menor que isto fica sem nome
    const double MinLabelHeight = 20;
    const double MinTwoLineHeight = 38; // a partir daqui cabe também o tamanho, numa segunda linha
    const double SecondLineOffset = 19;
    const double MinLabelSpace = 8;     // largura mínima para valer a pena desenhar texto
    const double LabelFontSize = 11.5;
    const double MinTileArea = 24; // px²: abaixo disso o item entra no bloco "itens menores"

    readonly DrawingVisual _map = new();
    readonly DrawingVisual _overlay = new();
    readonly VisualCollection _visuals;
    readonly List<(Rect Rect, DiskNode Node)> _tiles = [];
    readonly Typeface _regular = new("Segoe UI");
    readonly Typeface _semibold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    // Blocos "itens menores" por pasta: reaproveitados entre redesenhos para a seleção não se perder
    // (trocar o modo de cor redesenha tudo). Valem enquanto a pasta aberta não muda.
    readonly Dictionary<DiskNode, DiskNode> _aggregates = [];

    DiskNode? _root;
    DiskNode? _hover;
    DiskNode? _selected;
    ColorMode _mode;
    long _nowTicks;
    double _pixelsPerDip = 1;

    public TreemapControl()
    {
        _visuals = new VisualCollection(this) { _map, _overlay };
        RenderOptions.SetEdgeMode(_map, EdgeMode.Aliased);
        ClipToBounds = true;
    }

    public event Action<DiskNode?>? HoverChanged;
    public event Action<DiskNode>? ItemClicked;
    public event Action<DiskNode>? ItemDoubleClicked;

    public DiskNode? Root
    {
        get => _root;
        set
        {
            _root = value;
            _hover = null;
            _selected = null;
            _aggregates.Clear();
            Redraw();
        }
    }

    public ColorMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Redraw();
        }
    }

    public DiskNode? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            DrawOverlay();
        }
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Redraw();
    }

    public void Redraw()
    {
        _tiles.Clear();
        _nowTicks = DateTime.UtcNow.Ticks;
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var area = new Rect(RenderSize);
        using (var dc = _map.RenderOpen())
        {
            dc.DrawRectangle(Palette.Canvas, null, area);
            if (_root is { Children.Count: > 0, Size: > 0 } && area.Width > 0 && area.Height > 0)
                DrawChildren(dc, _root, area, 0, _root.RecoverableAncestor() is not null);
        }
        DrawOverlay();
    }

    void DrawChildren(DrawingContext dc, DiskNode parent, Rect area, int depth, bool inRecoverable)
    {
        double scale = area.Width * area.Height / parent.Size;
        var items = new List<DiskNode>();
        long restSize = 0;
        int restCount = 0;
        foreach (var child in parent.Children!)
        {
            if (child.Size <= 0) continue;
            if (child.Size * scale >= MinTileArea) items.Add(child);
            else
            {
                restSize += child.Size;
                restCount++;
            }
        }
        if (restCount == 1)
        {
            // Um item só não precisa de bloco agregado; ele aparece pequeno mesmo.
            items.Add(parent.Children!.Last(c => c.Size > 0));
        }
        else if (restCount > 1)
        {
            items.Add(AggregateFor(parent, restSize, restCount));
            items.Sort(DiskNode.BySizeDescending);
        }

        var laid = new List<(Rect Rect, DiskNode Node)>(items.Count);
        Squarify.Layout(items, area, laid);
        foreach (var (rect, node) in laid)
            DrawTile(dc, node, rect, depth, inRecoverable);
    }

    /// <summary>O mesmo bloco agregado da última vez, se ainda junta os mesmos itens; senão um novo.</summary>
    DiskNode AggregateFor(DiskNode parent, long size, int count)
    {
        if (_aggregates.TryGetValue(parent, out var cached) && cached.Size == size && cached.FileCount == count)
            return cached;
        var aggregate = DiskNode.Aggregate(parent, size, count);
        _aggregates[parent] = aggregate;
        return aggregate;
    }

    void DrawTile(DrawingContext dc, DiskNode node, Rect r, int depth, bool inRecoverable)
    {
        if (r.Width < 1 || r.Height < 1) return;
        _tiles.Add((r, node));
        bool recoverable = inRecoverable || node.Recoverable is not null;

        if (node.IsDirectory && node.Children is { Count: > 0 })
        {
            bool highlight = _mode == ColorMode.Recriavel && node.Recoverable is not null;
            if (highlight) dc.DrawRectangle(Coloring.RecoverableFolder, Coloring.RecoverableFolderBorder, r);
            else dc.DrawRectangle(depth % 2 == 0 ? Palette.Folder : Palette.FolderAlt, Palette.FolderBorder, r);
            Rect inner;
            if (r.Width >= MinHeaderWidth && r.Height >= MinHeaderHeight)
            {
                string header = highlight
                    ? $"{node.Name}   {Format.Bytes(node.Size)}   · {Localization.Loc.Get("Disk.Map.Recoverable")}"
                    : $"{node.Name}   {Format.Bytes(node.Size)}";
                DrawLabel(dc, header, r.X + 4, r.Y + 1.5, r.Width - 8, _semibold, Palette.FolderText);
                inner = new Rect(r.X + 2, r.Y + HeaderHeight, r.Width - 4, r.Height - HeaderHeight - 2);
            }
            else
            {
                inner = new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2));
            }
            if (inner.Width >= 3 && inner.Height >= 3)
                DrawChildren(dc, node, inner, depth + 1, recoverable);
            return;
        }

        var fill = Coloring.Fill(node, _mode, recoverable, _nowTicks, Palette.Aggregate, Palette.FolderAlt);
        dc.DrawRectangle(fill, Palette.TileBorder, r);
        if (r.Width >= MinLabelWidth && r.Height >= MinLabelHeight)
        {
            var text = Coloring.TextFor(fill);
            DrawLabel(dc, node.Name, r.X + 4, r.Y + 3, r.Width - 8, _regular, text);
            if (r.Height >= MinTwoLineHeight)
                DrawLabel(dc, Format.Bytes(node.Size), r.X + 4, r.Y + SecondLineOffset, r.Width - 8, _regular, text);
        }
    }

    void DrawLabel(DrawingContext dc, string text, double x, double y, double maxWidth, Typeface face, Brush brush)
    {
        if (maxWidth < MinLabelSpace) return;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, LabelFontSize, brush,
            _pixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(ft, new Point(x, y));
    }

    void DrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        if (_selected is not null && FindRect(_selected) is Rect sel)
            dc.DrawRectangle(null, Palette.SelectedPen, Deflate(sel, 1));
        if (_hover is not null && _hover != _selected && FindRect(_hover) is Rect hov)
            dc.DrawRectangle(Palette.HoverFill, Palette.HoverPen, Deflate(hov, 1));
    }

    static Rect Deflate(Rect r, double d) =>
        new(r.X + d, r.Y + d, Math.Max(0, r.Width - 2 * d), Math.Max(0, r.Height - 2 * d));

    Rect? FindRect(DiskNode node)
    {
        foreach (var t in _tiles)
            if (t.Node == node) return t.Rect;
        return null;
    }

    /// <summary>O item mais profundo sob o ponto (filhos são desenhados depois dos pais).</summary>
    public DiskNode? HitNode(Point p)
    {
        for (int i = _tiles.Count - 1; i >= 0; i--)
            if (_tiles[i].Rect.Contains(p)) return _tiles[i].Node;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var node = HitNode(e.GetPosition(this));
        if (node == _hover) return;
        _hover = node;
        DrawOverlay();
        HoverChanged?.Invoke(node);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is null) return;
        _hover = null;
        DrawOverlay();
        HoverChanged?.Invoke(null);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (HitNode(e.GetPosition(this)) is not { } node) return;
        if (e.ClickCount == 2) ItemDoubleClicked?.Invoke(node);
        else ItemClicked?.Invoke(node);
    }

    static class Palette
    {
        public static readonly Brush Canvas = FrozenPaint.Solid("#FFFFFF");
        public static readonly Brush Folder = FrozenPaint.Solid("#EEF0F3");
        public static readonly Brush FolderAlt = FrozenPaint.Solid("#E3E6EA");
        public static readonly Pen FolderBorder = FrozenPaint.Stroke("#C9CED5", 1);
        public static readonly Brush FolderText = FrozenPaint.Solid("#2B3138");
        public static readonly Brush Aggregate = FrozenPaint.Solid("#CDD1D6");
        public static readonly Pen TileBorder = FrozenPaint.Stroke("#FFFFFF", 1);
        public static readonly Brush HoverFill = FrozenPaint.Solid("#26FFFFFF");
        public static readonly Pen HoverPen = FrozenPaint.Stroke("#1F2328", 1.5);
        public static readonly Pen SelectedPen = FrozenPaint.Stroke("#2F5D8A", 2.5);
    }
}
