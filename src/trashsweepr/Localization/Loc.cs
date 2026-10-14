using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace TrashSweepr.Localization;

/// <summary>
/// Textos da interface. Os arquivos Resources\Strings*.resx são resolvidos pela cultura da interface;
/// para traduzir, basta acrescentar Strings.en.resx (ou outra cultura) com as mesmas chaves.
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Manager = new("TrashSweepr.Resources.Strings", typeof(Loc).Assembly);

    /// <summary>Cultura usada para buscar textos. Nula = cultura da interface do Windows.</summary>
    public static CultureInfo? Override { get; set; }

    public static string Get(string key) =>
        Manager.GetString(key, Override ?? CultureInfo.CurrentUICulture) ?? $"!{key}!";

    public static string Format(string key, params object[] args) =>
        string.Format(Override ?? CultureInfo.CurrentUICulture, Get(key), args);
}

/// <summary>Uso no XAML: <c>Text="{loc:T Chave.Do.Texto}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
