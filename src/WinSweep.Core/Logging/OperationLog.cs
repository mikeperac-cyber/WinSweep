using System.Text.Json;
using System.Text.Json.Serialization;
using WinSweep.Core.Models;

namespace WinSweep.Core.Logging;

/// <summary>
/// Append-only history of everything the app changed. One JSON object per line so a
/// partially written last line can never corrupt the earlier history.
/// </summary>
public sealed class OperationLog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _logPath;
    private readonly object _writeLock = new();

    public OperationLog(string? logPath = null)
    {
        _logPath = logPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinSweep",
            "operations.jsonl");
    }

    public string LogPath => _logPath;

    /// <summary>Records one action. A failed write warns once instead of failing the action.</summary>
    public void Append(OperationRecord record)
    {
        try
        {
            var directory = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonSerializer.Serialize(record, SerializerOptions);
            lock (_writeLock)
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A history write must never break the operation the user asked for.
            Console.Error.WriteLine($"WinSweep: could not write the operation history: {e.Message}");
        }
    }

    /// <summary>Reads the history, newest first. Unreadable lines are skipped, not fatal.</summary>
    public IReadOnlyList<OperationRecord> Read(int limit = 200)
    {
        if (!File.Exists(_logPath))
        {
            return Array.Empty<OperationRecord>();
        }

        var records = new List<OperationRecord>();
        try
        {
            foreach (var line in File.ReadLines(_logPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var record = JsonSerializer.Deserialize<OperationRecord>(line, SerializerOptions);
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                    // Skip a damaged line instead of losing the whole history.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return records;
        }

        records.Reverse();
        return records.Count <= limit ? records : records.GetRange(0, limit);
    }
}
