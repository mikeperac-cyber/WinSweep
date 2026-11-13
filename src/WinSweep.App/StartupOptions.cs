namespace WinSweep.App;

/// <summary>
/// Startup options passed on the command line, used for testing and diagnostics:
/// <c>--page</c>, <c>--scan</c>, <c>--maintenance</c>, <c>--dry-run</c> (scan, then preview without removing) and <c>--rollback</c> (undo a sweep).
/// </summary>
public static class StartupOptions
{
    /// <summary>The page to open on launch: Overview, Clean, Apps, Disk, History or Settings. <c>--dry-run</c> defaults it to Clean, <c>--rollback</c> sets History.</summary>
    public static string? Page { get; private set; }

    /// <summary>Start a cleanup scan as soon as the Clean page opens.</summary>
    public static bool AutoScan { get; private set; }

    /// <summary>The maintenance task to run as soon as the Maintenance page opens.</summary>
    public static string? MaintenanceTaskId { get; set; }

    /// <summary>Show the dry-run preview as soon as the scan on the Clean page finishes; nothing is removed.</summary>
    public static bool DryRun { get; private set; }

    /// <summary>The sweep to undo as soon as the History page opens: a sweep id or "last".</summary>
    public static string? RollbackSweepId { get; set; }

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
                case "--dry-run":
                    DryRun = true;
                    AutoScan = true;
                    Page ??= "clean";
                    break;
                case "--rollback" when i + 1 < args.Length:
                    RollbackSweepId = args[++i];
                    Page = "history";
                    break;
            }
        }
    }
}
