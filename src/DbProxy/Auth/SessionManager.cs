using System.Collections.Concurrent;

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
}

public class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly TimeSpan _idleTimeout;
    private readonly Timer _cleanupTimer;

    public SessionManager(TimeSpan idleTimeout)
    {
        _idleTimeout = idleTimeout;
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public AgentSession CreateSession(string sessionId, string agentId, string task, int? queryBudget, DateTime expiresAt)
    {
        var session = new AgentSession
        {
            SessionId = sessionId,
            AgentId = agentId,
            Task = task,
            QueryBudget = queryBudget,
            ExpiresAt = expiresAt,
        };

        if (!_sessions.TryAdd(sessionId, session))
            throw new InvalidOperationException($"Session {sessionId} already exists");

        return session;
    }

    public AgentSession? GetSession(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var session) ? session : null;
    }

    public bool ValidateSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        if (session.IsRevoked)
            return false;

        if (DateTime.UtcNow > session.ExpiresAt)
            return false;

        if (DateTime.UtcNow - session.LastActivityAt > _idleTimeout)
            return false;

        return true;
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

        session.QueriesUsed++;

        if (session.QueryBudget.HasValue && session.QueriesUsed > session.QueryBudget.Value)
            return false;

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

    private void CleanupExpiredSessions(object? state)
    {
        var expired = _sessions.Values
            .Where(s => DateTime.UtcNow > s.ExpiresAt.AddHours(1)) // Keep for 1hr after expiry for history
            .Select(s => s.SessionId)
            .ToList();

        foreach (var id in expired)
            _sessions.TryRemove(id, out _);
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        GC.SuppressFinalize(this);
    }
}
