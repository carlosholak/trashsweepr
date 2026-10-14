using System.Windows;

namespace TrashSweepr;

/// <summary>Treemap "squarified" (Bruls, Huizing, van Wijk): retângulos o mais próximos possível de quadrados.</summary>
public static class Squarify
{
    /// <param name="items">Itens com tamanho &gt; 0, ordenados do maior para o menor.</param>
    public static void Layout(IReadOnlyList<DiskNode> items, Rect area, List<(Rect Rect, DiskNode Node)> output)
    {
        double total = 0;
        foreach (var it in items) total += it.Size;
        if (total <= 0 || area.Width <= 0 || area.Height <= 0) return;

        double scale = area.Width * area.Height / total;
        var free = area;
        int i = 0;
        while (i < items.Count)
        {
            double side = Math.Min(free.Width, free.Height);
            if (side <= 0) break;

            double largest = items[i].Size * scale;
            double rowSum = 0;
            double worst = double.MaxValue;
            int j = i;
            while (j < items.Count)
            {
                double a = items[j].Size * scale;
                double sum = rowSum + a;
                double ratio = Math.Max(side * side * largest / (sum * sum), sum * sum / (side * side * a));
                if (j > i && ratio > worst) break;
                worst = ratio;
                rowSum = sum;
                j++;
            }

            if (free.Width >= free.Height)
            {
                // Coluna encostada à esquerda.
                double thick = rowSum / free.Height;
                double y = free.Y;
                for (int k = i; k < j; k++)
                {
                    double h = items[k].Size * scale / thick;
                    output.Add((new Rect(free.X, y, thick, h), items[k]));
                    y += h;
                }
                free = new Rect(free.X + thick, free.Y, Math.Max(0, free.Width - thick), free.Height);
            }
            else
            {
                // Linha encostada no topo.
                double thick = rowSum / free.Width;
                double x = free.X;
                for (int k = i; k < j; k++)
                {
                    double w = items[k].Size * scale / thick;
                    output.Add((new Rect(x, free.Y, w, thick), items[k]));
                    x += w;
                }
                free = new Rect(free.X, free.Y + thick, free.Width, Math.Max(0, free.Height - thick));
            }
            i = j;
        }
    }
}
