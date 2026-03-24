using System.Text.Json.Serialization;
using DbProxy.Auth;
using DbProxy.Configuration;
using DbProxy.Query;

namespace DbProxy.Api;

public static class AdminApiEndpoints
{
    public static void MapAdminApi(this WebApplication app, ProxyConfig config, JwtAuthenticator jwtAuth, SessionManager sessionManager, QueryLogger queryLogger, SchemaIntrospector? schemaIntrospector = null)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/sessions", (CreateSessionRequest request, HttpContext ctx) =>
        {
            var apiKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(apiKey) || !config.Auth.ParentApiKeys.Any(k => k.Key == apiKey))
                return Results.Json(new { error = "Invalid API key" }, statusCode: 401);

            var sessionId = $"sess_{Guid.NewGuid():N}";
            var lifetime = TimeSpan.FromMinutes(config.Auth.HardCapMinutes);
            var expiresAt = DateTime.UtcNow.Add(lifetime);

            var session = sessionManager.CreateSession(
                sessionId,
                request.AgentId,
                request.Task,
                request.QueryBudget,
                expiresAt,
                request.ReadOnly ?? false,
                request.DangerousQueryMode ?? "block",
                request.AllowedTables);

            var token = jwtAuth.GenerateToken(
                sessionId,
                request.AgentId,
                request.Task,
                request.QueryBudget,
                lifetime);

            var host = config.Proxy.ListenHost == "0.0.0.0" ? "localhost" : config.Proxy.ListenHost;
            var port = config.Proxy.ListenPort;
            var database = config.Upstream.Database;

            return Results.Json(new CreateSessionResponse
            {
                Token = token,
                SessionId = sessionId,
                ExpiresAt = expiresAt,
                ConnectionString = $"postgresql://agent:{Uri.EscapeDataString(token)}@{host}:{port}/{database}",
                PsqlCommand = $"PGPASSWORD=\"{token}\" psql -h {host} -p {port} -U agent -d {database}",
            });
        });

        api.MapGet("/sessions", (HttpContext ctx) =>
        {
            var apiKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(apiKey) || !config.Auth.ParentApiKeys.Any(k => k.Key == apiKey))
                return Results.Json(new { error = "Invalid API key" }, statusCode: 401);

            var sessions = sessionManager.GetAllSessions()
                .Select(s => new SessionDto
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
                    IsReadOnly = s.IsReadOnly,
                    DangerousQueryMode = s.DangerousQueryMode,
                    AllowedTables = s.AllowedTables,
                });

            return Results.Json(sessions);
        });

        api.MapDelete("/sessions/{sessionId}", (string sessionId, HttpContext ctx) =>
        {
            var apiKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(apiKey) || !config.Auth.ParentApiKeys.Any(k => k.Key == apiKey))
                return Results.Json(new { error = "Invalid API key" }, statusCode: 401);

            if (sessionManager.RevokeSession(sessionId))
                return Results.Ok(new { message = "Session revoked" });

            return Results.NotFound(new { error = "Session not found" });
        });

        api.MapGet("/queries", (HttpContext ctx, int? count) =>
        {
            var apiKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(apiKey) || !config.Auth.ParentApiKeys.Any(k => k.Key == apiKey))
                return Results.Json(new { error = "Invalid API key" }, statusCode: 401);

            var queries = queryLogger.GetRecentQueries(count ?? 100);
            return Results.Json(queries);
        });

        api.MapGet("/schema", async (HttpContext ctx) =>
        {
            var apiKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(apiKey) || !config.Auth.ParentApiKeys.Any(k => k.Key == apiKey))
                return Results.Json(new { error = "Invalid API key" }, statusCode: 401);

            if (schemaIntrospector == null)
                return Results.Json(new { error = "Schema introspection not available" }, statusCode: 503);

            try
            {
                var markdown = await schemaIntrospector.RenderMarkdownAsync();
                return Results.Text(markdown, "text/plain");
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Schema introspection failed: {ex.Message}" }, statusCode: 502);
            }
        });

        api.MapGet("/schema/{tableName}/annotations", async (string tableName, HttpContext ctx) =>
        {
            var apiKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(apiKey) || !config.Auth.ParentApiKeys.Any(k => k.Key == apiKey))
                return Results.Json(new { error = "Invalid API key" }, statusCode: 401);

            if (schemaIntrospector == null)
                return Results.Json(new { error = "Schema introspection not available" }, statusCode: 503);

            var annotations = await schemaIntrospector.GetAnnotationsAsync();
            if (annotations.TryGetValue(tableName, out var ann))
                return Results.Json(new { ann.Description, ann.ExampleQueries, ann.Notes });
            return Results.Json(new { Description = (string?)null, ExampleQueries = (string?)null, Notes = (string?)null });
        });

        api.MapPut("/schema/{tableName}/annotations", async (string tableName, SchemaAnnotationRequest request, HttpContext ctx) =>
        {
            var apiKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(apiKey) || !config.Auth.ParentApiKeys.Any(k => k.Key == apiKey))
                return Results.Json(new { error = "Invalid API key" }, statusCode: 401);

            if (schemaIntrospector == null)
                return Results.Json(new { error = "Schema introspection not available" }, statusCode: 503);

            await schemaIntrospector.SaveAnnotationAsync(tableName, request.Description, request.ExampleQueries, request.Notes);
            return Results.Ok(new { message = "Annotations saved" });
        });
    }
}

public class SchemaAnnotationRequest
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("exampleQueries")]
    public string? ExampleQueries { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public class CreateSessionRequest
{
    [JsonPropertyName("agentId")]
    public string AgentId { get; set; } = "";

    [JsonPropertyName("task")]
    public string Task { get; set; } = "";

    [JsonPropertyName("queryBudget")]
    public int? QueryBudget { get; set; }

    [JsonPropertyName("readOnly")]
    public bool? ReadOnly { get; set; }

    [JsonPropertyName("dangerousQueryMode")]
    public string? DangerousQueryMode { get; set; }

    [JsonPropertyName("allowedTables")]
    public List<string>? AllowedTables { get; set; }
}

public class CreateSessionResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; set; }

    [JsonPropertyName("connectionString")]
    public string ConnectionString { get; set; } = "";

    [JsonPropertyName("psqlCommand")]
    public string PsqlCommand { get; set; } = "";
}

public class SessionDto
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("agentId")]
    public string AgentId { get; set; } = "";

    [JsonPropertyName("task")]
    public string Task { get; set; } = "";

    [JsonPropertyName("queryBudget")]
    public int? QueryBudget { get; set; }

    [JsonPropertyName("queriesUsed")]
    public int QueriesUsed { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; set; }

    [JsonPropertyName("lastActivityAt")]
    public DateTime LastActivityAt { get; set; }

    [JsonPropertyName("isConnected")]
    public bool IsConnected { get; set; }

    [JsonPropertyName("isRevoked")]
    public bool IsRevoked { get; set; }

    [JsonPropertyName("isReadOnly")]
    public bool IsReadOnly { get; set; }

    [JsonPropertyName("dangerousQueryMode")]
    public string DangerousQueryMode { get; set; } = "block";

    [JsonPropertyName("allowedTables")]
    public List<string>? AllowedTables { get; set; }
}
