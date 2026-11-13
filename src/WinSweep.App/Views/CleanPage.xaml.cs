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
            Loaded += async (_, _) =>
            {
                await _viewModel.ScanAsync();
                if (StartupOptions.DryRun)
                {
                    StartupOptions.DryRun = false;
                    await ShowPreviewAsync();
                }
            };
        }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        await ShowPreviewAsync();
    }

    private async Task ShowPreviewAsync()
    {
        var manifest = await _viewModel.PreviewAsync();
        if (manifest is not null)
        {
            await SweepPreview.ShowAsync(XamlRoot, manifest);
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
                Text = $"{selected} locations will be {_viewModel.RemovalModeText}",
                TextWrapping = TextWrapping.Wrap
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
