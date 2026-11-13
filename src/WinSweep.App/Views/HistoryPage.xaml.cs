using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSweep.App.ViewModels;
using WinSweep.Core.Logging;
using WinSweep.Core.Settings;

namespace WinSweep.App.Views;

public sealed partial class HistoryPage : Page
{
    private readonly HistoryViewModel _viewModel;

    public HistoryPage(OperationLog log, AppSettings settings)
    {
        InitializeComponent();

        _viewModel = new HistoryViewModel(log, settings);
        DataContext = _viewModel;

        Loaded += async (_, _) =>
        {
            _viewModel.Refresh();

            // --rollback <id|last>: runs once, then is cleared so reopening the page does not repeat it.
            var rollbackId = StartupOptions.RollbackSweepId;
            if (!string.IsNullOrWhiteSpace(rollbackId))
            {
                StartupOptions.RollbackSweepId = null;
                if (string.Equals(rollbackId, "last", StringComparison.OrdinalIgnoreCase))
                {
                    await _viewModel.UndoLastAsync();
                }
                else
                {
                    await _viewModel.RestoreAsync(rollbackId);
                }
            }
        };
    }

    private async void UndoLast_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.UndoLastAsync();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string id)
        {
            await _viewModel.RestoreAsync(id);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete this sweep now?",
            Content = new TextBlock
            {
                Text = "The quarantined files will be deleted permanently. This cannot be undone, and you will not be able to restore this sweep."
            },
            PrimaryButtonText = "Delete permanently",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _viewModel.Delete(id);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Refresh();
    }
}
