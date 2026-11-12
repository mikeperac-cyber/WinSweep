using Microsoft.UI.Xaml;

namespace WinSweep.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupOptions.Parse(Environment.GetCommandLineArgs());

        _window = new MainWindow(StartupOptions.Page);
        _window.Activate();
    }
}
