using System.Collections.Concurrent;
using System.Text.Json;
using DbProxy.Data;
using Microsoft.EntityFrameworkCore;

namespace DbProxy.Auth;

public record AgentSession
{
    public required string SessionId { get; init; }
    public required string AgentId { get; init; }
    public required string Task { get; init; }
    public int? QueryBudget { get; init; }
    public int QueriesUsed { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; init; }
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public bool IsConnected { get; set; }
    public bool IsRevoked { get; set; }
    public bool IsReadOnly { get; init; }
    public string DangerousQueryMode { get; init; } = "block";
    public List<string>? AllowedTables { get; init; }
}

public class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly TimeSpan _idleTimeout;
    private readonly IDbContextFactory<GateSqlDbContext>? _dbFactory;
    private readonly Timer _cleanupTimer;
    private readonly Timer? _flushTimer;

    public SessionManager(TimeSpan idleTimeout, IDbContextFactory<GateSqlDbContext>? dbFactory = null)
    {
        _idleTimeout = idleTimeout;
        _dbFactory = dbFactory;
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        if (_dbFactory != null)
            _flushTimer = new Timer(_ => FlushToDb(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public async Task InitializeAsync()
    {
        if (_dbFactory == null) return;

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Load active sessions into memory
        var sessions = await db.Sessions
            .Where(s => !s.IsRevoked && s.ExpiresAt > DateTime.UtcNow)
            .ToListAsync();

        foreach (var e in sessions)
        {
            _sessions.TryAdd(e.SessionId, new AgentSession
            {
                SessionId = e.SessionId,
                AgentId = e.AgentId,
                Task = e.Task,
                QueryBudget = e.QueryBudget,
                QueriesUsed = e.QueriesUsed,
                CreatedAt = e.CreatedAt,
                ExpiresAt = e.ExpiresAt,
                LastActivityAt = e.LastActivityAt,
                IsConnected = false, // reset on restart
                IsRevoked = e.IsRevoked,
                IsReadOnly = e.IsReadOnly,
                DangerousQueryMode = e.DangerousQueryMode ?? "block",
                AllowedTables = e.AllowedTablesJson != null
                    ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(e.AllowedTablesJson)
                    : null,
            });
        }
    }

    public AgentSession CreateSession(string sessionId, string agentId, string task, int? queryBudget, DateTime expiresAt,
        bool isReadOnly = false, string dangerousQueryMode = "block", List<string>? allowedTables = null)
    {
        var session = new AgentSession
        {
            SessionId = sessionId,
            AgentId = agentId,
            Task = task,
            QueryBudget = queryBudget,
            ExpiresAt = expiresAt,
            IsReadOnly = isReadOnly,
            DangerousQueryMode = dangerousQueryMode,
            AllowedTables = allowedTables,
        };

        if (!_sessions.TryAdd(sessionId, session))
            throw new InvalidOperationException($"Session {sessionId} already exists");

        if (_dbFactory != null)
        {
            using var db = _dbFactory.CreateDbContext();
            db.Sessions.Add(new SessionEntity
            {
                SessionId = session.SessionId,
                AgentId = session.AgentId,
                Task = session.Task,
                QueryBudget = session.QueryBudget,
                QueriesUsed = 0,
                CreatedAt = session.CreatedAt,
                ExpiresAt = session.ExpiresAt,
                LastActivityAt = session.LastActivityAt,
                IsConnected = false,
                IsRevoked = false,
                IsReadOnly = session.IsReadOnly,
                DangerousQueryMode = session.DangerousQueryMode,
                AllowedTablesJson = session.AllowedTables != null ? System.Text.Json.JsonSerializer.Serialize(session.AllowedTables) : null,
            });
            db.SaveChanges();
        }

        return session;
    }

    public AgentSession? GetSession(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var session) ? session : null;
    }

    public bool ValidateSession(string sessionId)
    {
        return GetInvalidReason(sessionId) == null;
    }

    public string? GetInvalidReason(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return "not_found";

        if (session.IsRevoked)
            return "revoked";

        if (DateTime.UtcNow > session.ExpiresAt)
            return "expired";

        if (DateTime.UtcNow - session.LastActivityAt > _idleTimeout)
            return "idle_timeout";

        return null;
    }

    public void TouchSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            session.LastActivityAt = DateTime.UtcNow;
    }

    public bool IncrementQueryCount(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        if (session.QueryBudget.HasValue && session.QueriesUsed >= session.QueryBudget.Value)
            return false;

        session.QueriesUsed++;
        return true;
    }

    public int? GetRemainingBudget(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        if (!session.QueryBudget.HasValue)
            return null;

        return Math.Max(0, session.QueryBudget.Value - session.QueriesUsed);
    }

    public bool RevokeSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        session.IsRevoked = true;

        if (_dbFactory != null)
        {
            using var db = _dbFactory.CreateDbContext();
            var entity = db.Sessions.Find(sessionId);
            if (entity != null)
            {
                entity.IsRevoked = true;
                db.SaveChanges();
            }
        }

        return true;
    }

    public IReadOnlyList<AgentSession> GetActiveSessions()
    {
        return _sessions.Values
            .Where(s => !s.IsRevoked && DateTime.UtcNow < s.ExpiresAt)
            .ToList();
    }

    public IReadOnlyList<AgentSession> GetAllSessions()
    {
        return _sessions.Values.ToList();
    }

    private void FlushToDb()
    {
        if (_dbFactory == null) return;

        try
        {
            using var db = _dbFactory.CreateDbContext();
            var active = _sessions.Values.Where(s => !s.IsRevoked && s.ExpiresAt > DateTime.UtcNow).ToList();

            foreach (var s in active)
            {
                var entity = db.Sessions.Find(s.SessionId);
                if (entity == null) continue;
                entity.QueriesUsed = s.QueriesUsed;
                entity.LastActivityAt = s.LastActivityAt;
                entity.IsConnected = s.IsConnected;
            }

            db.SaveChanges();
        }
        catch
        {
            // Don't crash the timer on transient DB errors
        }
    }

    private void CleanupExpiredSessions(object? state)
    {
        var expired = _sessions.Values
            .Where(s => DateTime.UtcNow > s.ExpiresAt.AddHours(1))
            .Select(s => s.SessionId)
            .ToList();

        foreach (var id in expired)
            _sessions.TryRemove(id, out _);

        if (_dbFactory == null || expired.Count == 0) return;

        try
        {
            using var db = _dbFactory.CreateDbContext();
            var cutoff = DateTime.UtcNow.AddHours(-1);
            db.Sessions.Where(s => s.ExpiresAt < cutoff).ExecuteDelete();
        }
        catch
        {
            // Don't crash the timer
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        _flushTimer?.Dispose();

        // Final flush
        if (_dbFactory != null)
            FlushToDb();

        GC.SuppressFinalize(this);
    }
}
