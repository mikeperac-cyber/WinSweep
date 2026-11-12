using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSweep.App.ViewModels;
using WinSweep.Core.Logging;
using WinSweep.Core.Settings;

namespace WinSweep.App.Views;

public sealed partial class CleanPage : Page
{
    private readonly CleanViewModel _viewModel;

    public CleanPage(OperationLog log, AppSettings settings)
    {
        InitializeComponent();

        _viewModel = new CleanViewModel(log, settings);
        DataContext = _viewModel;

        if (StartupOptions.AutoScan)
        {
            Loaded += async (_, _) => await _viewModel.ScanAsync();
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ScanAsync();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.Candidates.Count(c => c.IsSelected);
        if (selected == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove the selected locations?",
            Content = new TextBlock
            {
                Text = _viewModel.IsUsingRecycleBin
                    ? $"{selected} locations will be moved to the Recycle Bin, so you can still recover them."
                    : $"{selected} locations will be deleted permanently. This cannot be undone."
            },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _viewModel.RemoveSelectedAsync();
    }
}
