using System.Diagnostics;
using DbProxy.Auth;
using DbProxy.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DbProxy.Query;

public record QueryResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public List<ColumnDefinition>? Columns { get; init; }
    public List<object?[]>? Rows { get; init; }
    public int RowCount { get; init; }
    public bool Truncated { get; init; }
    public long DurationMs { get; init; }
}

public record ColumnDefinition(string Name, string Type);

public class GovernedQueryExecutor
{
    private readonly ProxyConfig _config;
    private readonly SessionManager _sessionManager;
    private readonly QueryLogger _queryLogger;
    private readonly ILogger<GovernedQueryExecutor> _logger;

    public GovernedQueryExecutor(ProxyConfig config, SessionManager sessionManager,
        QueryLogger queryLogger, ILogger<GovernedQueryExecutor> logger)
    {
        _config = config;
        _sessionManager = sessionManager;
        _queryLogger = queryLogger;
        _logger = logger;
    }

    public async Task<QueryResult> ExecuteAsync(AgentSession session, string sql, string purpose,
        int maxRows = 500, CancellationToken ct = default)
    {
        maxRows = Math.Clamp(maxRows, 1, 5000);
        var safePurpose = purpose.Replace("*/", "* /");
        var fullSql = $"/* <agent_purpose>{safePurpose}</agent_purpose> */ {sql}";
        var sw = Stopwatch.StartNew();

        // Validate session is still active
        var invalidReason = _sessionManager.GetInvalidReason(session.SessionId);
        if (invalidReason != null)
        {
            return new QueryResult
            {
                Success = false,
                Error = $"Session is no longer valid: {invalidReason}",
            };
        }

        // Analyze query
        var analysis = QueryAnalyzer.Analyze(sql);

        // Read-only enforcement (fail-closed: only allow known-safe types)
        if (session.IsReadOnly && analysis.Type is not (StatementType.Read or StatementType.Transaction or StatementType.Utility))
        {
            LogRejection(session, fullSql, "Session is read-only");
            return new QueryResult
            {
                Success = false,
                Error = $"Query rejected — session is read-only. This session only allows SELECT, EXPLAIN, and SHOW queries. Detected: {analysis.Type} statement",
            };
        }

        // Dangerous query detection
        if (analysis.IsDangerous && session.DangerousQueryMode == "block")
        {
            LogRejection(session, fullSql, $"Dangerous operation: {analysis.DangerReason}");
            return new QueryResult
            {
                Success = false,
                Error = $"Query rejected — dangerous operation detected. Reason: {analysis.DangerReason}. Add a WHERE clause or contact your administrator.",
            };
        }

        if (analysis.IsDangerous && session.DangerousQueryMode == "warn")
        {
            _logger.LogWarning("Dangerous query from agent {AgentId} (session {SessionId}): {Reason} — {Query}",
                session.AgentId, session.SessionId, analysis.DangerReason, sql);
        }

        // Table allowlist enforcement
        if (session.AllowedTables is { Count: > 0 } && analysis.TableNames.Count > 0)
        {
            var disallowed = QueryAnalyzer.CheckTableAllowlist(analysis.TableNames, session.AllowedTables);
            if (disallowed != null)
            {
                LogRejection(session, fullSql, $"Table \"{disallowed}\" not allowed");
                return new QueryResult
                {
                    Success = false,
                    Error = $"Query rejected — table \"{disallowed}\" is not accessible in this session. Allowed tables: {string.Join(", ", session.AllowedTables)}",
                };
            }
        }

        // Budget enforcement
        if (!_sessionManager.IncrementQueryCount(session.SessionId))
        {
            LogRejection(session, fullSql, "Query budget exhausted");
            return new QueryResult
            {
                Success = false,
                Error = "Query rejected — query budget exhausted. Use get_session_info to check your remaining budget.",
            };
        }

        _sessionManager.TouchSession(session.SessionId);

        // Execute query
        try
        {
            var connStr = $"Host={_config.Upstream.Host};Port={_config.Upstream.Port};" +
                          $"Database={_config.Upstream.Database};Username={_config.Upstream.Username};" +
                          $"Password={_config.Upstream.Password};Timeout=30;Command Timeout=60";

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(fullSql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            var columns = new List<ColumnDefinition>();
            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(new ColumnDefinition(reader.GetName(i), reader.GetDataTypeName(i)));

            var rows = new List<object?[]>();
            var truncated = false;
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }

                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }

            sw.Stop();

            _queryLogger.Log(new QueryLogEntry
            {
                AgentId = session.AgentId,
                SessionId = session.SessionId,
                Task = session.Task,
                Query = fullSql,
                RowCount = rows.Count,
                DurationMs = sw.ElapsedMilliseconds,
                Success = true,
                Context = new Dictionary<string, string> { ["source"] = "mcp" },
            });

            return new QueryResult
            {
                Success = true,
                Columns = columns,
                Rows = rows,
                RowCount = rows.Count,
                Truncated = truncated,
                DurationMs = sw.ElapsedMilliseconds,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();

            _queryLogger.Log(new QueryLogEntry
            {
                AgentId = session.AgentId,
                SessionId = session.SessionId,
                Task = session.Task,
                Query = fullSql,
                RowCount = 0,
                DurationMs = sw.ElapsedMilliseconds,
                Success = false,
                Error = ex.Message,
                Context = new Dictionary<string, string> { ["source"] = "mcp" },
            });

            return new QueryResult
            {
                Success = false,
                Error = ex.Message,
                DurationMs = sw.ElapsedMilliseconds,
            };
        }
    }

    private void LogRejection(AgentSession session, string sql, string error)
    {
        _queryLogger.Log(new QueryLogEntry
        {
            AgentId = session.AgentId,
            SessionId = session.SessionId,
            Task = session.Task,
            Query = sql,
            RowCount = 0,
            DurationMs = 0,
            Success = false,
            Error = error,
            Context = new Dictionary<string, string> { ["source"] = "mcp" },
        });
    }
}
