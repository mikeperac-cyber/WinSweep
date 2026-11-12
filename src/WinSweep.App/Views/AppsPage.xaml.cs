using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSweep.App.ViewModels;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Settings;

namespace WinSweep.App.Views;

public sealed partial class AppsPage : Page
{
    private readonly AppsViewModel _viewModel;

    public AppsPage(OperationLog log, AppSettings settings)
    {
        InitializeComponent();

        _viewModel = new AppsViewModel(log, settings);
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadAsync();
    }

    private async void Apps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.FirstOrDefault() is InstalledApp app)
        {
            _viewModel.SelectedApp = app;
            await _viewModel.ReviewSelectedAsync();
        }
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ReviewSelectedAsync();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedApp is null)
        {
            return;
        }

        if (_viewModel.SelectedAppIsStore)
        {
            _viewModel.OpenWindowsAppSettings();
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Uninstall {_viewModel.SelectedApp.Name}?",
            Content = new TextBlock
            {
                Text = "The app's own uninstaller will run. It decides what it removes, so some files may stay. You can review those leftovers afterwards."
            },
            PrimaryButtonText = "Run uninstaller",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _viewModel.UninstallSelectedAsync(preferSilent: false);
        await _viewModel.ReviewSelectedAsync();
    }

    private async void RemoveLeftovers_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.Leftovers.Count(l => l.IsSelected);
        if (selected == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove the selected leftover folders?",
            Content = new TextBlock
            {
                Text = $"{selected} folders will be moved to the Recycle Bin. Folders that hold personal data stay unselected unless you tick them yourself."
            },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _viewModel.RemoveSelectedLeftoversAsync();
    }
}
