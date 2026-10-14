using System.Globalization;

namespace TrashSweepr;

public static class Format
{
    static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(value < 10 ? "0.0" : "0", PtBr) + " " + Units[unit];
    }

    public static string Count(long n) => n.ToString("N0", PtBr);

    public static string Files(long n) => n == 1 ? "1 arquivo" : $"{Count(n)} arquivos";

    /// <summary>"1 pasta" / "3 pastas".</summary>
    public static string Plural(long n, string one, string many) => $"{Count(n)} {(n == 1 ? one : many)}";

    /// <summary>Caminho relativo a <paramref name="basePath"/>; <paramref name="whenSame"/> se for a própria pasta.</summary>
    public static string RelativePath(string basePath, string path, string whenSame)
    {
        string rel = System.IO.Path.GetRelativePath(basePath, path);
        return rel == "." ? whenSame : rel;
    }

    public static string Percent(double fraction)
    {
        if (fraction <= 0) return "0%";
        if (fraction < 0.001) return "<0,1%";
        return (fraction * 100).ToString(fraction < 0.1 ? "0.0" : "0", PtBr) + "%";
    }

    /// <summary>"+1,2 GB" / "−300 MB".</summary>
    public static string Signed(long delta) => delta switch
    {
        > 0 => "+" + Bytes(delta),
        < 0 => "−" + Bytes(-delta),
        _ => "sem mudança",
    };

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", PtBr);

    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var d = nowUtc - utc;
        if (d.TotalMinutes < 2) return "agora há pouco";
        if (d.TotalMinutes < 60) return $"há {(int)d.TotalMinutes} min";
        if (d.TotalHours < 24) return $"há {(int)d.TotalHours} h";
        if (d.TotalDays < 14) return "há " + Plural((int)d.TotalDays, "dia", "dias");
        if (d.TotalDays < 60) return "há " + Plural((int)(d.TotalDays / 7), "semana", "semanas");
        if (d.TotalDays < 365) return "há " + Plural((int)(d.TotalDays / 30), "mês", "meses");
        return "há " + Plural((int)(d.TotalDays / 365), "ano", "anos");
    }

    public static string Seconds(TimeSpan t) => t.TotalSeconds.ToString(t.TotalSeconds < 10 ? "0.0" : "0", PtBr) + " s";
}
