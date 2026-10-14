using System.Windows;
using System.Windows.Threading;

namespace TrashSweepr;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Assinado aqui (e não no construtor) para não valer no host dos testes, que cria o App sem chamar Run.
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    /// <summary>Erro inesperado na interface: avisa e mantém o app aberto em vez de fechar sem explicação.</summary>
    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        const string title = "Erro inesperado";
        if (MainWindow is { IsLoaded: true } owner)
            MessageBox.Show(owner, e.Exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(e.Exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
