using System.Windows;
using TrashSweepr.Limpeza.Services;
using TrashSweepr.Limpeza.ViewModels;

namespace TrashSweepr.Limpeza.Views;

public partial class ConfirmRisksDialog : Window
{
    public ConfirmRisksDialog(IReadOnlyList<RiskConfirmationItem> items)
    {
        InitializeComponent();
        DataContext = new ConfirmRisksViewModel(items);
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}