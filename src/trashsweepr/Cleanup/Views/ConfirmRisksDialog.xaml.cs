using System.Windows;
using TrashSweepr.Cleanup.Services;
using TrashSweepr.Cleanup.ViewModels;

namespace TrashSweepr.Cleanup.Views;

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