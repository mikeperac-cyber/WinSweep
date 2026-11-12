namespace WinSweep.App;

/// <summary>Startup options passed on the command line, used for testing and diagnostics.</summary>
public static class StartupOptions
{
    /// <summary>The page to open on launch: Overview, Clean, Apps, Disk, History or Settings.</summary>
    public static string? Page { get; private set; }

    /// <summary>Start a cleanup scan as soon as the Clean page opens.</summary>
    public static bool AutoScan { get; private set; }

    /// <summary>The maintenance task to run as soon as the Maintenance page opens.</summary>
    public static string? MaintenanceTaskId { get; set; }

    public static void Parse(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--page" when i + 1 < args.Length:
                    Page = args[++i];
                    break;
                case "--scan":
                    AutoScan = true;
                    break;
                case "--maintenance" when i + 1 < args.Length:
                    MaintenanceTaskId = args[++i];
                    break;
            }
        }
    }
}
