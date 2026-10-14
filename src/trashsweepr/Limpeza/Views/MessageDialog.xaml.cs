using System.Windows;
using TrashSweepr.Limpeza.Services;

namespace TrashSweepr.Limpeza.Views;

/// <summary>Diálogo simples: título, mensagem e um ou dois botões.</summary>
public partial class MessageDialog : Window
{
    public MessageDialog(string title, string message, string confirmText, string? cancelText, bool isDanger)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        ConfirmButton.Style = (Style)FindResource(isDanger ? "DangerBtn" : "PrimaryBtn");

        if (cancelText is null)
        {
            CancelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            CancelButton.Content = cancelText;
        }

        // Enter nunca confirma uma ação perigosa por hábito: nesse caso o padrão é cancelar.
        var dangerWithCancel = isDanger && cancelText is not null;
        ConfirmButton.IsDefault = !dangerWithCancel;
        CancelButton.IsDefault = dangerWithCancel;
    }

    public static MessageDialog Info(string title, string message) =>
        new(title, message, Loc.Get("Dialog.Ok"), null, isDanger: false);

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}