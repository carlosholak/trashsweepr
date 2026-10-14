using System.Globalization;
using System.Runtime.CompilerServices;

namespace TrashSweepr.Tests.Helpers;

/// <summary>
/// Os testes rodam sempre na mesma língua, seja qual for o Windows da máquina (o runner do GitHub é em inglês).
/// Padrão: pt-BR, que é o que a maioria dos testes confere. TRASHSWEEPR_TEST_CULTURE=en-US roda tudo em inglês.
/// Os testes que precisam de uma língua específica usam <see cref="Use"/>.
/// </summary>
internal static class TestCulture
{
    public static CultureInfo Default { get; } =
        CultureInfo.GetCultureInfo(Environment.GetEnvironmentVariable("TRASHSWEEPR_TEST_CULTURE") is { Length: > 0 } name ? name : "pt-BR");

    [ModuleInitializer]
    internal static void Initialize() => Apply(Default);

    /// <summary>
    /// Troca a língua só deste teste até o retorno ser descartado. Vale também depois de await
    /// (a cultura acompanha o contexto de execução) e dentro de <see cref="WpfHost"/>.
    /// </summary>
    public static IDisposable Use(string name)
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        return new Restore(previous.CurrentCulture, previous.CurrentUICulture);
    }

    private static void Apply(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private sealed class Restore(CultureInfo culture, CultureInfo uiCulture) : IDisposable
    {
        public void Dispose()
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }
}
