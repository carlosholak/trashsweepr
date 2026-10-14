using System.IO;
using System.Windows.Media;
using TrashSweepr.Localization;

namespace TrashSweepr;

public enum Category { Video, Imagem, Audio, Documento, Codigo, Compactado, Programa, Outros }

public static class FileCategories
{
    public static readonly Category[] All = Enum.GetValues<Category>();

    static readonly Dictionary<string, Category> ByExtension = new(StringComparer.OrdinalIgnoreCase);
    static readonly Brush[] Brushes = new Brush[All.Length];

    static FileCategories()
    {
        Add(Category.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts m2ts vob 3gp");
        Add(Category.Imagem, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 nef arw dng psd ai svg ico");
        Add(Category.Audio, "mp3 wav flac aac ogg m4a wma opus aiff mid");
        Add(Category.Documento, "pdf doc docx xls xlsx xlsm ppt pptx odt ods odp txt rtf csv md epub xml json");
        Add(Category.Codigo, "cs js ts tsx jsx py java c cpp h hpp rs go rb php html css scss sql sh ps1 bat " +
                             "pdb obj lib o a class pyc map lock rlib rmeta d ilk exp pch ipch idx pack");
        Add(Category.Compactado, "zip rar 7z tar gz tgz bz2 xz zst cab iso img vhd vhdx vmdk wim esd");
        Add(Category.Programa, "exe dll sys msi msix appx so dylib jar apk node bin dat pak");

        // Tons dessaturados: o mapa já tem muita área colorida, a cor só distingue o tipo.
        Set(Category.Video, "#9D86B9");
        Set(Category.Imagem, "#7AAA8E");
        Set(Category.Audio, "#C8A665");
        Set(Category.Documento, "#7496BA");
        Set(Category.Codigo, "#7FAAA6");
        Set(Category.Compactado, "#BA8D78");
        Set(Category.Programa, "#A88088");
        Set(Category.Outros, "#AAAEB4");
    }

    static void Add(Category c, string extensions)
    {
        foreach (var ext in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            ByExtension[ext] = c;
    }

    static void Set(Category c, string hex) => Brushes[(int)c] = FrozenPaint.Solid(hex);

    public static Category Of(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length > 1 && ByExtension.TryGetValue(ext[1..], out var c) ? c : Category.Outros;
    }

    public static Brush Brush(Category c) => Brushes[(int)c];

    public static string Label(Category c) => c switch
    {
        Category.Video => Loc.Get("Disk.Category.Video"),
        Category.Imagem => Loc.Get("Disk.Category.Image"),
        Category.Audio => Loc.Get("Disk.Category.Audio"),
        Category.Documento => Loc.Get("Disk.Category.Document"),
        Category.Codigo => Loc.Get("Disk.Category.Code"),
        Category.Compactado => Loc.Get("Disk.Category.Archive"),
        Category.Programa => Loc.Get("Disk.Category.Program"),
        _ => Loc.Get("Disk.Category.Other"),
    };
}
