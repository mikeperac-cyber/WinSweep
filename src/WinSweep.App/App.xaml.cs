using Microsoft.UI.Xaml;

namespace WinSweep.App;

public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// The main window's HWND, set in <see cref="OnLaunched"/> before the window is activated.
    /// Any page that opens a system picker must run only after that point, because the picker
    /// needs the parent window handle.
    /// </summary>
    internal static IntPtr MainWindowHandle { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupOptions.Parse(Environment.GetCommandLineArgs());

        _window = new MainWindow(StartupOptions.Page);
        MainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _window.Activate();
    }
}
