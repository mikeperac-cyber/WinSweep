using WinSweep.Core.Safety;

namespace WinSweep.Core.Sweep;

/// <summary>Turns the targets a sweep wants to remove into a manifest, without touching any of them.</summary>
public static class SweepPlanner
{
    /// <summary>Where the dry run writes its manifest.</summary>
    public static string DryRunManifestPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinSweep",
        "sweep-manifest.json");

    /// <summary>Builds the manifest for <paramref name="targets"/>; targets that no longer exist are left out.</summary>
    public static SweepManifest Build(string source, IEnumerable<SweepTarget> targets,
        RiskAnalyzer analyzer, bool dryRun, string? id = null)
    {
        var entries = new List<SweepEntry>();
        foreach (var target in targets)
        {
            var path = PathGuard.Normalize(target.Path);
            var isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path))
            {
                continue;
            }

            DateTime? lastWrite = null;
            try
            {
                lastWrite = isDirectory ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The time is informational only.
            }

            var (risk, reason) = analyzer.Evaluate(path);
            entries.Add(new SweepEntry
            {
                Path = path,
                Name = target.Name,
                Category = target.Category,
                IsDirectory = isDirectory,
                SizeBytes = target.SizeBytes,
                LastWriteUtc = lastWrite,
                Risk = risk,
                RiskReason = reason
            });
        }

        return new SweepManifest
        {
            Id = id ?? Guid.NewGuid().ToString("N")[..8],
            Source = source,
            DryRun = dryRun,
            TotalBytes = entries.Sum(e => e.SizeBytes),
            BytesByCategory = entries.GroupBy(e => e.Category).ToDictionary(g => g.Key, g => g.Sum(e => e.SizeBytes)),
            Entries = entries.OrderByDescending(e => e.SizeBytes).ToList()
        };
    }
}
