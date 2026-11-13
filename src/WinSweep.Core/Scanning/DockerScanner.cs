using System.Text.Json;
using WinSweep.Core.Logging;
using WinSweep.Core.Maintenance;
using WinSweep.Core.Models;

namespace WinSweep.Core.Scanning;

/// <summary>One kind of reclaimable Docker data. <see cref="Kind"/> is "images", "containers" or "build-cache".</summary>
public sealed record DockerItem(string Kind, string Name, int Count, string Reclaimable);

/// <summary>
/// Finds Docker leftovers through the docker command line and prunes them. Volumes are never
/// offered because they hold data.
/// </summary>
public sealed class DockerScanner
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Turns <c>docker system df --format "{{json .}}"</c> lines plus the dangling image ids into items.</summary>
    public static IReadOnlyList<DockerItem> Parse(string systemDfOutput, string danglingImageIds)
    {
        var danglingCount = danglingImageIds
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Length;

        var items = new List<DockerItem>();
        foreach (var line in systemDfOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var reclaimable = Field(root, "Reclaimable");
            var unused = Number(root, "TotalCount") - Number(root, "Active");

            switch (Field(root, "Type"))
            {
                case "Images" when danglingCount > 0:
                    items.Add(new DockerItem("images", "Dangling images", danglingCount, ""));
                    break;
                case "Containers" when unused > 0:
                    items.Add(new DockerItem("containers", "Stopped containers", unused, reclaimable));
                    break;
                case "Build Cache" when reclaimable.Length > 0 && !reclaimable.StartsWith("0B", StringComparison.Ordinal):
                    items.Add(new DockerItem("build-cache", "Unused build cache", unused, reclaimable));
                    break;
            }
        }

        return items;
    }

    /// <summary>Lists what Docker could reclaim. The second value explains why nothing was listed, or is null.</summary>
    public async Task<(IReadOnlyList<DockerItem> Items, string? Unavailable)> ScanAsync(
        CancellationToken cancellationToken = default)
    {
        var df = await CommandRunner.RunAsync("docker.exe", "system df --format \"{{json .}}\"", QueryTimeout, cancellationToken);
        if (df.Outcome != MaintenanceOutcome.Applied)
        {
            return (Array.Empty<DockerItem>(), "Docker is not installed or its engine is not running.");
        }

        var dangling = await CommandRunner.RunAsync("docker.exe", "images -f dangling=true -q", QueryTimeout, cancellationToken);
        return (Parse(df.Output, dangling.Outcome == MaintenanceOutcome.Applied ? dangling.Output : ""), null);
    }

    /// <summary>Prunes one kind of leftover with a fixed command and records the outcome.</summary>
    public async Task<MaintenanceResult> PruneAsync(
        DockerItem item,
        OperationLog log,
        string runId,
        CancellationToken cancellationToken = default)
    {
        var arguments = item.Kind switch
        {
            "images" => "image prune -f",
            "containers" => "container prune -f",
            "build-cache" => "builder prune -f",
            _ => null
        };

        if (arguments is null)
        {
            return MaintenanceResult.Unavailable($"Docker cannot clean \"{item.Kind}\".");
        }

        var result = await CommandRunner.RunAsync("docker.exe", arguments, TimeSpan.FromMinutes(5), cancellationToken);
        log.Append(new OperationRecord
        {
            Command = "docker",
            RunId = runId,
            Action = result.Outcome == MaintenanceOutcome.Applied ? OperationAction.Completed : OperationAction.Failed,
            Path = item.Kind,
            Detail = $"{item.Name}: {result.Message}"
        });
        return result;
    }

    private static string Field(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int Number(JsonElement root, string name) =>
        int.TryParse(Field(root, name), out var number) ? number : 0;
}
