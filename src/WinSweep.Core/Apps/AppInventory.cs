using Microsoft.Win32;
using WinSweep.Core.Models;
using WinSweep.Core.Scanning;

namespace WinSweep.Core.Apps;

/// <summary>Reads the installed application inventory from the registry.</summary>
public sealed class AppInventory
{
    private static readonly string[] UninstallKeys =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    private readonly TimeSpan _sizeBudget;
    private readonly TimeSpan _perAppBudget;

    public AppInventory(TimeSpan? sizeBudget = null, TimeSpan? perAppBudget = null)
    {
        _sizeBudget = sizeBudget ?? TimeSpan.FromSeconds(20);
        _perAppBudget = perAppBudget ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Lists installed applications, optionally measuring their install folders.</summary>
    public IReadOnlyList<InstalledApp> ListInstalledApps(bool measureSizes = true, CancellationToken cancellationToken = default)
    {
        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var keyPath in UninstallKeys)
            {
                CollectFromKey(hive, keyPath, apps, seen);
            }
        }

        return Measure(apps, measureSizes, cancellationToken);
    }

    /// <summary>Lists the registry applications and the Microsoft Store applications together.</summary>
    public IReadOnlyList<InstalledApp> ListAllApps(bool measureSizes = true, CancellationToken cancellationToken = default)
    {
        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var keyPath in UninstallKeys)
            {
                CollectFromKey(hive, keyPath, apps, seen);
            }
        }

        var storeApps = new StoreAppInventory(_sizeBudget, _perAppBudget).ListStoreApps(measureSizes: false, cancellationToken);
        var namedStoreApps = StoreAppInventory.WithFriendlyNames(storeApps);
        foreach (var storeApp in namedStoreApps)
        {
            // The same application can be listed by both sources.
            if (!apps.Any(a => string.Equals(a.Name, storeApp.Name, StringComparison.OrdinalIgnoreCase)))
            {
                apps.Add(storeApp);
            }
        }

        return Measure(apps, measureSizes, cancellationToken);
    }

    private IReadOnlyList<InstalledApp> Measure(
        IReadOnlyList<InstalledApp> apps,
        bool measureSizes,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _sizeBudget;
        var results = new List<InstalledApp>();

        foreach (var app in apps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!measureSizes || app.SizeBytes is not null)
            {
                results.Add(app);
                continue;
            }

            // The whole measurement run is bounded. An app that cannot be measured in
            // time keeps an unknown size instead of holding up the whole list.
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                results.Add(app);
                continue;
            }

            var perApp = remaining < _perAppBudget ? remaining : _perAppBudget;

            long? size = null;
            if (!string.IsNullOrWhiteSpace(app.InstallLocation) && Directory.Exists(app.InstallLocation))
            {
                var measurement = DirectorySizer.Measure(app.InstallLocation, cancellationToken, perApp);
                if (measurement.Status != ScanStatus.Unavailable)
                {
                    size = measurement.Bytes;
                }
            }

            results.Add(new InstalledApp
            {
                Id = app.Id,
                Name = app.Name,
                Version = app.Version,
                Publisher = app.Publisher,
                InstallLocation = app.InstallLocation,
                UninstallString = app.UninstallString,
                QuietUninstallString = app.QuietUninstallString,
                Source = app.Source,
                InstallDateUtc = app.InstallDateUtc,
                IconKey = app.IconKey,
                PackageFamilyName = app.PackageFamilyName,
                SizeBytes = size
            });
        }

        return results
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CollectFromKey(RegistryHive hive, string keyPath, List<InstalledApp> apps, HashSet<string> seen)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(keyPath);

        if (key is null)
        {
            return;
        }

        foreach (var subName in key.GetSubKeyNames())
        {
            using var subKey = key.OpenSubKey(subName);
            if (subKey is null)
            {
                continue;
            }

            var displayName = GetString(subKey, "DisplayName");
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            if (GetInt(subKey, "SystemComponent") == 1)
            {
                continue;
            }

            var keyId = subKey.Name;
            if (!seen.Add(keyId))
            {
                continue;
            }

            var uninstallString = GetString(subKey, "UninstallString");
            var quietUninstallString = GetString(subKey, "QuietUninstallString");

            apps.Add(new InstalledApp
            {
                Id = $"registry:{subName}",
                Name = displayName,
                Version = GetString(subKey, "DisplayVersion"),
                Publisher = GetString(subKey, "Publisher"),
                InstallLocation = GetString(subKey, "InstallLocation"),
                UninstallString = uninstallString,
                QuietUninstallString = quietUninstallString,
                Source = InstallSource.Registry,
                InstallDateUtc = ParseInstallDate(GetString(subKey, "InstallDate")),
                IconKey = GetString(subKey, "DisplayIcon")
            });
        }
    }

    private static string GetString(RegistryKey key, string name)
    {
        try
        {
            return (key.GetValue(name) as string)?.Trim() ?? string.Empty;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException)
        {
            return string.Empty;
        }
    }

    private static int GetInt(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) is int value ? value : 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException)
        {
            return 0;
        }
    }

    private static DateTime? ParseInstallDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length != 8)
        {
            return null;
        }

        if (DateTime.TryParseExact(raw, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
