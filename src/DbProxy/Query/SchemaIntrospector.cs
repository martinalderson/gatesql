using System.Text;
using DbProxy.Configuration;
using DbProxy.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DbProxy.Query;

public record ColumnInfo(string Name, string Type, bool Nullable);
public record TableSchema(string Schema, string Name, List<ColumnInfo> Columns, long EstimatedRows);

public class SchemaIntrospector
{
    private readonly ProxyConfig _config;
    private readonly IDbContextFactory<GateSqlDbContext>? _dbFactory;

    private List<TableSchema>? _cachedSchema;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SchemaIntrospector(ProxyConfig config, IDbContextFactory<GateSqlDbContext>? dbFactory = null)
    {
        _config = config;
        _dbFactory = dbFactory;
    }

    public async Task<List<TableSchema>> GetSchemaAsync()
    {
        if (_cachedSchema != null && DateTime.UtcNow < _cacheExpiry)
            return _cachedSchema;

        await _lock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_cachedSchema != null && DateTime.UtcNow < _cacheExpiry)
                return _cachedSchema;

            _cachedSchema = await IntrospectAsync();
            _cacheExpiry = DateTime.UtcNow.Add(_cacheDuration);
            return _cachedSchema;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void InvalidateCache() => _cacheExpiry = DateTime.MinValue;

    public async Task<Dictionary<string, SchemaAnnotationEntity>> GetAnnotationsAsync()
    {
        if (_dbFactory == null) return new();
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.SchemaAnnotations.ToDictionaryAsync(a => a.TableName);
    }

    public async Task SaveAnnotationAsync(string tableName, string? description, string? exampleQueries, string? notes)
    {
        if (_dbFactory == null) return;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.SchemaAnnotations.FindAsync(tableName);
        if (existing != null)
        {
            existing.Description = description;
            existing.ExampleQueries = exampleQueries;
            existing.Notes = notes;
        }
        else
        {
            db.SchemaAnnotations.Add(new SchemaAnnotationEntity
            {
                TableName = tableName,
                Description = description,
                ExampleQueries = exampleQueries,
                Notes = notes,
            });
        }
        await db.SaveChangesAsync();
    }

    public async Task<string> RenderMarkdownAsync()
    {
        var tables = await GetSchemaAsync();
        var annotations = await GetAnnotationsAsync();
        var sb = new StringBuilder();

        foreach (var table in tables)
        {
            var fullName = $"{table.Schema}.{table.Name}";
            sb.AppendLine($"## {table.Name}");

            if (annotations.TryGetValue(fullName, out var ann) && !string.IsNullOrWhiteSpace(ann.Description))
                sb.AppendLine(ann.Description);

            sb.AppendLine();
            sb.AppendLine("| Column | Type | Nullable |");
            sb.AppendLine("|---|---|---|");
            foreach (var col in table.Columns)
                sb.AppendLine($"| {col.Name} | {col.Type} | {(col.Nullable ? "yes" : "no")} |");

            sb.AppendLine();
            sb.AppendLine($"~{FormatRowCount(table.EstimatedRows)} rows");

            if (ann != null)
            {
                if (!string.IsNullOrWhiteSpace(ann.ExampleQueries))
                {
                    sb.AppendLine();
                    sb.AppendLine("**Example queries:**");
                    foreach (var q in ann.ExampleQueries.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        sb.AppendLine($"- `{q}`");
                }

                if (!string.IsNullOrWhiteSpace(ann.Notes))
                {
                    sb.AppendLine();
                    sb.AppendLine($"**Notes:** {ann.Notes}");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    public async Task<string> GetCompactSummaryAsync()
    {
        var tables = await GetSchemaAsync();
        if (tables.Count == 0) return "";

        var parts = tables.Select(t =>
        {
            var cols = string.Join(",", t.Columns.Select(c => c.Name));
            return $"{t.Name}({cols}) ~{FormatRowCount(t.EstimatedRows)}";
        });

        return $"Schema: {string.Join(" | ", parts)}";
    }

    private async Task<List<TableSchema>> IntrospectAsync()
    {
        var connStr = $"Host={_config.Upstream.Host};Port={_config.Upstream.Port};" +
                      $"Database={_config.Upstream.Database};Username={_config.Upstream.Username};" +
                      $"Password={_config.Upstream.Password};Timeout=10";

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // Get row estimates
        var rowEstimates = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new NpgsqlCommand(
            "SELECT schemaname, relname, n_live_tup FROM pg_stat_user_tables", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var key = $"{reader.GetString(0)}.{reader.GetString(1)}";
                rowEstimates[key] = reader.GetInt64(2);
            }
        }

        // Get tables and columns in one query
        var tables = new Dictionary<string, TableSchema>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.table_schema, c.table_name, c.column_name, c.data_type, c.is_nullable, c.ordinal_position
            FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE t.table_schema NOT IN ('pg_catalog', 'information_schema')
              AND t.table_type = 'BASE TABLE'
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """, conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var schema = reader.GetString(0);
                var table = reader.GetString(1);
                var key = $"{schema}.{table}";

                if (!tables.ContainsKey(key))
                {
                    rowEstimates.TryGetValue(key, out var rows);
                    tables[key] = new TableSchema(schema, table, [], rows);
                }

                tables[key].Columns.Add(new ColumnInfo(
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4) == "YES"));
            }
        }

        return tables.Values.OrderBy(t => t.Schema).ThenBy(t => t.Name).ToList();
    }

    private static string FormatRowCount(long count) => count switch
    {
        >= 1_000_000 => $"{count / 1_000_000.0:0.#}M",
        >= 1_000 => $"{count / 1_000.0:0.#}k",
        _ => count.ToString(),
    };
}
