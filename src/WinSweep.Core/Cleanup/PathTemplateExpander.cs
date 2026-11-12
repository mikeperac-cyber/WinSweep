namespace WinSweep.Core.Cleanup;

/// <summary>
/// Turns a rule path template into concrete paths. Supports environment variables and a
/// single wildcard that matches one or more folder levels.
/// </summary>
public static class PathTemplateExpander
{
    public static IReadOnlyList<string> Expand(string template, bool includeFiles = false)
    {
        var expanded = Environment.ExpandEnvironmentVariables(template);

        if (!expanded.Contains('*', StringComparison.Ordinal))
        {
            return new[] { expanded };
        }

        var results = new List<string>();
        var segments = expanded.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        // Everything before the first wildcard is the fixed base directory.
        var firstWildcard = Array.FindIndex(segments, s => s.Contains('*', StringComparison.Ordinal));
        if (firstWildcard < 0)
        {
            return new[] { expanded };
        }

        var baseParts = segments.Take(firstWildcard).ToArray();
        var basePath = baseParts.Length == 0
            ? Path.GetPathRoot(expanded) ?? string.Empty
            : Path.Combine(baseParts);

        try
        {
            ExpandLevel(basePath, segments.Skip(firstWildcard).ToArray(), results, includeFiles);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            // A base folder that does not exist simply yields nothing.
        }

        return results;
    }

    private static void ExpandLevel(string current, IReadOnlyList<string> remaining, List<string> results, bool includeFiles)
    {
        if (remaining.Count == 0)
        {
            if (Directory.Exists(current))
            {
                results.Add(current);
            }

            return;
        }

        var segment = remaining[0];
        var isLast = remaining.Count == 1;

        IEnumerable<string> matches;
        try
        {
            matches = Directory.EnumerateDirectories(current, segment);
            if (includeFiles && isLast)
            {
                matches = matches.Concat(Directory.EnumerateFiles(current, segment));
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return;
        }

        foreach (var match in matches)
        {
            if (isLast)
            {
                results.Add(match);
            }
            else
            {
                ExpandLevel(match, remaining.Skip(1).ToArray(), results, includeFiles);
            }
        }
    }
}
