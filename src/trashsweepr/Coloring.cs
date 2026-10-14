using System.Windows.Media;

namespace TrashSweepr;

public enum ColorMode { Tipo, Idade, Recriavel, Mudancas }

public enum AgeBucket { UmMes, SeisMeses, UmAno, TresAnos, MaisAntigo }

public enum ChangeKind { Desconhecido, Igual, Novo, Cresceu, Diminuiu }

/// <summary>Cor de cada bloco do mapa conforme o modo escolhido.</summary>
public static class Coloring
{
    public static readonly AgeBucket[] AgeBuckets = Enum.GetValues<AgeBucket>();
    public static readonly ChangeKind[] ChangeKinds = [ChangeKind.Novo, ChangeKind.Cresceu, ChangeKind.Diminuiu, ChangeKind.Igual];

    public static readonly Brush Neutral = Solid("#D5D9DE");
    public static readonly Brush Recoverable = Solid("#D3A24F");
    public static readonly Brush RecoverableFolder = Solid("#F5EAD5");
    public static readonly Pen RecoverableFolderBorder = FrozenPaint.Stroke("#C9963F", 1);

    // Uma só matiz, do claro (recente) ao escuro (esquecido): o que chama atenção é o antigo.
    static readonly Brush[] AgeBrushes = [Solid("#DCE6F0"), Solid("#B5CBE0"), Solid("#8AAACB"), Solid("#5F87B0"), Solid("#38608A")];

    static readonly Dictionary<ChangeKind, Brush> ChangeBrushes = new()
    {
        [ChangeKind.Desconhecido] = Neutral,
        [ChangeKind.Igual] = Neutral,
        [ChangeKind.Novo] = Solid("#B4544A"),
        [ChangeKind.Cresceu] = Solid("#D9978B"),
        [ChangeKind.Diminuiu] = Solid("#86B496"),
    };

    static readonly Brush DarkText = Solid("#1C2126");
    static readonly Brush LightText = Solid("#FFFFFF");
    static readonly Dictionary<Brush, Brush> TextCache = [];

    public static AgeBucket Age(long ticks, long nowTicks)
    {
        double days = TimeSpan.FromTicks(nowTicks - ticks).TotalDays;
        return days switch
        {
            < 31 => AgeBucket.UmMes,
            < 183 => AgeBucket.SeisMeses,
            < 365 => AgeBucket.UmAno,
            < 365 * 3 => AgeBucket.TresAnos,
            _ => AgeBucket.MaisAntigo,
        };
    }

    public static string Label(AgeBucket b) => b switch
    {
        AgeBucket.UmMes => "Último mês",
        AgeBucket.SeisMeses => "1 a 6 meses",
        AgeBucket.UmAno => "6 meses a 1 ano",
        AgeBucket.TresAnos => "1 a 3 anos",
        _ => "Mais de 3 anos",
    };

    public static Brush Brush(AgeBucket b) => AgeBrushes[(int)b];

    public static ChangeKind Change(DiskNode n)
    {
        if (n.IsAggregate || n.PreviousSize == DiskNode.NoBaseline) return ChangeKind.Desconhecido;
        if (n.PreviousSize == DiskNode.Added) return ChangeKind.Novo;
        long delta = n.Size - n.PreviousSize;
        // Pastas mudam um pouco o tempo todo (logs, caches): só conta variação relevante.
        long tolerance = n.IsDirectory ? Math.Max(64 * 1024, n.PreviousSize / 200) : 0;
        if (delta > tolerance) return ChangeKind.Cresceu;
        if (delta < -tolerance) return ChangeKind.Diminuiu;
        return ChangeKind.Igual;
    }

    /// <summary>Quanto mudou: o tamanho inteiro se é novo, 0 se não há referência.</summary>
    public static long Delta(DiskNode n) => n.PreviousSize switch
    {
        DiskNode.NoBaseline => 0,
        DiskNode.Added => n.Size,
        var prev => n.Size - prev,
    };

    public static string Label(ChangeKind k) => k switch
    {
        ChangeKind.Novo => "Novo",
        ChangeKind.Cresceu => "Cresceu",
        ChangeKind.Diminuiu => "Diminuiu",
        ChangeKind.Igual => "Sem mudança",
        _ => "Sem referência",
    };

    public static Brush Brush(ChangeKind k) => ChangeBrushes[k];

    /// <summary>Cor de um bloco folha (arquivo, agregado ou pasta pequena demais para abrir).</summary>
    public static Brush Fill(DiskNode n, ColorMode mode, bool insideRecoverable, long nowTicks, Brush aggregate, Brush folder) =>
        mode switch
        {
            ColorMode.Recriavel => insideRecoverable ? Recoverable : Neutral,
            ColorMode.Idade => n.IsAggregate ? Neutral : Brush(Age(n.LastWriteTicks, nowTicks)),
            ColorMode.Mudancas => Brush(Change(n)),
            _ => n.IsAggregate ? aggregate : n.IsDirectory ? folder : FileCategories.Brush(n.Category),
        };

    /// <summary>Texto escuro em fundo claro e branco em fundo escuro.</summary>
    public static Brush TextFor(Brush fill)
    {
        if (TextCache.TryGetValue(fill, out var cached)) return cached;
        var result = DarkText;
        if (fill is SolidColorBrush s)
        {
            var c = s.Color;
            double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
            if (luminance < 0.5) result = LightText;
        }
        TextCache[fill] = result;
        return result;
    }

    static SolidColorBrush Solid(string hex) => FrozenPaint.Solid(hex);
}
