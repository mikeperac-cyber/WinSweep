using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSweep.App.ViewModels;
using WinSweep.Core.Logging;

namespace WinSweep.App.Views;

public sealed partial class HistoryPage : Page
{
    private readonly HistoryViewModel _viewModel;

    public HistoryPage(OperationLog log)
    {
        InitializeComponent();

        _viewModel = new HistoryViewModel(log);
        DataContext = _viewModel;

        Loaded += (_, _) => _viewModel.Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Refresh();
    }
}
