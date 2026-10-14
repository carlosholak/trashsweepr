using System.Globalization;
using TrashSweepr.Localization;

namespace TrashSweepr;

/// <summary>
/// Números, tamanhos e datas seguem o formato regional do Windows (CurrentCulture); os textos,
/// a língua da interface (ver <see cref="Loc"/>).
/// </summary>
public static class Format
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    static CultureInfo Culture => CultureInfo.CurrentCulture;

    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(Culture) + " B";
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(value < 10 ? "0.0" : "0", Culture) + " " + Units[unit];
    }

    public static string Count(long n) => n.ToString("N0", Culture);

    /// <summary>"1 arquivo" / "3 arquivos".</summary>
    public static string Files(long n) => Plural(n, "Disk.Files");

    /// <summary>
    /// Escolhe <c>{key}.One</c> (n = 1) ou <c>{key}.Other</c> e põe a contagem no lugar de {0}:
    /// "1 pasta" / "3 pastas", "1 day ago" / "3 days ago". Português e inglês só distinguem o singular.
    /// </summary>
    public static string Plural(long n, string key) => Loc.Format(n == 1 ? key + ".One" : key + ".Other", Count(n));

    /// <summary>Caminho relativo a <paramref name="basePath"/>; <paramref name="whenSame"/> se for a própria pasta.</summary>
    public static string RelativePath(string basePath, string path, string whenSame)
    {
        string rel = System.IO.Path.GetRelativePath(basePath, path);
        return rel == "." ? whenSame : rel;
    }

    public static string Percent(double fraction)
    {
        if (fraction <= 0) return "0%";
        if (fraction < 0.001) return "<" + 0.1.ToString("0.0", Culture) + "%";
        return (fraction * 100).ToString(fraction < 0.1 ? "0.0" : "0", Culture) + "%";
    }

    /// <summary>"+1,2 GB" / "−300 MB".</summary>
    public static string Signed(long delta) => delta switch
    {
        > 0 => "+" + Bytes(delta),
        < 0 => "−" + Bytes(-delta),
        _ => Loc.Get("Disk.NoChange"),
    };

    /// <summary>Data e hora curtas do formato regional ("14/10/2026 09:30", "10/14/2026 9:30 AM").</summary>
    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("g", Culture);

    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var d = nowUtc - utc;
        if (d.TotalMinutes < 2) return Loc.Get("Disk.Ago.JustNow");
        if (d.TotalMinutes < 60) return Plural((int)d.TotalMinutes, "Disk.Ago.Minutes");
        if (d.TotalHours < 24) return Plural((int)d.TotalHours, "Disk.Ago.Hours");
        if (d.TotalDays < 14) return Plural((int)d.TotalDays, "Disk.Ago.Days");
        if (d.TotalDays < 60) return Plural((int)(d.TotalDays / 7), "Disk.Ago.Weeks");
        if (d.TotalDays < 365) return Plural((int)(d.TotalDays / 30), "Disk.Ago.Months");
        return Plural((int)(d.TotalDays / 365), "Disk.Ago.Years");
    }

    public static string Seconds(TimeSpan t) => t.TotalSeconds.ToString(t.TotalSeconds < 10 ? "0.0" : "0", Culture) + " s";
}
