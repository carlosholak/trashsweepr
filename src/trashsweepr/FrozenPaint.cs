using System.Windows.Media;

namespace TrashSweepr;

/// <summary>Pincéis e canetas congelados (imutáveis, compartilháveis entre threads e mais baratos de desenhar).</summary>
static class FrozenPaint
{
    /// <summary>Pincel sólido a partir de "#RRGGBB" ou "#AARRGGBB".</summary>
    public static SolidColorBrush Solid(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static Pen Stroke(string hex, double thickness)
    {
        var p = new Pen(Solid(hex), thickness);
        p.Freeze();
        return p;
    }
}
