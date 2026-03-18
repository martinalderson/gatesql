using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbProxy.Query;

public record QueryLogEntry
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("task")]
    public required string Task { get; init; }

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("context")]
    public Dictionary<string, string>? Context { get; init; }

    [JsonPropertyName("rowCount")]
    public int? RowCount { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public class QueryLogger : IDisposable
{
    private readonly string _logDirectory;
    private readonly ConcurrentQueue<QueryLogEntry> _buffer = new();
    private readonly Timer _flushTimer;
    private readonly ConcurrentBag<QueryLogEntry> _recentQueries = new();
    private readonly object _writeLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public QueryLogger(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(logDirectory);
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Log(QueryLogEntry entry)
    {
        _buffer.Enqueue(entry);
        _recentQueries.Add(entry);

        // Keep only last 1000 entries in memory for dashboard
        if (_recentQueries.Count > 1500)
            TrimRecentQueries();
    }

    public IReadOnlyList<QueryLogEntry> GetRecentQueries(int count = 100)
    {
        return _recentQueries
            .OrderByDescending(q => q.Timestamp)
            .Take(count)
            .ToList();
    }

    private void Flush()
    {
        var entries = new List<QueryLogEntry>();
        while (_buffer.TryDequeue(out var entry))
            entries.Add(entry);

        if (entries.Count == 0)
            return;

        var fileName = Path.Combine(_logDirectory, $"{DateTime.UtcNow:yyyy-MM-dd}.jsonl");

        lock (_writeLock)
        {
            using var writer = new StreamWriter(fileName, append: true);
            foreach (var entry in entries)
            {
                var json = JsonSerializer.Serialize(entry, JsonOptions);
                writer.WriteLine(json);
            }
        }
    }

    private void TrimRecentQueries()
    {
        var keep = _recentQueries
            .OrderByDescending(q => q.Timestamp)
            .Take(1000)
            .ToList();

        while (_recentQueries.TryTake(out _)) { }

        foreach (var entry in keep)
            _recentQueries.Add(entry);
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        Flush();
        GC.SuppressFinalize(this);
    }
}
