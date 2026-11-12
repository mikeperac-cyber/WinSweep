using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSweep.App.ViewModels;
using WinSweep.Core.Logging;

namespace WinSweep.App.Views;

public sealed partial class MaintenancePage : Page
{
    private readonly MaintenanceViewModel _viewModel;

    public MaintenancePage(OperationLog log)
    {
        InitializeComponent();

        _viewModel = new MaintenanceViewModel(log);
        DataContext = _viewModel;

        Loaded += async (_, _) => await RunRequestedTaskAsync();
    }

    /// <summary>Runs the task the app was launched for, if any.</summary>
    private async Task RunRequestedTaskAsync()
    {
        var taskId = StartupOptions.MaintenanceTaskId;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        StartupOptions.MaintenanceTaskId = null;
        _viewModel.ShowTasks();

        var item = _viewModel.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (item is null)
        {
            return;
        }

        if (item.RequiresElevation && !_viewModel.IsElevated)
        {
            _viewModel.StatusText = "This task needs the administrator window that was just started.";
            return;
        }

        await _viewModel.RunAsync(item);
    }

    private void ReviewTasks_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ShowTasks();
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var item = FindItem(sender);
        if (item is null)
        {
            return;
        }

        if (item.RequiresElevation && !_viewModel.IsElevated)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "This task needs an administrator account",
                Content = new TextBlock
                {
                    Text = "Windows will ask for your permission. The app restarts with administrator rights and runs the task there."
                },
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!MaintenanceViewModel.RelaunchElevated(item.Id))
            {
                _viewModel.StatusText = "The administrator window could not be started.";
            }

            return;
        }

        await _viewModel.RunAsync(item);
    }

    private async void RunElevated_Click(object sender, RoutedEventArgs e)
    {
        var item = FindItem(sender);
        if (item is null)
        {
            return;
        }

        if (!MaintenanceViewModel.RelaunchElevated(item.Id))
        {
            _viewModel.StatusText = "The administrator window could not be started.";
        }
    }

    private MaintenanceTaskItem? FindItem(object sender)
    {
        if (sender is Button button && button.Tag is string id)
        {
            return _viewModel.Tasks.FirstOrDefault(t => t.Id == id);
        }

        return null;
    }
}
