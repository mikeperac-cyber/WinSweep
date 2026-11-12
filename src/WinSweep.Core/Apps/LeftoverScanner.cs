using WinSweep.Core.Models;
using WinSweep.Core.Scanning;

namespace WinSweep.Core.Apps;

/// <summary>
/// Finds the folders an app leaves behind. Only exact folder-name matches inside the
/// known per-user data locations are offered, so a vendor-wide sweep never happens.
/// </summary>
public sealed class LeftoverScanner
{
    private static readonly string[] DataRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
    };

    private readonly TimeSpan _budget;

    public LeftoverScanner(TimeSpan? budget = null)
    {
        _budget = budget ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Builds the review for one application. Nothing is removed here.</summary>
    public UninstallPlan BuildPlan(InstalledApp app)
    {
        var leftovers = new List<LeftoverItem>();

        // A Microsoft Store application keeps its data in a folder named after its
        // package family. That folder holds personal data, so it is never selected.
        if (app.Source == InstallSource.WindowsStore && !string.IsNullOrWhiteSpace(app.PackageFamilyName))
        {
            var packageFolder = StoreAppInventory.GetPackageDataFolder(app.PackageFamilyName);
            if (Directory.Exists(packageFolder))
            {
                var measurement = DirectorySizer.Measure(packageFolder, default, _budget);
                leftovers.Add(new LeftoverItem
                {
                    Path = packageFolder,
                    Name = app.PackageFamilyName,
                    Kind = "Store app data",
                    SizeBytes = measurement.Bytes,
                    SizeStatus = measurement.Status,
                    Reason = $"The data folder that {app.Name} owns. It may hold settings and saved work.",
                    HoldsPersonalData = true,
                    IsSelected = false
                });
            }
        }

        var folderNames = GetCandidateFolderNames(app);

        foreach (var root in DataRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var folderName in folderNames)
            {
                var candidate = Path.Combine(root, folderName);
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                var measurement = DirectorySizer.Measure(candidate, default, _budget);
                var holdsPersonalData = IsPersonalDataLocation(candidate);

                leftovers.Add(new LeftoverItem
                {
                    Path = candidate,
                    Name = folderName,
                    Kind = "Folder",
                    SizeBytes = measurement.Bytes,
                    SizeStatus = measurement.Status,
                    Reason = $"Named exactly like {app.Name} in your user data folders.",
                    HoldsPersonalData = holdsPersonalData,
                    IsSelected = !holdsPersonalData
                });
            }
        }

        var warning = string.IsNullOrWhiteSpace(app.UninstallString)
            ? "This app has no registered uninstaller. Only leftover folders can be removed."
            : null;

        return new UninstallPlan
        {
            App = app,
            Leftovers = leftovers,
            UninstallCommand = BuildDisplayCommand(app),
            Warning = warning
        };
    }

    /// <summary>The exact folder names that may belong to this app.</summary>
    private static IReadOnlyList<string> GetCandidateFolderNames(InstalledApp app)
    {
        var names = new List<string>();

        if (!string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            var leaf = app.InstallLocation.TrimEnd('\\').Split('\\').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(leaf))
            {
                names.Add(leaf);
            }
        }

        // The publisher folder name is only used when it matches the app name exactly.
        var publisherFolder = app.Name.Split(' ').FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(publisherFolder) &&
            !names.Contains(publisherFolder, StringComparer.OrdinalIgnoreCase) &&
            app.Name.StartsWith(publisherFolder, StringComparison.OrdinalIgnoreCase) &&
            publisherFolder.Length >= 4)
        {
            names.Add(publisherFolder);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsPersonalDataLocation(string path)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (!string.IsNullOrEmpty(roaming) &&
            path.StartsWith(roaming, StringComparison.OrdinalIgnoreCase))
        {
            // Settings and profile data live here, so it is never selected by default.
            return true;
        }

        if (!string.IsNullOrEmpty(documents) &&
            path.StartsWith(documents, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string BuildDisplayCommand(InstalledApp app)
    {
        var command = string.IsNullOrWhiteSpace(app.QuietUninstallString)
            ? app.UninstallString
            : app.QuietUninstallString;

        return command.Trim();
    }
}
