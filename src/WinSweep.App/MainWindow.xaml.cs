using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinSweep.App.Views;
using WinSweep.Core.Logging;
using WinSweep.Core.Settings;
using Windows.UI;

namespace WinSweep.App;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly OperationLog _log = new();

    private static readonly Dictionary<string, Color> PageColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Clean"] = Color.FromArgb(255, 27, 41, 64),
        ["Apps"] = Color.FromArgb(255, 57, 34, 40),
        ["Artifacts"] = Color.FromArgb(255, 28, 43, 41),
        ["Optimize"] = Color.FromArgb(255, 46, 45, 44),
        ["Analyze"] = Color.FromArgb(255, 62, 39, 25),
        ["Status"] = Color.FromArgb(255, 52, 47, 28),
        ["History"] = Color.FromArgb(255, 34, 40, 45),
        ["Settings"] = Color.FromArgb(255, 34, 40, 45)
    };

    private static readonly Color SelectedNavigationColor = Color.FromArgb(255, 241, 239, 233);
    private static readonly Color SelectedNavigationTextColor = Color.FromArgb(255, 30, 33, 36);
    private static readonly Color DefaultNavigationTextColor = Color.FromArgb(255, 217, 217, 213);

    public MainWindow(string? initialPage = null)
    {
        InitializeComponent();

        Title = "WinSweep";
        ExtendsContentIntoTitleBar = false;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WinSweep.ico"));

        SelectPage(string.IsNullOrWhiteSpace(initialPage) ? "Status" : initialPage);
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string page)
        {
            SelectPage(page);
        }
    }

    private void History_Click(object sender, RoutedEventArgs e) => SelectPage("History");

    private void Settings_Click(object sender, RoutedEventArgs e) => SelectPage("Settings");

    /// <summary>Opens a page and updates the top navigation and its ambient color.</summary>
    private void SelectPage(string pageName)
    {
        pageName = NormalizePageName(pageName);

        ContentFrame.Content = pageName switch
        {
            "Clean" => new CleanPage(_log, _settings),
            "Apps" => new AppsPage(_log, _settings),
            "Artifacts" => new ProjectArtifactsPage(_settings, _log),
            "Optimize" => new MaintenancePage(_log),
            "Analyze" => new DiskPage(),
            "History" => new HistoryPage(_log),
            "Settings" => new SettingsPage(_settings),
            _ => new OverviewPage()
        };

        var key = pageName;
        if (PageColors.TryGetValue(key, out var color))
        {
            RootGrid.Background = new SolidColorBrush(color);
        }

        UpdateNavigationState(pageName);
    }

    private static string NormalizePageName(string pageName) => pageName.ToLowerInvariant() switch
    {
        "clean" => "Clean",
        "apps" => "Apps",
        "projects" or "artifacts" => "Artifacts",
        "maintenance" or "optimize" => "Optimize",
        "disk" or "analyze" => "Analyze",
        "overview" or "status" => "Status",
        "history" => "History",
        "settings" => "Settings",
        _ => "Status"
    };

    private void UpdateNavigationState(string pageName)
    {
        foreach (var button in new[] { CleanNav, AppsNav, ProjectsNav, OptimizeNav, AnalyzeNav, StatusNav })
        {
            var selected = string.Equals(button.Tag as string, pageName, StringComparison.OrdinalIgnoreCase);
            button.Background = new SolidColorBrush(selected ? SelectedNavigationColor : Color.FromArgb(0, 0, 0, 0));
            button.Foreground = new SolidColorBrush(selected ? SelectedNavigationTextColor : DefaultNavigationTextColor);
        }
    }
}
