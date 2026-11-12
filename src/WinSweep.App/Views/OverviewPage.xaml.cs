using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSweep.App.ViewModels;

namespace WinSweep.App.Views;

public sealed partial class OverviewPage : Page
{
    private readonly DashboardViewModel _viewModel = new();

    public OverviewPage()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshAsync();
    }
}
