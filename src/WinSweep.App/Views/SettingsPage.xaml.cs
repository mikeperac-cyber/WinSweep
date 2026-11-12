using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSweep.App.ViewModels;
using WinSweep.Core.Settings;

namespace WinSweep.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(AppSettings settings)
    {
        InitializeComponent();

        _viewModel = new SettingsViewModel(settings);
        DataContext = _viewModel;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.AddProtectedPath(ProtectedPathBox.Text);
        ProtectedPathBox.Text = string.Empty;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string path)
        {
            _viewModel.RemoveProtectedPath(path);
        }
    }
}
