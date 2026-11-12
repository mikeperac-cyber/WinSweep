using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSweep.App.ViewModels;
using WinSweep.Core.Models;

namespace WinSweep.App.Views;

public sealed partial class DiskPage : Page
{
    private readonly DiskViewModel _viewModel = new();

    public DiskPage()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ScanAsync();
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.GoUpAsync();
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");

        // The picker needs a window handle on its own thread.
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await _viewModel.ScanAsync(folder.Path);
        }
    }

    private async void Entry_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DiskEntry entry)
        {
            await _viewModel.DrillIntoAsync(entry);
        }
    }
}
