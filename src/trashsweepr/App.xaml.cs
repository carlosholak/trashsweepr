using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using TrashSweepr.Localization;

namespace TrashSweepr;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A língua segue o Windows. TRASHSWEEPR_LANG (ex.: en-US) força outra, só para testar e tirar capturas.
        if (Environment.GetEnvironmentVariable("TRASHSWEEPR_LANG") is { Length: > 0 } lang)
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(lang);
                CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
            }
            catch (CultureNotFoundException)
            {
                // nome inválido: fica a língua do Windows
            }
        }

        // O WPF formata bindings em en-US por padrão; aqui passa a seguir o formato regional do Windows.
        // Só uma vez por processo (OverrideMetadata lança na segunda), por isso fica no OnStartup.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        // Assinado aqui (e não no construtor) para não valer no host dos testes, que cria o App sem chamar Run.
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    /// <summary>Erro inesperado na interface: avisa e mantém o app aberto em vez de fechar sem explicação.</summary>
    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string title = Loc.Get("App.UnexpectedError");
        if (MainWindow is { IsLoaded: true } owner)
            MessageBox.Show(owner, e.Exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(e.Exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
