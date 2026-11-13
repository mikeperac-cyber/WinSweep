using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSweep.App.ViewModels;
using WinSweep.Core.Logging;
using WinSweep.Core.Settings;

namespace WinSweep.App.Views;

public sealed partial class ProjectArtifactsPage : Page
{
    private readonly ProjectArtifactsViewModel _viewModel;

    public ProjectArtifactsPage(AppSettings settings, OperationLog log)
    {
        InitializeComponent();
        _viewModel = new ProjectArtifactsViewModel(settings, log);
        DataContext = _viewModel;

        if (StartupOptions.AutoScan)
        {
            Loaded += async (_, _) => await _viewModel.ScanAsync();
        }
    }

    private async void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            _viewModel.AddRoot(folder.Path);
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ScanAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelScan();
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string path)
        {
            _viewModel.RemoveRoot(path);
        }
    }

    private void SelectSafe_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectAllSafe();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearSelection();
    }

    private async void CheckDocker_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.CheckDockerAsync();
    }

    private async void PruneDocker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kind })
        {
            return;
        }

        var item = _viewModel.DockerItems.FirstOrDefault(i => i.Kind == kind);
        if (item is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Prune {item.Name}?",
            Content = new TextBlock { Text = "Docker deletes these permanently. This cannot be undone from WinSweep.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Prune",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _viewModel.PruneDockerAsync(item);
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        var manifest = await _viewModel.PreviewAsync();
        if (manifest is not null)
        {
            await SweepPreview.ShowAsync(XamlRoot, manifest);
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedCount == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _viewModel.ConfirmTitle,
            Content = new TextBlock { Text = _viewModel.ConfirmText },
            PrimaryButtonText = "Move",
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
