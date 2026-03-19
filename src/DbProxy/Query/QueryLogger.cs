using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbProxy.Data;
using Microsoft.EntityFrameworkCore;

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
    private readonly ConcurrentQueue<QueryLogEntry> _buffer = new();
    private readonly ConcurrentBag<QueryLogEntry> _inMemoryLog = new();
    private readonly IDbContextFactory<GateSqlDbContext>? _dbFactory;
    private readonly Timer _flushTimer;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public QueryLogger(IDbContextFactory<GateSqlDbContext>? dbFactory = null)
    {
        _dbFactory = dbFactory;
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Log(QueryLogEntry entry)
    {
        _buffer.Enqueue(entry);
    }

    public IReadOnlyList<QueryLogEntry> GetRecentQueries(int count = 100)
    {
        if (_dbFactory != null)
        {
            using var db = _dbFactory.CreateDbContext();
            return db.QueryLogs
                .OrderByDescending(q => q.Timestamp)
                .Take(count)
                .Select(q => new QueryLogEntry
                {
                    Timestamp = q.Timestamp,
                    AgentId = q.AgentId,
                    SessionId = q.SessionId,
                    Task = q.Task,
                    Query = q.Query,
                    Context = q.Context != null ? JsonSerializer.Deserialize<Dictionary<string, string>>(q.Context) : null,
                    RowCount = q.RowCount,
                    DurationMs = q.DurationMs,
                    Success = q.Success,
                    Error = q.Error,
                })
                .ToList();
        }

        return _inMemoryLog.OrderByDescending(q => q.Timestamp).Take(count).ToList();
    }

    private void Flush()
    {
        var entries = new List<QueryLogEntry>();
        while (_buffer.TryDequeue(out var entry))
            entries.Add(entry);

        if (entries.Count == 0) return;

        if (_dbFactory != null)
        {
            try
            {
                using var db = _dbFactory.CreateDbContext();
                foreach (var e in entries)
                {
                    db.QueryLogs.Add(new QueryLogEntity
                    {
                        Timestamp = e.Timestamp,
                        AgentId = e.AgentId,
                        SessionId = e.SessionId,
                        Task = e.Task,
                        Query = e.Query,
                        Context = e.Context != null ? JsonSerializer.Serialize(e.Context, JsonOptions) : null,
                        RowCount = e.RowCount,
                        DurationMs = e.DurationMs,
                        Success = e.Success,
                        Error = e.Error,
                    });
                }
                db.SaveChanges();
            }
            catch
            {
                // Don't crash the timer — entries are lost on failure
            }
        }
        else
        {
            foreach (var e in entries)
                _inMemoryLog.Add(e);
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        Flush();
        GC.SuppressFinalize(this);
    }
}
