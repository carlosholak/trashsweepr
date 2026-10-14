using System.Globalization;
using System.Resources;

namespace TrashSweepr.Cleanup.Core;

/// <summary>
/// Mensagens do motor de limpeza que chegam ao usuário ou ao log (erros de execução, backup, validação do catálogo).
/// Ficam em Resources\Messages.resx (inglês, neutro) e Messages.pt.resx (português) e seguem a cultura da
/// interface do Windows, como os textos do app. Para traduzir, basta acrescentar Messages.&lt;cultura&gt;.resx
/// com as mesmas chaves.
/// </summary>
public static class CoreText
{
    private static readonly ResourceManager Manager = new("TrashSweepr.Cleanup.Core.Resources.Messages", typeof(CoreText).Assembly);

    /// <summary>Texto da chave na cultura da interface (inglês quando a cultura não tem tradução).</summary>
    public static string Get(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? $"!{key}!";

    /// <summary>Texto da chave com os argumentos no lugar de {0}, {1}...</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
