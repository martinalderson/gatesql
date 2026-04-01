using System.ComponentModel;
using System.Text.Json;
using DbProxy.Auth;
using DbProxy.Query;
using ModelContextProtocol.Server;

namespace DbProxy.Mcp;

[McpServerToolType]
public class McpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "query_database"), Description(
        "Execute a SQL query through the governed proxy. " +
        "All governance rules apply: read-only enforcement, dangerous query blocking, table allowlists, and query budget. " +
        "Each call uses one query from your budget. Multi-statement transactions are not supported — each call is independent.")]
    public static async Task<string> QueryDatabase(
        McpSessionContext ctx,
        GovernedQueryExecutor executor,
        [Description("The SQL query to execute")] string sql,
        [Description("Why this query is being run (logged for audit)")] string purpose,
        [Description("Maximum rows to return (default 500, max 5000)")] int maxRows = 500,
        CancellationToken ct = default)
    {
        var session = ctx.Session ?? throw new InvalidOperationException("No session");

        var result = await executor.ExecuteAsync(session, sql, purpose, maxRows, ct);

        if (!result.Success)
            return JsonSerializer.Serialize(new { error = result.Error }, JsonOptions);

        return JsonSerializer.Serialize(new
        {
            columns = result.Columns!.Select(c => new { c.Name, c.Type }),
            rows = result.Rows!.Select(r => r),
            result.RowCount,
            result.Truncated,
            result.DurationMs,
        }, JsonOptions);
    }

    [McpServerTool(Name = "list_tables"), Description(
        "List available database tables with their schemas and estimated row counts. " +
        "If your session has a table allowlist, only allowed tables are shown. Does not count against your query budget.")]
    public static async Task<string> ListTables(
        McpSessionContext ctx,
        SchemaIntrospector schemaIntrospector)
    {
        var session = ctx.Session ?? throw new InvalidOperationException("No session");

        var tables = await schemaIntrospector.GetSchemaAsync();

        if (session.AllowedTables is { Count: > 0 })
        {
            var allowed = new HashSet<string>(session.AllowedTables, StringComparer.OrdinalIgnoreCase);
            tables = tables.Where(t =>
                allowed.Contains($"{t.Schema}.{t.Name}") ||
                allowed.Contains(t.Name)).ToList();
        }

        return JsonSerializer.Serialize(tables.Select(t => new
        {
            t.Schema,
            t.Name,
            t.EstimatedRows,
        }), JsonOptions);
    }

    [McpServerTool(Name = "describe_table"), Description(
        "Get detailed schema for a specific table including columns, types, nullability, and any annotations. " +
        "Does not count against your query budget.")]
    public static async Task<string> DescribeTable(
        McpSessionContext ctx,
        SchemaIntrospector schemaIntrospector,
        [Description("Table name, with or without schema prefix (e.g. 'orders' or 'public.orders')")] string tableName)
    {
        var session = ctx.Session ?? throw new InvalidOperationException("No session");

        // Check allowlist
        if (session.AllowedTables is { Count: > 0 })
        {
            var allowed = new HashSet<string>(session.AllowedTables, StringComparer.OrdinalIgnoreCase);
            if (!allowed.Contains(tableName))
            {
                var dot = tableName.IndexOf('.');
                var unqualified = dot >= 0 ? tableName[(dot + 1)..] : tableName;
                if (!allowed.Any(a => a.Equals(unqualified, StringComparison.OrdinalIgnoreCase) ||
                                      a.EndsWith($".{unqualified}", StringComparison.OrdinalIgnoreCase)))
                {
                    return JsonSerializer.Serialize(new
                    {
                        error = $"Table \"{tableName}\" is not accessible in this session. Allowed tables: {string.Join(", ", session.AllowedTables)}"
                    }, JsonOptions);
                }
            }
        }

        var tables = await schemaIntrospector.GetSchemaAsync();
        var table = tables.FirstOrDefault(t =>
            $"{t.Schema}.{t.Name}".Equals(tableName, StringComparison.OrdinalIgnoreCase) ||
            t.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));

        if (table == null)
            return JsonSerializer.Serialize(new { error = $"Table \"{tableName}\" not found" }, JsonOptions);

        var annotations = await schemaIntrospector.GetAnnotationsAsync();
        var fullName = $"{table.Schema}.{table.Name}";
        annotations.TryGetValue(fullName, out var ann);

        return JsonSerializer.Serialize(new
        {
            table.Schema,
            table.Name,
            table.EstimatedRows,
            columns = table.Columns.Select(c => new { c.Name, c.Type, c.Nullable }),
            description = ann?.Description,
            exampleQueries = ann?.ExampleQueries,
            notes = ann?.Notes,
        }, JsonOptions);
    }

    [McpServerTool(Name = "get_session_info"), Description(
        "Get current session information including remaining query budget, time left, and permissions.")]
    public static string GetSessionInfo(
        McpSessionContext ctx,
        SessionManager sessionManager)
    {
        var session = ctx.Session ?? throw new InvalidOperationException("No session");

        var remaining = sessionManager.GetRemainingBudget(session.SessionId);
        var timeRemaining = session.ExpiresAt - DateTime.UtcNow;

        return JsonSerializer.Serialize(new
        {
            session.SessionId,
            session.AgentId,
            session.Task,
            session.QueryBudget,
            session.QueriesUsed,
            queriesRemaining = remaining,
            session.ExpiresAt,
            timeRemainingSeconds = Math.Max(0, (int)timeRemaining.TotalSeconds),
            session.IsReadOnly,
            session.DangerousQueryMode,
            session.AllowedTables,
        }, JsonOptions);
    }
}
