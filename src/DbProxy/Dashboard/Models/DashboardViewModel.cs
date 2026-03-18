using DbProxy.Auth;
using DbProxy.Query;

namespace DbProxy.Dashboard.Models;

public class DashboardViewModel
{
    public required List<SessionViewModel> ActiveSessions { get; init; }
    public required List<QueryLogEntry> RecentQueries { get; init; }
    public int TotalSessions { get; init; }
    public int ConnectedNow { get; init; }
    public int TotalQueries { get; init; }
    public int IdleTimeoutMinutes { get; init; }
}

public class SessionViewModel
{
    public required string SessionId { get; init; }
    public required string AgentId { get; init; }
    public required string Task { get; init; }
    public int? QueryBudget { get; init; }
    public int QueriesUsed { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime LastActivityAt { get; init; }
    public bool IsConnected { get; init; }
    public bool IsRevoked { get; init; }

    public string Status =>
        IsRevoked ? "Revoked" :
        DateTime.UtcNow > ExpiresAt ? "Expired" :
        IsConnected ? "Connected" :
        "Idle";

    public string StatusCss =>
        Status switch
        {
            "Connected" => "badge-green",
            "Revoked" => "badge-red",
            "Expired" => "badge-gray",
            _ => "badge-yellow"
        };

    public string BudgetDisplay =>
        QueryBudget.HasValue ? $"{QueriesUsed} / {QueryBudget}" : $"{QueriesUsed}";

    public string TimeAgo(DateTime dt)
    {
        var span = DateTime.UtcNow - dt;
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s ago";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }

    public static SessionViewModel FromSession(AgentSession s) => new()
    {
        SessionId = s.SessionId,
        AgentId = s.AgentId,
        Task = s.Task,
        QueryBudget = s.QueryBudget,
        QueriesUsed = s.QueriesUsed,
        CreatedAt = s.CreatedAt,
        ExpiresAt = s.ExpiresAt,
        LastActivityAt = s.LastActivityAt,
        IsConnected = s.IsConnected,
        IsRevoked = s.IsRevoked,
    };
}
